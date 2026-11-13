using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskApp.Models;
using DeskApp.Utils;
using DeskApp.ViewModels;

namespace DeskApp;

/// <summary>
/// ToDesk 模块（二级页面）。与 Win 远程交互一致：新增/修改弹窗、双击行连接、右键复制；
/// 不含任何 Ping 相关功能（设备代码不是 IP）。
/// </summary>
public partial class ToDeskView : UserControl
{
    public ToDeskView()
    {
        InitializeComponent();
        DataContext = new ToDeskViewModel();
    }

    private ToDeskViewModel VM => (ToDeskViewModel)DataContext;

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = NewDialog();
        if (dlg.ShowDialog() == true)
        {
            VM.Add(dlg.Record);
        }
    }

    private ToDeskEditDialog NewDialog() => new() { Owner = Window.GetWindow(this) };

    private ToDeskEditDialog NewDialog(ToDeskRecord item) => new(item) { Owner = Window.GetWindow(this) };

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
        if (FindRow(e.OriginalSource)?.Item is ToDeskRecord rec)
        {
            _ = VM.Connect(rec, Window.GetWindow(this));
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
            PromptDialog.Show(Window.GetWindow(this), "该记录未设置连接密码。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Clipboard.SetText(pwd);
    }

    /// <summary>右键菜单：复制信息（名称、设备代码、密码）。</summary>
    private void CopyInfo_Click(object sender, RoutedEventArgs e)
    {
        if (VM.SelectedItem is null)
        {
            return;
        }

        var r = VM.SelectedItem;
        var pwd = RdpCrypto.Decrypt(r.PasswordEncrypted);
        var text = $"名称：{r.Name}\n设备代码：{r.DeviceId}\n连接密码：{pwd ?? "未设置"}";
        if (!string.IsNullOrWhiteSpace(r.Notes))
        {
            text += $"\n备注：{r.Notes}";
        }

        Clipboard.SetText(text);
    }
}
