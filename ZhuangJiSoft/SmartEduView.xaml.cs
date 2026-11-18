using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskApp.Models;
using DeskApp.ViewModels;

namespace DeskApp;

/// <summary>
/// 国家中小学智慧教育平台模块：按站点教材页的学段/学科/版本/年级四级筛选，
/// 按条件联网拉取教材清单并本地过滤；勾选后批量下载 PDF。
/// </summary>
[ToolModule("smartedu", "国家中小学智慧教育平台", "🎓",
    "按站点筛选浏览并批量下载电子教材", Order = 123)]
public partial class SmartEduView : UserControl
{
    private bool _loaded;
    private SmartEduItem? _contextRecord;

    public SmartEduView()
    {
        InitializeComponent();
        VM = new SmartEduViewModel();
        DataContext = VM;
        VM.PropertyChanged += VM_PropertyChanged;
        VM.Load();
        _loaded = true;
        UpdateButtons();
    }

    private SmartEduViewModel VM { get; }

    private ComboBox[] FilterCombos =>
    [
        StageCombo, SubjectCombo, EditionCombo, GradeCombo
    ];

    private void VM_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(SmartEduViewModel.Busy) or
            nameof(SmartEduViewModel.PageIndex) or nameof(SmartEduViewModel.PageCount))
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
        RefreshBtn.IsEnabled = enabled;
        SelectAllBtn.IsEnabled = enabled;
        ClearSelectBtn.IsEnabled = enabled;
        PrevBtn.IsEnabled = enabled && VM.PageIndex > 1;
        NextBtn.IsEnabled = enabled && VM.PageIndex < VM.PageCount;
        DownloadBtn.IsEnabled = enabled && VM.HasToken;
        LoginBtn.IsEnabled = enabled;
        ManualTokenBtn.IsEnabled = enabled;
        ClearTokenBtn.IsEnabled = enabled;
    }

    private void Stage_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded && e.AddedItems.Count > 0)
        {
            VM.OnStageChanged();
        }
    }

    private void Subject_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded && e.AddedItems.Count > 0)
        {
            VM.OnSubjectChanged();
        }
    }

    private void Edition_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded && e.AddedItems.Count > 0)
        {
            VM.OnEditionChanged();
        }
    }

    private void Grade_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_loaded && e.AddedItems.Count > 0)
        {
            VM.ApplyFilters();
        }
    }

    private void Query_Click(object sender, RoutedEventArgs e) => VM.ApplyFilters();

    private void KeywordBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            VM.ApplyFilters();
        }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        await VM.RefreshCatalogAsync();
    }

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SmartEduLoginWindow { Owner = Window.GetWindow(this) };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.TokenJson))
        {
            var ok = VM.ApplyLoginToken(dlg.TokenJson, out var message);
            MessageBox.Show(Window.GetWindow(this), message, "国家中小学智慧教育平台",
                MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            UpdateButtons();
        }
    }

    private void ManualToken_Click(object sender, RoutedEventArgs e)
    {
        var input = new TokenInputDialog(VM.TokenJson) { Owner = Window.GetWindow(this) };
        if (input.ShowDialog() == true)
        {
            var ok = VM.SaveToken(input.TokenText, out var message);
            MessageBox.Show(Window.GetWindow(this), message, "国家中小学智慧教育平台",
                MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
            UpdateButtons();
        }
    }

    private void ClearToken_Click(object sender, RoutedEventArgs e)
    {
        var ok = VM.SaveToken(string.Empty, out var message);
        MessageBox.Show(Window.GetWindow(this), message, "国家中小学智慧教育平台",
            MessageBoxButton.OK, ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        UpdateButtons();
    }

    /// <summary>左键点行即切换勾选（CheckBox 不接收命中测试，避免重复切换）。</summary>
    private void Row_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (VM.Busy || sender is not DataGridRow row || row.Item is not SmartEduItem item)
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

        if (dep is DataGridRow row && row.Item is SmartEduItem item)
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

    private void OpenDetail_Click(object sender, RoutedEventArgs e)
    {
        if (_contextRecord is null || string.IsNullOrWhiteSpace(_contextRecord.DetailUrl))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = _contextRecord.DetailUrl,
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(Window.GetWindow(this), "打开详情页失败：" + ex.Message, "国家中小学智慧教育平台",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void SelectAll_Click(object sender, RoutedEventArgs e) => VM.SelectPage(true);

    private void ClearSelection_Click(object sender, RoutedEventArgs e) => VM.SelectPage(false);

    private void Prev_Click(object sender, RoutedEventArgs e) => VM.GoPage(VM.PageIndex - 1);

    private void Next_Click(object sender, RoutedEventArgs e) => VM.GoPage(VM.PageIndex + 1);

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择教材保存目录",
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
