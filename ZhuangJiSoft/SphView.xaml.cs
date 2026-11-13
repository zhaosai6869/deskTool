using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskApp.Utils;
using DeskApp.ViewModels;

namespace DeskApp;

/// <summary>
/// 视频号下载模块（二级页面）：输入单条分享链接解析下载，表格展示进度与结果。
/// 参考 QinGeneral/wx-downloader 的交互思路重构（元宝登录 + 链接解析 + 去重下载）。
/// </summary>
[ToolModule("wechat_channels", "视频号下载", "🎬", "粘贴视频号分享链接，解析下载",
    Order = 100, Tables = new string[] { "SphRecords" })]
public partial class SphView : UserControl
{
    public SphView()
    {
        InitializeComponent();
        DataContext = new SphViewModel();
    }

    private SphViewModel VM => (SphViewModel)DataContext;

    private void Login_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new YuanbaoLoginWindow
        {
            Owner = Window.GetWindow(this)
        };
        dlg.ShowDialog();
        if (dlg.Success)
        {
            VM.RefreshLoginText();
        }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var raw = LinkBox.Text;
        // 只有真正开始解析下载才清空输入框；未登录 / 链接无效等提示后保留原地址，方便直接重试
        if (await VM.DownloadAsync(raw, Window.GetWindow(this)))
        {
            LinkBox.Clear();
        }
    }

    private void PickDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择视频号下载保存目录",
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
        if (PromptDialog.Show(Window.GetWindow(this), "清空全部下载记录？已下载的文件不会被删除。",
                "确认清空", MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            VM.ClearHistory();
        }
    }

    /// <summary>退出元宝登录：删除本地保存的凭据（不动下载记录与已下载文件）。</summary>
    private void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (YuanbaoAuth.Load() is null)
        {
            PromptDialog.Show(Window.GetWindow(this), "当前未登录元宝，无需退出。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (PromptDialog.Show(Window.GetWindow(this), "退出元宝登录？退出后需要重新登录才能解析视频号链接（下载记录与文件不受影响）。",
                "确认退出", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        YuanbaoAuth.Clear();
        VM.RefreshLoginText();
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

        var path = Path.Combine(VM.Config.DownloadDir, VM.SelectedItem.FileName);
        if (!string.IsNullOrEmpty(VM.SelectedItem.FileName) && File.Exists(path))
        {
            // 走 Shell API，避免直接启动 explorer.exe 被 360 判为可疑操作
            ShellHelper.OpenAndSelect(path);
        }
        else
        {
            Directory.CreateDirectory(VM.Config.DownloadDir);
            ShellHelper.OpenAndSelect(VM.Config.DownloadDir);
        }
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
