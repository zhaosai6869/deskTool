using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeskApp.ViewModels;

namespace DeskApp;

/// <summary>
/// 漫画下载模块：粘贴目录页网址，通用规则抓图打包 CBZ/PDF。
/// 不绑定任何站点；识别不准时可用 CSS 选择器校正。
/// </summary>
[ToolModule("comic", "漫画下载", "📚",
    "粘贴目录页网址，通用规则抓图打包 CBZ/PDF", Order = 124)]
public partial class ComicView : UserControl
{
    public ComicView()
    {
        InitializeComponent();
        VM = new ComicViewModel();
        DataContext = VM;
        VM.PropertyChanged += VM_PropertyChanged;
        Unloaded += (_, _) => VM.Cancel();
        UpdateButtons();
    }

    private ComicViewModel VM { get; }

    private void VM_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(ComicViewModel.Busy) or nameof(ComicViewModel.Progress))
        {
            UpdateButtons();
        }
    }

    private void UpdateButtons()
    {
        var enabled = !VM.Busy;
        UrlBox.IsEnabled = enabled;
        ParseBtn.IsEnabled = enabled;
        SelectAllBtn.IsEnabled = enabled;
        UnselectBtn.IsEnabled = enabled;
        DownloadBtn.IsEnabled = enabled && VM.HasChapters;
        BrowseBtn.IsEnabled = enabled;
        CancelBtn.IsEnabled = VM.Busy;
    }

    private async void Parse_Click(object sender, RoutedEventArgs e)
    {
        await VM.ParseAsync(Window.GetWindow(this));
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !VM.Busy)
        {
            _ = VM.ParseAsync(Window.GetWindow(this));
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => VM.Cancel();

    private void SelectAll_Click(object sender, RoutedEventArgs e) => VM.SelectAll(true);

    private void Unselect_Click(object sender, RoutedEventArgs e) => VM.SelectAll(false);

    /// <summary>整行点击切换勾选（CheckBox 不接收命中测试）。</summary>
    private void Row_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (VM.Busy || sender is not DataGridRow row || row.Item is not Models.ComicChapter ch)
        {
            return;
        }
        ch.IsSelected = !ch.IsSelected;
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        VM.SaveSettings();
        await VM.DownloadAsync(Window.GetWindow(this));
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择漫画保存目录",
            InitialDirectory = Directory.Exists(VM.DownloadDir) ? VM.DownloadDir : string.Empty
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
        {
            VM.DownloadDir = dlg.FolderName;
            VM.SaveSettings();
        }
    }

    private void OpenDir_Click(object sender, RoutedEventArgs e) => VM.OpenDownloadFolder();
}
