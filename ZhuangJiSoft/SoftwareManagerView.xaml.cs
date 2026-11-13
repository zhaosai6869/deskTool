using System.Windows;
using System.Windows.Controls;
using DeskApp.Models;
using DeskApp.ViewModels;

namespace DeskApp;

/// <summary>
/// 装机软件模块（二级页面）。新增/修改用弹窗 SoftwareEditDialog；去掉了导入/导出/清空/排序。
/// </summary>
public partial class SoftwareManagerView : UserControl
{
    public SoftwareManagerView()
    {
        InitializeComponent();
        DataContext = new MainViewModel();
    }

    private MainViewModel VM => (MainViewModel)DataContext;

    private void Add_Click(object sender, RoutedEventArgs e)
    {
        var dlg = NewDialog();
        if (dlg.ShowDialog() == true)
        {
            VM.Add(dlg.SoftwareName, dlg.Url);
        }
    }

    /// <summary>弹窗归属主窗口，保证 CenterOwner 居中在主窗口中间</summary>
    private SoftwareEditDialog NewDialog() => new() { Owner = Window.GetWindow(this) };

    private SoftwareEditDialog NewDialog(SoftwareItem item) => new(item) { Owner = Window.GetWindow(this) };

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
            VM.Update(VM.SelectedItem, dlg.SoftwareName, dlg.Url);
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
}
