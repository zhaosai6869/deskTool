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
        Log(e.Exception);
        PromptDialog.Show(null, $"程序出现异常：\n{e.Exception.Message}\n\n详情见 data\\error.log",
            "桌面工具", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
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
