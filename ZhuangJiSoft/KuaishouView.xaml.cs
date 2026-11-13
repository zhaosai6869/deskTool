using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskApp.Utils;
using DeskApp.ViewModels;

namespace DeskApp;

/// <summary>
/// 快手下载模块（二级页面）：输入单条分享链接解析下载，表格展示进度与结果。
/// 分享页 INIT_STATE / APOLLO_STATE 协议解析，无需登录。
/// </summary>
public partial class KuaishouView : UserControl
{
    public KuaishouView()
    {
        InitializeComponent();
        DataContext = new KuaishouViewModel();
    }

    private KuaishouViewModel VM => (KuaishouViewModel)DataContext;

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        var raw = LinkBox.Text;
        // 只有真正开始解析下载才清空输入框；链接无效等提示后保留原地址，方便直接重试
        if (await VM.DownloadAsync(raw, Window.GetWindow(this)))
        {
            LinkBox.Clear();
        }
    }

    private void PickDir_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择快手下载保存目录",
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
