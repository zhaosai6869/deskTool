using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskApp.Data;
using DeskApp.Models;

namespace DeskApp;

/// <summary>
/// 设置弹窗：与主窗口同风格（FluentWindow + Mica + 自绘标题栏）。
/// 导航用自定义 RadioButton，复选框用自定义模板，均避免 WPF-UI 隐式样式吃掉可见性。
/// 目前实际功能只有「数据管理」（模块多选清空）；常规/关于为占位。
/// </summary>
public partial class SettingsWindow : Wpf.Ui.Controls.FluentWindow
{
    public List<ModuleDataEntry> Modules { get; } = new()
    {
        new ModuleDataEntry
        {
            Key = "software",
            Name = "装机软件",
            Clear = () =>
            {
                using var db = new AppDbContext();
                db.Items.RemoveRange(db.Items);
                db.SaveChanges();
            }
        }
    };

    public SettingsWindow()
    {
        InitializeComponent();
        DataContext = this;
        ShowPanel(1); // 默认显示「数据管理」（当前主功能）
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (sender is RadioButton rb && int.TryParse(rb.Tag?.ToString(), out var idx))
        {
            ShowPanel(idx);
        }
    }

    private void ShowPanel(int idx)
    {
        if (PanelGeneral is null)
        {
            return; // XAML 初始化期间 Checked 可能提前触发
        }

        PanelGeneral.Visibility = idx == 0 ? Visibility.Visible : Visibility.Collapsed;
        PanelData.Visibility = idx == 1 ? Visibility.Visible : Visibility.Collapsed;
        PanelAbout.Visibility = idx == 2 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        var selected = Modules.Where(m => m.IsChecked).ToList();
        if (selected.Count == 0)
        {
            PromptDialog.Show(Window.GetWindow(this), "请先勾选要清空的模块。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var names = string.Join("、", selected.Select(m => m.Name));
        if (PromptDialog.Show(Window.GetWindow(this), $"确定清空以下模块的数据？\n{names}\n此操作不可恢复！", "确认清空",
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
        {
            return;
        }

        foreach (var m in selected)
        {
            m.Clear();
        }

        PromptDialog.Show(Window.GetWindow(this), "已清空选中模块的数据。", "完成", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>标题栏空白处拖动窗口；点到按钮时不拖动，避免吞掉点击</summary>
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject d)
        {
            var cur = d;
            while (cur is not null)
            {
                if (cur is System.Windows.Controls.Button or Wpf.Ui.Controls.Button)
                {
                    return;
                }
                cur = VisualTreeHelper.GetParent(cur);
            }
        }

        DragMove();
    }
}
