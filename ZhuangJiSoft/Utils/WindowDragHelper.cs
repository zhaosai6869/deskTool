using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Wpf;

namespace DeskApp.Utils;

/// <summary>
/// 无边框窗口整窗拖动：在窗口空白处按住左键可拖动窗口，遇到输入框 / 按钮 / 列表 / 滚动条 /
/// WebView2 等交互控件则放行，不抢它们的点击。
/// 用途：工程引用 WPF-UI ControlsDictionary 后，普通 Window（各对话框）被样式化为无边框、
/// 没有系统标题栏，无法拖动；在这些窗口构造时调用 Enable(this) 即可恢复拖动。
/// </summary>
public static class WindowDragHelper
{
    public static void Enable(Window window)
    {
        window.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Left || e.ButtonState != MouseButtonState.Pressed)
            {
                return;
            }

            if (e.OriginalSource is DependencyObject source && IsInteractive(source))
            {
                return;
            }

            try
            {
                window.DragMove();
            }
            catch
            {
                // 鼠标已抬起 / 窗口最大化时 DragMove 会抛异常，忽略
            }
        };
    }

    /// <summary>命中交互控件（或其子元素）时不拖动，避免抢走输入/点击。</summary>
    private static bool IsInteractive(DependencyObject node)
    {
        var current = node;
        while (current is not null)
        {
            if (current is TextBoxBase or ButtonBase or ComboBox or PasswordBox or
                ScrollBar or DataGrid or ListBox or ListView or Slider or Thumb or WebView2)
            {
                return true;
            }

            current = VisualTreeHelper.GetParent(current);
        }

        return false;
    }
}
