using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeskApp.ViewModels;

namespace DeskApp;

/// <summary>
/// 漫画下载模块：按站点独立解析，抓图打包 CBZ/PDF。
/// 支持站点固定为内置 8 家，不提供站点增删改。
/// </summary>
[ToolModule("comic", "漫画下载", "📚",
    "按站点独立解析章节图片，打包 CBZ/PDF", Order = 124)]
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

    /// <summary>保存 B 漫 Cookie（SESSDATA）到配置。</summary>
    private void SaveCookie_Click(object sender, RoutedEventArgs e)
    {
        VM.SaveSettings();
        VM.StatusText = string.IsNullOrWhiteSpace(VM.BiliCookie)
            ? "B漫 Cookie 已清空（哔哩哔哩漫画将无法解析）"
            : "B漫 Cookie 已保存";
    }

    /// <summary>打开内嵌 B 漫登录窗口，扫码/账密登录后自动回填 SESSDATA。</summary>
    private void Login_Click(object sender, RoutedEventArgs e)
    {
        var owner = Window.GetWindow(this);
        var dlg = new ComicLoginWindow();
        if (owner is not null) dlg.Owner = owner;
        VM.StatusText = "正在打开哔哩哔哩漫画登录页…";
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.SessData))
        {
            VM.BiliCookie = dlg.SessData;
            VM.SaveSettings();
            VM.StatusText = "B漫登录成功，SESSDATA 已保存（约 30 天有效，过期重新点「登录」即可）";
        }
        else
        {
            VM.StatusText = "已取消 B漫登录";
        }
    }

    private void Site_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is Models.ComicSite site)
        {
            VM.OpenSite(site, Window.GetWindow(this));
        }
    }
}
