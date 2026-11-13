using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wpf.Ui.Controls;

namespace DeskApp;

/// <summary>
/// 桌面工具外壳：FluentWindow + 自定义标题栏（标题/返回/设置/最小化/关闭）+ 各模块二级页面。
/// </summary>
public partial class LauncherWindow : FluentWindow
{
    private readonly HomeView _home = new();
    private readonly SoftwareManagerView _software = new();
    private readonly WinRemoteView _winRemote = new();
    private readonly SshRemoteView _sshRemote = new();
    private readonly ToDeskView _toDesk = new();
    private readonly SunflowerView _sunflower = new();
    private readonly SphView _sph = new();

    private readonly DouyinView _douyin = new();

    private readonly KuaishouView _kuaishou = new();

    private readonly BiliView _bili = new();

    private readonly FanqieView _fanqie = new();

    private readonly QuickLaunchView _quickLaunch = new();

    public LauncherWindow()
    {
        InitializeComponent();
        // 跟随系统主题切换（浅色/深色）
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        NavigateHome();
    }

    public void NavigateHome()
    {
        PageHost.Content = _home;
        BackButton.Visibility = Visibility.Collapsed;
    }

    public void NavigateSoftware()
    {
        PageHost.Content = _software;
        BackButton.Visibility = Visibility.Visible;
    }

    public void NavigateWinRemote()
    {
        (_winRemote.DataContext as ViewModels.RdpViewModel)?.Load();
        PageHost.Content = _winRemote;
        BackButton.Visibility = Visibility.Visible;
    }

    public void NavigateSshRemote()
    {
        (_sshRemote.DataContext as ViewModels.SshViewModel)?.Load();
        PageHost.Content = _sshRemote;
        BackButton.Visibility = Visibility.Visible;
    }

    public void NavigateToDesk()
    {
        (_toDesk.DataContext as ViewModels.ToDeskViewModel)?.Load();
        PageHost.Content = _toDesk;
        BackButton.Visibility = Visibility.Visible;
    }

    public void NavigateSunflower()
    {
        (_sunflower.DataContext as ViewModels.SunflowerViewModel)?.Load();
        PageHost.Content = _sunflower;
        BackButton.Visibility = Visibility.Visible;
    }

    public void NavigateSph()
    {
        (_sph.DataContext as ViewModels.SphViewModel)?.Load();
        PageHost.Content = _sph;
        BackButton.Visibility = Visibility.Visible;
    }

    public void NavigateDouyin()
    {
        (_douyin.DataContext as ViewModels.DouyinViewModel)?.Load();
        PageHost.Content = _douyin;
        BackButton.Visibility = Visibility.Visible;
    }

    public void NavigateKuaishou()
    {
        (_kuaishou.DataContext as ViewModels.KuaishouViewModel)?.Load();
        PageHost.Content = _kuaishou;
        BackButton.Visibility = Visibility.Visible;
    }

    public void NavigateBili()
    {
        (_bili.DataContext as ViewModels.BiliViewModel)?.Load();
        PageHost.Content = _bili;
        BackButton.Visibility = Visibility.Visible;
    }

    public void NavigateFanqie()
    {
        (_fanqie.DataContext as ViewModels.FanqieViewModel)?.Load();
        PageHost.Content = _fanqie;
        BackButton.Visibility = Visibility.Visible;
    }

    public void NavigateQuickLaunch()
    {
        (_quickLaunch.DataContext as ViewModels.QuickLaunchViewModel)?.Load();
        PageHost.Content = _quickLaunch;
        BackButton.Visibility = Visibility.Visible;
    }

    private void Back_Click(object sender, RoutedEventArgs e) => NavigateHome();

    private void Settings_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsWindow
        {
            Owner = this,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        dlg.Left = Left + (Width - dlg.Width) / 2;
        dlg.Top = Top + (Height - dlg.Height) / 2;
        dlg.ShowDialog();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

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
