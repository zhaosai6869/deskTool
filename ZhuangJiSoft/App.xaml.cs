using System;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;

namespace DeskApp;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // 在线更新的「更新进程」模式：只做文件替换 + 拉起新版，不显示任何窗口
        if (DeskApp.Utils.UpdateService.HandleUpdaterArgs(e.Args))
        {
            StartupUri = null;
            Shutdown();
            return;
        }

        base.OnStartup(e);

        // 未处理异常兜底：写日志 + 弹窗提示，避免「双击没反应」无从排查
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;

        // 跟随系统浅色/深色主题（WPF-UI）
        ApplicationThemeManager.ApplySystemTheme();

        // 清理两天前的旧更新包目录
        DeskApp.Utils.UpdateService.CleanupOldUpdates();
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        // 第三方控件模板自带的悬停/状态切换动画，在本工程未合并主题色字典时可能抛
        // 「属性未指向路径中的 DependencyObject」。ActionButton 已换自绘模板根治，
        // 这里再兜一层：同类动画异常只记日志，不弹窗打扰用户；其它异常照常提示。
        var silent = IsThemeAnimationNoise(e.Exception);
        Log(e.Exception);
        e.Handled = true;

        if (!silent)
        {
            PromptDialog.Show(null, $"程序出现异常：\n{e.Exception.Message}\n\n详情见 data\\error.log",
                "桌面工具", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static bool IsThemeAnimationNoise(Exception ex)
    {
        if (ex is not InvalidOperationException)
        {
            return false;
        }

        var stack = ex.StackTrace ?? string.Empty;
        return stack.Contains("VerifyPathIsAnimatable", StringComparison.Ordinal)
               || stack.Contains("Storyboard.BeginCommon", StringComparison.Ordinal)
               || (stack.Contains("BeginStoryboard", StringComparison.Ordinal)
                   && ex.Message.Contains("DependencyObject", StringComparison.Ordinal));
    }

    private static void OnDomainUnhandledException(object sender, UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception ex)
        {
            Log(ex);
        }
    }

    private static void Log(Exception ex)
    {
        try
        {
            var dir = Path.Combine(AppContext.BaseDirectory, "data");
            Directory.CreateDirectory(dir);
            File.AppendAllText(Path.Combine(dir, "error.log"),
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}{Environment.NewLine}");
        }
        catch
        {
            // 日志写不进去也不能再抛，避免二次崩溃
        }
    }
}
