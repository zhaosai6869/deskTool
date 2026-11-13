using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using DeskApp.Models;
using DeskApp.Services;

namespace DeskApp;

/// <summary>
/// 卡片式首页。列表不写死：从 ModuleCatalog（反射扫描 [ToolModule]）取全部模块，
/// 再按 ModuleSettings 过滤出已勾选的，末尾追加一张「＋」卡片。
/// 设置窗口关闭后调用 RefreshModules() 即可立即反映勾选变化。
/// </summary>
public partial class HomeView : UserControl
{
    public const string AddCardKey = "__add__";

    public ObservableCollection<ToolEntry> Tools { get; } = new();

    public HomeView()
    {
        InitializeComponent();
        DataContext = this;
        RefreshModules();
    }

    /// <summary>重建首页卡片（启用状态变化后调用）</summary>
    public void RefreshModules()
    {
        var settings = ModuleSettings.Instance;
        settings.EnsureDefaults(ModuleCatalog.All);

        Tools.Clear();
        foreach (var module in ModuleCatalog.All.Where(m => settings.IsEnabled(m.Key)))
        {
            Tools.Add(new ToolEntry
            {
                Key = module.Key,
                Name = module.Name,
                Icon = module.Icon,
                Description = module.Description
            });
        }

        // 最后一张「＋」卡片：直接进入设置 → 模块管理
        Tools.Add(new ToolEntry
        {
            Key = AddCardKey,
            Name = "添加模块",
            Icon = "＋",
            Description = "前往设置，勾选要显示的功能"
        });
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not ToolEntry tool ||
            Window.GetWindow(this) is not LauncherWindow w)
        {
            return;
        }

        if (tool.Key == AddCardKey)
        {
            w.OpenSettings(SettingsWindow.ModulesPanel);
            return;
        }

        w.NavigateModule(tool.Key);
    }
}
