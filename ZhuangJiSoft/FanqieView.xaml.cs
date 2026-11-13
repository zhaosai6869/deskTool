using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskApp.Utils;
using DeskApp.ViewModels;

namespace DeskApp;

/// <summary>
/// 番茄小说下载模块（二级页面）：按名称搜索（WebView2 过滑块）或输入书号/目录链接，逐章下载并解码导出 TXT。
/// </summary>
[ToolModule("fanqie", "番茄小说", "📖", "按名称搜索或书号直达，下载 TXT",
    Order = 110, Tables = new string[] { "FanqieRecords" })]
public partial class FanqieView : UserControl
{
    public FanqieView()
    {
        InitializeComponent();
        DataContext = new FanqieViewModel();
        VM.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(FanqieViewModel.Busy))
            {
                DownloadBtn.IsEnabled = !VM.Busy;
                CancelBtn.IsEnabled = VM.Busy;
            }
        };
        Unloaded += (_, _) => VM.Cancel();
    }

    private FanqieViewModel VM => (FanqieViewModel)DataContext;

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var raw = LinkBox.Text;
        // 只有真正开始处理才清空输入框；校验拦下（无效输入）时保留原文方便直接改
        if (await VM.DownloadAsync(raw, Window.GetWindow(this)))
        {
            LinkBox.Clear();
        }
    }

    private void Search_Click(object sender, RoutedEventArgs e)
    {
        if (VM.Busy)
        {
            PromptDialog.Show(Window.GetWindow(this), "已有下载任务在进行中，请等待完成或先取消。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var keyword = LinkBox.Text.Trim();
        var win = new FanqieSearchWindow(keyword)
        {
            Owner = Window.GetWindow(this),
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        // 「下载当前书」不关闭搜索窗：直接把书号交给主 ViewModel 后台下载
        win.DownloadRequested = bookId =>
        {
            if (VM.Busy)
            {
                return false;
            }

            _ = VM.DownloadAsync(bookId, win);
            return true;
        };
        win.ShowDialog();

        if (win.SelectedResult is not null)
        {
            LinkBox.Clear();
            _ = VM.DownloadBySearchResultAsync(win.SelectedResult, Window.GetWindow(this));
        }
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        VM.Cancel();
    }

    private void PickDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择番茄小说下载保存目录",
            InitialDirectory = Directory.Exists(VM.Config.DownloadDir) ? VM.Config.DownloadDir : string.Empty
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
        {
            VM.Config.DownloadDir = dlg.FolderName;
            VM.Config.Save();
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        if (PromptDialog.Show(Window.GetWindow(this), "清空全部下载记录？已下载的 TXT 文件不会被删除。",
                "确认清空", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            VM.ClearHistory();
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedItem is null)
        {
            return;
        }

        await VM.RetryAsync(VM.SelectedItem);
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedItem is null)
        {
            return;
        }

        if (!string.IsNullOrEmpty(VM.SelectedItem.FileName) &&
            VM.SelectedItem.FileName.EndsWith(".txt", System.StringComparison.OrdinalIgnoreCase))
        {
            var path = Path.Combine(VM.Config.DownloadDir, VM.SelectedItem.FileName);
            if (File.Exists(path))
            {
                ShellHelper.OpenAndSelect(path);
                return;
            }
        }

        Directory.CreateDirectory(VM.Config.DownloadDir);
        ShellHelper.OpenAndSelect(VM.Config.DownloadDir);
    }

    private void CopyLink_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedItem is null)
        {
            return;
        }

        Clipboard.SetText(VM.SelectedItem.SourceUrl);
    }

    private void Grid_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var dep = e.OriginalSource as DependencyObject;
        while (dep is not null && dep is not DataGridRow)
        {
            dep = VisualTreeHelper.GetParent(dep);
        }

        if (dep is DataGridRow row)
        {
            Grid.SelectedItem = row.Item;
        }
    }
}
