using System;
using System.Windows;
using System.Windows.Threading;

namespace DeskApp;

/// <summary>
/// 向日葵双击后的右下角置顶信息窗：明文显示识别码与访问密码，各带一个复制按钮。
/// 点击「复制密码」后 5 秒自动关闭，留出用户切到向日葵里粘贴密码的时间。
/// </summary>
public partial class SunflowerConnectWindow : Window
{
    private static SunflowerConnectWindow? _current;

    private readonly string _code;
    private readonly string _password;
    private DispatcherTimer? _closeTimer;

    private SunflowerConnectWindow(string code, string password)
    {
        InitializeComponent();
        DeskApp.Utils.WindowDragHelper.Enable(this);
        _code = code;
        _password = password;
        CodeText.Text = code;
        PwdText.Text = string.IsNullOrEmpty(password) ? "未设置" : password;
        CopyPwdBtn.IsEnabled = !string.IsNullOrEmpty(password);
        Loaded += OnLoaded;
    }

    /// <summary>打开信息窗；重复双击时先关掉旧的，避免堆叠。</summary>
    public static void ShowInfo(string code, string password)
    {
        if (_current is { IsLoaded: true })
        {
            _current.Close();
        }

        _current = new SunflowerConnectWindow(code, password);
        _current.Show();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // SizeToContent="Height" 的实际高度要等窗口渲染后才有，故在 Loaded 里定位右下角
        var area = SystemParameters.WorkArea;
        Left = Math.Max(area.Left, area.Right - ActualWidth - 16);
        Top = Math.Max(area.Top, area.Bottom - ActualHeight - 16);
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_code);
            CopyCodeBtn.Content = "已复制";
        }
        catch
        {
            CopyCodeBtn.Content = "复制失败";
        }
    }

    private void CopyPwd_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_password))
        {
            return;
        }

        try
        {
            Clipboard.SetText(_password);
            CopyPwdBtn.Content = "已复制";
        }
        catch
        {
            CopyPwdBtn.Content = "复制失败";
            return;
        }

        // 只有复制密码才启动 5 秒倒计时；连点时重置计时，不叠加多个定时器
        _closeTimer?.Stop();
        _closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _closeTimer.Tick += (_, _) =>
        {
            _closeTimer?.Stop();
            Close();
        };
        _closeTimer.Start();
    }

    protected override void OnClosed(EventArgs e)
    {
        _closeTimer?.Stop();
        if (ReferenceEquals(_current, this))
        {
            _current = null;
        }

        base.OnClosed(e);
    }
}
