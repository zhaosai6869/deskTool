using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskApp.Models;
using DeskApp.Utils;
using DeskApp.ViewModels;

namespace DeskApp;

/// <summary>
/// SSH / FTP 模块（二级页面）。列表页与 Win 远程保持一致的交互：
/// 双击行直接连接、Ping 列红绿两态、右键菜单复制/测 Ping、顶部 Ping 值按钮测全部。
/// 差异：SSH 记录双击打开内嵌终端（含 SFTP 文件管理入口）；FTP 记录双击直接打开文件管理。
/// </summary>
public partial class SshRemoteView : UserControl
{
    public SshRemoteView()
    {
        InitializeComponent();
        DataContext = new SshViewModel();
    }

    private SshViewModel VM => (SshViewModel)DataContext;

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = NewDialog();
        if (dlg.ShowDialog() == true)
        {
            VM.Add(dlg.Record);
        }
    }

    private SshEditDialog NewDialog() => new() { Owner = Window.GetWindow(this) };

    private SshEditDialog NewDialog(SshRecord item) => new(item) { Owner = Window.GetWindow(this) };

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedItem is null)
        {
            PromptDialog.Show(Window.GetWindow(this), "请先选择一行再修改。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var dlg = NewDialog(VM.SelectedItem);
        if (dlg.ShowDialog() == true)
        {
            VM.Update(VM.SelectedItem, dlg.Record);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedItem is null) return;

        if (PromptDialog.Show(Window.GetWindow(this), $"确定删除「{VM.SelectedItem.Name}」？", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            VM.Delete(VM.SelectedItem);
        }
    }

    /// <summary>双击行直接连接：SSH → 终端窗口；FTP → 文件管理窗口。</summary>
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindRow(e.OriginalSource)?.Item is not SshRecord rec) return;

        if (rec.IsFtp)
        {
            OpenFileManager(rec);
        }
        else
        {
            OpenTerminal(rec);
        }
    }

    private void Grid_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindRow(e.OriginalSource);
        if (row is not null) Grid.SelectedItem = row.Item;
    }

    private static DataGridRow? FindRow(object source)
    {
        var dep = source as DependencyObject;
        while (dep is not null && dep is not DataGridRow)
        {
            dep = VisualTreeHelper.GetParent(dep);
        }
        return dep as DataGridRow;
    }

    private void Ping_Click(object sender, RoutedEventArgs e) => VM.PingAllAsync();

    private void TestPingOne_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedItem is not null) VM.PingOneAsync(VM.SelectedItem);
    }

    private void OpenTerminal_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedItem is null) return;
        if (VM.SelectedItem.IsFtp)
        {
            PromptDialog.Show(Window.GetWindow(this), "FTP 连接没有终端，请改用「文件管理」。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenTerminal(VM.SelectedItem);
    }

    private void FileManager_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedItem is null)
        {
            PromptDialog.Show(Window.GetWindow(this), "请先选择一条记录再打开文件管理。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        OpenFileManager(VM.SelectedItem);
    }

    private void OpenTerminal(SshRecord rec)
    {
        var win = new SshTerminalWindow(SshViewModel.CreateConnectionInfo(rec));
        win.Show();
    }

    private void OpenFileManager(SshRecord rec)
    {
        var win = new SshFileManagerWindow(SshViewModel.CreateConnectionInfo(rec));
        win.Show();
    }

    private void CopyPassword_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedItem is null) return;
        var pwd = RdpCrypto.Decrypt(VM.SelectedItem.PasswordEncrypted);
        if (string.IsNullOrEmpty(pwd))
        {
            PromptDialog.Show(Window.GetWindow(this), "该记录未设置密码。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        Clipboard.SetText(pwd);
    }

    private void CopyInfo_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedItem is null) return;
        var r = VM.SelectedItem;
        var pwd = RdpCrypto.Decrypt(r.PasswordEncrypted);
        var text = $"名称：{r.Name}\n协议：{r.Protocol}\n主机：{r.Host}\n端口：{r.Port}\n" +
                   $"用户名：{r.UserName}\n密码：{pwd ?? "未设置"}";
        Clipboard.SetText(text);
    }
}
