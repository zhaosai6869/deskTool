using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskApp.Models;
using DeskApp.Utils;
using DeskApp.ViewModels;

namespace DeskApp;

/// <summary>
/// Win 远程模块（二级页面）。新增/修改用弹窗 WinRemoteEditDialog；双击行直接连接（拉起 mstsc）；
/// 行内 Ping 列显示延迟（绿色数字 / 红色"未连通"）；右键菜单可复制密码、复制信息、测试 Ping。
/// </summary>
[ToolModule("winremote", "win远程", "🖥", "保存远程桌面连接，一键连上主机",
    Order = 20, Tables = new string[] { "RdpRecords" })]
public partial class WinRemoteView : UserControl
{
    public WinRemoteView()
    {
        InitializeComponent();
        DataContext = new RdpViewModel();
    }

    private RdpViewModel VM => (RdpViewModel)DataContext;

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = NewDialog();
        if (dlg.ShowDialog() == true)
        {
            VM.Add(dlg.Record);
        }
    }

    private WinRemoteEditDialog NewDialog() => new() { Owner = Window.GetWindow(this) };

    private WinRemoteEditDialog NewDialog(RdpRecord item) => new(item) { Owner = Window.GetWindow(this) };

    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedItem is null)
        {
            PromptDialog.Show(Window.GetWindow(this), "请先选择一行再修改。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
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
        if (VM.SelectedItem is null)
        {
            return;
        }

        if (PromptDialog.Show(Window.GetWindow(this), $"确定删除「{VM.SelectedItem.Name}」？", "确认删除",
                MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes)
        {
            VM.Delete(VM.SelectedItem);
        }
    }

    /// <summary>双击行直接连接。</summary>
    private void Grid_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (FindRow(e.OriginalSource)?.Item is RdpRecord rec)
        {
            VM.Connect(rec, Window.GetWindow(this));
        }
    }

    /// <summary>右键点击时选中所在行，供上下文菜单针对该记录操作。</summary>
    private void Grid_MouseRightButtonDown(object sender, MouseButtonEventArgs e)
    {
        var row = FindRow(e.OriginalSource);
        if (row is not null)
        {
            Grid.SelectedItem = row.Item;
        }
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

    /// <summary>顶部"Ping 值"按钮：测试全部。</summary>
    private void Ping_Click(object sender, RoutedEventArgs e) => VM.PingAllAsync();

    /// <summary>右键菜单：测试单条 Ping。</summary>
    private void TestPingOne_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedItem is not null)
        {
            VM.PingOneAsync(VM.SelectedItem);
        }
    }

    /// <summary>右键菜单：复制明文密码到剪贴板。</summary>
    private void CopyPassword_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedItem is null)
        {
            return;
        }

        var pwd = RdpCrypto.Decrypt(VM.SelectedItem.PasswordEncrypted);
        if (string.IsNullOrEmpty(pwd))
        {
            PromptDialog.Show(Window.GetWindow(this), "该记录未设置密码。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Clipboard.SetText(pwd);
    }

    /// <summary>右键菜单：复制信息（名称、主机、端口、用户名、密码）。</summary>
    private void CopyInfo_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedItem is null)
        {
            return;
        }

        var r = VM.SelectedItem;
        var pwd = RdpCrypto.Decrypt(r.PasswordEncrypted);
        var text = $"名称：{r.Name}\n主机：{r.Host}\n端口：{r.Port}\n用户名：{r.UserName}\n密码：{pwd ?? "未设置"}";
        Clipboard.SetText(text);
    }
}
