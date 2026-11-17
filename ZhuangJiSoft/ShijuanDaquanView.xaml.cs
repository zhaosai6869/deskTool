using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskApp.Models;
using DeskApp.ViewModels;

namespace DeskApp;

/// <summary>
/// 试卷大全下载模块：按站点 /list 的 8 类筛选 + 排序 + 关键词实时浏览，
/// 勾选批量下载 PDF；支持登录（40 套/天）与未登录（20 套/天），配额以站点数字为准。
/// 不建表、不做全站扫描，数据管理里没有对应表。
/// </summary>
[ToolModule("shijuandaquan", "试卷大全下载", "📄",
    "按站点筛选浏览并批量下载试卷", Order = 122)]
public partial class ShijuanDaquanView : UserControl
{
    private bool _loaded;
    private ShijuanDaquanItem? _contextRecord;

    public ShijuanDaquanView()
    {
        InitializeComponent();
        VM = new ShijuanDaquanViewModel();
        DataContext = VM;
        VM.PropertyChanged += VM_PropertyChanged;
        VM.Load();
        _loaded = true;
        UpdateButtons();
    }

    private ShijuanDaquanViewModel VM { get; }

    private ComboBox[] FilterCombos =>
    [
        SubjectCombo, GradeCombo, TermCombo, RegionCombo, YearCombo,
        LevelCombo, VersionCombo, TypeCombo, SortCombo
    ];

    private void VM_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ShijuanDaquanViewModel.Busy)
            or nameof(ShijuanDaquanViewModel.Remaining)
            or nameof(ShijuanDaquanViewModel.PageIndex)
            or nameof(ShijuanDaquanViewModel.PageCount))
        {
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        var enabled = !VM.Busy;
        foreach (var combo in FilterCombos)
        {
            combo.IsEnabled = enabled;
        }

        KeywordBox.IsEnabled = enabled;
        QueryBtn.IsEnabled = enabled;
        LoginBtn.IsEnabled = enabled;
        LogoutBtn.IsEnabled = enabled;
        SelectAllBtn.IsEnabled = enabled;
        ClearSelectBtn.IsEnabled = enabled;
        PrevBtn.IsEnabled = enabled && VM.PageIndex > 1;
        NextBtn.IsEnabled = enabled && VM.PageIndex < VM.PageCount;
        DownloadBtn.IsEnabled = enabled && VM.Remaining > 0;
    }

    private void Filter_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded || e.AddedItems.Count == 0)
        {
            return;
        }

        VM.ApplyFilters();
    }

    private void Query_Click(object sender, RoutedEventArgs e) => VM.ApplyFilters();

    private void KeywordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            Query_Click(sender, e);
        }
    }

    private async void Login_Click(object sender, RoutedEventArgs e)
    {
        await VM.LoginAsync(Window.GetWindow(this));
    }

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        await VM.LogoutAsync(Window.GetWindow(this));
    }

    /// <summary>左键点行即切换勾选（CheckBox 不接收命中测试，避免重复切换）。</summary>
    private void Row_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (VM.Busy || sender is not DataGridRow row || row.Item is not ShijuanDaquanItem item)
        {
            return;
        }

        item.IsSelected = !item.IsSelected;
        VM.RefreshSelectionCount();
    }

    private void PaperGrid_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        DependencyObject? dep = e.OriginalSource as DependencyObject;
        while (dep is not null && dep is not DataGridRow)
        {
            dep = VisualTreeHelper.GetParent(dep);
        }

        if (dep is DataGridRow row && row.Item is ShijuanDaquanItem item)
        {
            _contextRecord = item;
            PaperGrid.SelectedItem = item;
        }
        else
        {
            _contextRecord = null;
        }
    }

    private async void DownloadCurrent_Click(object sender, RoutedEventArgs e)
    {
        if (_contextRecord is null)
        {
            return;
        }

        VM.SaveSettings();
        await VM.DownloadOneAsync(_contextRecord, Window.GetWindow(this));
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => VM.SelectPage(true);

    private void ClearSelection_Click(object sender, RoutedEventArgs e) => VM.SelectPage(false);

    private void Prev_Click(object sender, RoutedEventArgs e) => VM.GoPage(VM.PageIndex - 1);

    private void Next_Click(object sender, RoutedEventArgs e) => VM.GoPage(VM.PageIndex + 1);

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择试卷保存目录",
            InitialDirectory = Directory.Exists(VM.DownloadDir) ? VM.DownloadDir : string.Empty
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
        {
            VM.DownloadDir = dlg.FolderName;
            VM.SaveSettings();
        }
    }

    private void OpenDir_Click(object sender, RoutedEventArgs e) => VM.OpenDownloadFolder();

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        VM.SaveSettings();
        await VM.DownloadSelectedAsync(Window.GetWindow(this));
    }
}
