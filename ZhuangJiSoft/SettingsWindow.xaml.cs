using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskApp.Data;
using DeskApp.Models;
using DeskApp.Services;
using DeskApp.Utils;
using Microsoft.EntityFrameworkCore;

namespace DeskApp;

/// <summary>
/// 设置弹窗：常规 / 模块管理 / 数据管理 / 关于·更新。
/// 三个模块列表全部由 ModuleCatalog 反射生成，新增模块无需改这里。
/// </summary>
public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    public const int GeneralPanel = 0;
    public const int ModulesPanel = 1;
    public const int DataPanel = 2;
    public const int AboutPanel = 3;
    public const int HelpPanel = 4;

    /// <summary>打开设置时显示的面板（首页「＋」卡片会传 ModulesPanel）</summary>
    public int InitialPanel { get; set; } = ModulesPanel;

    /// <summary>模块管理：是否显示在主界面</summary>
    public ObservableCollection<ModuleEntry> ModuleToggles { get; } = new();

    /// <summary>数据管理：是否清空该模块数据</summary>
    public ObservableCollection<ModuleEntry> Modules { get; } = new();

    /// <summary>使用说明：按模块自动生成的文档列表</summary>
    public ObservableCollection<HelpService.Doc> HelpDocs { get; } = new();

    private bool _syncing;
    private bool _busy;
    private UpdateInfo? _update;

    public SettingsWindow()
    {
        InitializeComponent();
        DataContext = this;

        var settings = ModuleSettings.Instance;
        settings.EnsureDefaults(ModuleCatalog.All);
        foreach (var module in settings.GetOrderedModules())
        {
            var toggle = MakeEntry(module, settings.IsEnabled(module.Key));
            toggle.PropertyChanged += OnToggleChanged;
            ModuleToggles.Add(toggle);

            var data = MakeEntry(module, false);
            data.PropertyChanged += OnDataCheckChanged;
            Modules.Add(data);
        }

        SyncMoveState();
        VersionText.Text = $"版本 {UpdateService.CurrentVersionText}";

        foreach (var doc in HelpService.BuildList())
        {
            HelpDocs.Add(doc);
        }

        SetNav(InitialPanel);
        ShowPanel(InitialPanel);

        if (HelpDocs.Count > 0 && HelpModuleList.SelectedIndex < 0)
        {
            HelpModuleList.SelectedIndex = 0;
        }
    }

    private void HelpModule_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HelpModuleList.SelectedItem is not HelpService.Doc doc)
        {
            HelpViewer.Document = HelpRenderer.Render(null);
            return;
        }
        HelpViewer.Document = HelpRenderer.Render(doc.Markdown);
    }

    private void OpenHelp_Click(object sender, RoutedEventArgs e)
    {
        NavHelp.IsChecked = true;
    }

    private static ModuleEntry MakeEntry(ModuleInfo module, bool isChecked) => new()
    {
        Key = module.Key,
        Name = module.Name,
        Icon = module.Icon,
        Description = module.Description,
        Tables = module.Tables,
        IsChecked = isChecked
    };

    private void OnToggleChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ModuleEntry.IsChecked) || sender is not ModuleEntry entry)
        {
            return;
        }

        ModuleSettings.Instance.SetEnabled(entry.Key, entry.IsChecked);
    }

    /// <summary>↑↓：在列表里移动一项，并立刻把新顺序写回配置</summary>
    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveModule(sender, -1);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveModule(sender, 1);

    private void MoveModule(object sender, int delta)
    {
        if ((sender as Button)?.DataContext is not ModuleEntry entry)
        {
            return;
        }

        var index = ModuleToggles.IndexOf(entry);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= ModuleToggles.Count)
        {
            return;
        }

        ModuleToggles.Move(index, target);

        // 数据管理列表保持同一顺序，避免两个面板显示次序不一致
        var dataIndex = Modules.Select(m => m.Key).ToList().IndexOf(entry.Key);
        if (dataIndex >= 0 && dataIndex != target)
        {
            Modules.Move(dataIndex, target);
        }

        ModuleSettings.Instance.SetOrder(ModuleToggles.Select(m => m.Key));
        SyncMoveState();
    }

    private void ResetOrder_Click(object sender, RoutedEventArgs e)
    {
        var settings = ModuleSettings.Instance;
        settings.ResetOrder();

        // 重建两个列表，保留各自的勾选状态
        var toggleState = ModuleToggles.ToDictionary(m => m.Key, m => m.IsChecked,
            StringComparer.OrdinalIgnoreCase);
        var dataState = Modules.ToDictionary(m => m.Key, m => m.IsChecked,
            StringComparer.OrdinalIgnoreCase);

        _syncing = true;
        ModuleToggles.Clear();
        Modules.Clear();
        foreach (var module in settings.GetOrderedModules())
        {
            var toggle = MakeEntry(module, toggleState.TryGetValue(module.Key, out var on)
                ? on
                : settings.IsEnabled(module.Key));
            toggle.PropertyChanged += OnToggleChanged;
            ModuleToggles.Add(toggle);

            var data = MakeEntry(module, dataState.TryGetValue(module.Key, out var dc) && dc);
            data.PropertyChanged += OnDataCheckChanged;
            Modules.Add(data);
        }

        // _syncing 期间同步全选框，避免 SelectAll_Changed 反过来把刚恢复的勾选清掉
        SelectAllBox.IsChecked = Modules.All(m => m.IsChecked);
        _syncing = false;
        SyncMoveState();
    }

    private void SyncMoveState()
    {
        for (var i = 0; i < ModuleToggles.Count; i++)
        {
            ModuleToggles[i].CanMoveUp = i > 0;
            ModuleToggles[i].CanMoveDown = i < ModuleToggles.Count - 1;
        }
    }

    private void OnDataCheckChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_syncing || e.PropertyName != nameof(ModuleEntry.IsChecked))
        {
            return;
        }

        _syncing = true;
        SelectAllBox.IsChecked = Modules.All(m => m.IsChecked);
        _syncing = false;
    }

    private void SelectAll_Changed(object sender, RoutedEventArgs e)
    {
        if (_syncing)
        {
            return;
        }

        _syncing = true;
        var on = SelectAllBox.IsChecked == true;
        foreach (var module in Modules)
        {
            module.IsChecked = on;
        }

        _syncing = false;
    }

    private void SetNav(int idx)
    {
        NavGeneral.IsChecked = idx == GeneralPanel;
        NavModules.IsChecked = idx == ModulesPanel;
        NavData.IsChecked = idx == DataPanel;
        NavAbout.IsChecked = idx == AboutPanel;
        NavHelp.IsChecked = idx == HelpPanel;
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && int.TryParse(rb.Tag?.ToString(), out var idx))
        {
            ShowPanel(idx);
        }
    }

    private void ShowPanel(int idx)
    {
        if (PanelModules is null)
        {
            return; // XAML 初始化期间 Checked 可能提前触发
        }

        PanelGeneral.Visibility = idx == GeneralPanel ? Visibility.Visible : Visibility.Collapsed;
        PanelModules.Visibility = idx == ModulesPanel ? Visibility.Visible : Visibility.Collapsed;
        PanelData.Visibility = idx == DataPanel ? Visibility.Visible : Visibility.Collapsed;
        PanelAbout.Visibility = idx == AboutPanel ? Visibility.Visible : Visibility.Collapsed;
        PanelHelp.Visibility = idx == HelpPanel ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var selected = Modules.Where(m => m.IsChecked).ToList();
        if (selected.Count == 0)
        {
            PromptDialog.Show(this, "请先勾选要清空的模块。", "提示",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        var names = string.Join("、", selected.Select(m => m.Name));
        if (PromptDialog.Show(this, $"确定清空以下模块的数据？\n{names}\n此操作不可恢复！", "确认清空",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning)
            != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            using var db = new AppDbContext();
            var tables = selected.SelectMany(m => m.Tables)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            foreach (var table in tables)
            {
                // 表名只能来自 [ToolModule] 特性常量；再做一次字符白名单校验，杜绝任何注入可能。
                // 表名无法用 SQL 参数占位，因此这里显式校验后拼接，而不是用 ExecuteSqlRaw 的插值重载。
                if (string.IsNullOrWhiteSpace(table) ||
                    !table.All(ch => char.IsLetterOrDigit(ch) || ch == '_'))
                {
                    continue;
                }

                var sql = "DELETE FROM \"" + table + "\"";
                db.Database.ExecuteSqlRaw(sql);
            }

            PromptDialog.Show(this, "已清空选中模块的数据。", "完成",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            foreach (var module in selected)
            {
                module.IsChecked = false;
            }
        }
        catch (Exception ex)
        {
            PromptDialog.Show(this, $"清空失败：{ex.Message}", "错误",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
        }
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (_busy)
        {
            return;
        }

        _busy = true;
        _update = null;
        CheckUpdateBtn.IsEnabled = false;
        UpdateBtn.Visibility = Visibility.Collapsed;
        UpdateStatus.Text = "正在检查更新…";
        ReleaseNotes.Text = "正在向 GitHub 查询最新版本…";

        try
        {
            var latest = await UpdateService.CheckLatestAsync();
            if (latest is null)
            {
                UpdateStatus.Text = "暂无可用版本";
                ReleaseNotes.Text =
                    "仓库还没有发布过 Release。\n\n发布方法：在 GitHub 创建 Release（标签形如 v1.0.1），" +
                    "上传包含 DeskApp.exe 与全部依赖文件的 zip 附件，再回来点「检查更新」。";
            }
            else if (latest.Version <= UpdateService.CurrentVersion)
            {
                UpdateStatus.Text = $"已是最新版本 {UpdateService.CurrentVersionText}";
                ReleaseNotes.Text = string.IsNullOrWhiteSpace(latest.Notes)
                    ? "（该版本暂无更新说明）"
                    : latest.Notes;
            }
            else
            {
                _update = latest;
                UpdateStatus.Text = $"发现新版本 {latest.VersionText}（{latest.PublishedAt:yyyy-MM-dd}）";
                var size = latest.Size > 0 ? $"（{latest.Size / 1024.0 / 1024.0:F1} MB）" : string.Empty;
                ReleaseNotes.Text =
                    (string.IsNullOrWhiteSpace(latest.Notes) ? "（该版本暂无更新说明）" : latest.Notes) +
                    $"\n\n更新包：{latest.AssetName}{size}";
                UpdateBtn.Visibility = Visibility.Visible;
            }
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "检查失败";
            ReleaseNotes.Text = $"无法获取更新信息：{ex.Message}\n\n" +
                                $"请检查网络或代理设置，也可直接打开 {UpdateService.ReleasesPage} 查看。";
        }
        finally
        {
            _busy = false;
            CheckUpdateBtn.IsEnabled = true;
        }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (_update is null || _busy)
        {
            return;
        }

        if (PromptDialog.Show(this,
                $"下载 {_update.VersionText} 并自动更新？\n\n更新会替换程序文件并自动重启，已有的数据不会丢失。",
                "确认更新", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        _busy = true;
        CheckUpdateBtn.IsEnabled = false;
        UpdateBtn.IsEnabled = false;

        try
        {
            var stage = await UpdateService.DownloadAsync(_update,
                message => Dispatcher.Invoke(() => UpdateStatus.Text = message));
            UpdateStatus.Text = "正在重启并完成更新…";
            UpdateService.LaunchUpdaterAndExit(stage);
        }
        catch (Exception ex)
        {
            UpdateStatus.Text = "更新失败";
            ReleaseNotes.Text = $"更新未完成：{ex.Message}\n\n" +
                                $"可到 {UpdateService.ReleasesPage} 手动下载。";
            _busy = false;
            CheckUpdateBtn.IsEnabled = true;
            UpdateBtn.IsEnabled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>标题栏空白处拖动窗口；点到按钮时不拖动，避免吞掉点击</summary>
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject d)
        {
            var cur = d;
            while (cur is not null)
            {
                if (cur is System.Windows.Controls.Button or Wpf.Ui.Controls.Button)
                {
                    return;
                }
                cur = VisualTreeHelper.GetParent(cur);
            }
        }

        DragMove();
    }
}
