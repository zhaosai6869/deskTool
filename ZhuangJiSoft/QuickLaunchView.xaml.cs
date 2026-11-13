using System;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskApp.Models;
using DeskApp.ViewModels;

namespace DeskApp;

/// <summary>
/// 一键启动模块（二级页面）：模式增删改 + 应用下拉/浏览添加 + 单击启动整组程序。
/// </summary>
public partial class QuickLaunchView : UserControl
{
    public QuickLaunchView()
    {
        InitializeComponent();
        DataContext = new QuickLaunchViewModel();
    }

    private QuickLaunchViewModel VM => (QuickLaunchViewModel)DataContext;

    private void AddMode_Click(object sender, RoutedEventArgs e)
    {
        VM.AddMode(Window.GetWindow(this));
    }

    private void RenameMode_Click(object sender, RoutedEventArgs e)
    {
        VM.RenameMode(Window.GetWindow(this));
    }

    private void DeleteMode_Click(object sender, RoutedEventArgs e)
    {
        VM.DeleteMode(Window.GetWindow(this));
    }

    private void LaunchAll_Click(object sender, RoutedEventArgs e)
    {
        var (_, failed, failedNames) = VM.LaunchAll(Window.GetWindow(this));
        // 只在有失败时提示；全部启动成功保持静默（空模式的提示由 VM 内部给出）
        if (failed > 0)
        {
            PromptDialog.Show(Window.GetWindow(this),
                $"有 {failed} 个程序启动失败：{string.Join("、", failedNames)}",
                "一键启动", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void AddApp_Click(object sender, RoutedEventArgs e)
    {
        VM.AddSelectedApp(Window.GetWindow(this));
    }

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选择程序（exe 或 lnk）",
            Filter = "程序|*.exe;*.lnk"
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
        {
            VM.AddBrowsedApp(Window.GetWindow(this), dlg.FileName);
        }
    }

    private void LaunchOne_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is LauncherItem item)
        {
            if (!VM.LaunchOne(item))
            {
                PromptDialog.Show(Window.GetWindow(this), $"启动失败：{item.Name}", "提示",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private void RemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (Grid.SelectedItem is LauncherItem item)
        {
            VM.RemoveItem(item);
        }
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
