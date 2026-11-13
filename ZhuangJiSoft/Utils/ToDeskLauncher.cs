using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace DeskApp.Utils;

/// <summary>
/// ToDesk.exe 路径解析。参考 MstscManager 的 check_exe_path("todesk_exe_path")：
/// 原项目要求用户到「设置 → 三方EXE位置」手工配置；这里按新框架简化为
/// 记住的路径 → 常见安装目录 → 注册表卸载项 → 仍找不到时由用户选一次并记住。
/// 全局只存一份路径，不污染列表页 UI。
/// </summary>
public static class ToDeskLauncher
{
    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "data", "todesk_path.txt");

    /// <summary>
    /// ToDesk 后台服务名。5.x 的 -version/-status/-control 都不是新进程自己处理，
    /// 而是转发给该服务提供的本地命令通道（TCP 35600 或命名管道）；
    /// 服务未运行时一律输出 "Out of service"。
    /// </summary>
    private const string ServiceName = "ToDesk_Service";

    // 5.x 服务在 127.0.0.1:35600 提供本地命令通道；实测端口 Listening 并不代表
    // -control 可用（服务刚起来时会返回 Error），故就绪判定改用 -status 真实探测。

    /// <summary>返回可用的 ToDesk.exe 完整路径；找不到返回 null。</summary>
    public static string? Resolve()
    {
        try
        {
            if (File.Exists(ConfigPath))
            {
                var saved = File.ReadAllText(ConfigPath).Trim().Trim('"');
                if (File.Exists(saved))
                {
                    return saved;
                }
            }
        }
        catch
        {
            // 配置文件损坏时继续走自动探测
        }

        foreach (var path in CommonPaths())
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        foreach (var path in RegistryPaths())
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>
    /// 按 MstscManager 的方式生成命令行参数：
    /// .\ToDesk.exe -control -id 432160856 -passwd ipa2q65t
    /// 设备代码去掉全部空白字符；未存密码时不带 -passwd，由客户端弹框询问。
    /// </summary>
    public static string BuildArguments(string deviceId, string password)
    {
        var id = string.Concat((deviceId ?? string.Empty).Where(c => !char.IsWhiteSpace(c)));
        var args = "-control -id " + id;
        if (!string.IsNullOrEmpty(password))
        {
            args += " -passwd " + password;
        }

        return args;
    }

    /// <summary>判断 ToDesk 主程序是否已在运行（按 exe 文件名，并兜底 ToDesk）。</summary>
    public static bool IsRunning(string exe)
    {
        var name = Path.GetFileNameWithoutExtension(exe);
        if (HasProcess(name))
        {
            return true;
        }

        return !string.Equals(name, "ToDesk", StringComparison.OrdinalIgnoreCase) && HasProcess("ToDesk");
    }

    /// <summary>
    /// 无参数拉起 ToDesk 主程序。ToDesk 未运行时直接带 -control 参数会被冷启动流程丢弃，
    /// 所以先启动；后台服务是否可用由 WaitForReadyAsync 探测。
    /// </summary>
    public static bool StartMain(string exe)
    {
        try
        {
            Process.Start(new ProcessStartInfo(exe)
            {
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty
            });
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把 ToDesk 窗口恢复、激活并放到 Z 序顶部。与向日葵复用 WindowActivator。
    /// preferLargest=true 时优先挑面积最大的可见窗口，用于 -control 成功后切到远端会话窗口。
    /// </summary>
    public static Task<bool> ActivateAsync(
        string exe, TimeSpan timeout, bool preferLargest = false)
    {
        return WindowActivator.ActivateAsync(exe, timeout, preferLargest, "ToDesk");
    }

    /// <summary>
    /// 等待命令行通道真正可用：反复执行 ToDesk.exe -status，直到不再输出 Out of service。
    /// 不再用裸 TCP 探测 35600 —— 实测端口 Listening 只说明服务进程起来了，
    /// 此时 -control 仍可能返回 Error（tcp connect err / disconnect=1500）。
    /// 纯 .NET 进程调用，不依赖 sc.exe（可能被安全策略拦截）或本地化文本解析。
    /// </summary>
    public static async Task<bool> WaitForReadyAsync(string exe, TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var probe = await RunCommandAsync(exe, "-status");
            if (probe.Ok && IsStatusReady(probe.Output))
            {
                return true;
            }

            await Task.Delay(1200);
        }

        var last = await RunCommandAsync(exe, "-status");
        return last.Ok && IsStatusReady(last.Output);
    }

    private static bool IsStatusReady(string output)
    {
        if (string.IsNullOrWhiteSpace(output))
        {
            return false;
        }

        if (output.IndexOf("Out of service", StringComparison.OrdinalIgnoreCase) >= 0)
        {
            return false;
        }

        // 冷启动期 -status 可能输出独立一行 Error，此时命令通道尚未初始化完
        return !Regex.IsMatch(output, @"\bError\b", RegexOptions.IgnoreCase);
    }

    /// <summary>
    /// 执行 ToDesk 命令行并读取输出：UseShellExecute=false + 重定向 stdout/stderr，
    /// 与人工验证成功的方式完全一致。10 秒未自行退出则强制结束并保留已读输出。
    /// Ok 仅表示进程执行完毕并拿到输出，不代表命令业务成功；secret 会在输出里脱敏。
    /// </summary>
    public static async Task<(bool Ok, string Output)> RunCommandAsync(
        string exe, string args, string? secret = null)
    {
        try
        {
            var psi = new ProcessStartInfo(exe, args)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? string.Empty
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return (false, "未能启动 ToDesk 命令行客户端");
            }

            // 并行读两个管道，避免 stdout/stderr 顺序等待死锁
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            Task readAll = Task.WhenAll(stdoutTask, stderrTask);
            var finished = await Task.WhenAny(readAll, Task.Delay(10000));
            var timedOut = finished != readAll;
            if (timedOut && !process.HasExited)
            {
                try { process.Kill(true); } catch { }
            }

            var text = (stdoutTask.IsCompletedSuccessfully ? stdoutTask.Result : string.Empty)
                       + "\n" + (stderrTask.IsCompletedSuccessfully ? stderrTask.Result : string.Empty);
            if (timedOut)
            {
                text += "\n[命令行客户端 10 秒内未退出，已强制结束]";
            }

            if (!string.IsNullOrEmpty(secret))
            {
                text = text.Replace(secret, "***");
            }

            return (true, text.Trim());
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    /// <summary>
    /// 命令行服务未运行时弹一次 UAC：把 ToDesk_Service 设为自动启动并立即启动。
    /// 设为 auto 后重启电脑也不再需要提权；返回 false 通常是用户取消了 UAC。
    /// </summary>
    public static async Task<bool> TryStartServiceElevatedAsync()
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe",
                "/c \"sc config " + ServiceName + " start= auto & sc start " + ServiceName + "\"")
            {
                UseShellExecute = true,
                Verb = "runas",
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return false;
            }

            await Task.WhenAny(process.WaitForExitAsync(), Task.Delay(20000));
            return true;
        }
        catch
        {
            // 用户取消 UAC 或提权失败
            return false;
        }
    }

    /// <summary>官方协议：ToDesk://connect&amp;设备代码&amp;密码，由系统交给 ToDesk 处理。</summary>
    public static string BuildConnectUri(string deviceId, string password)
    {
        var id = string.Concat((deviceId ?? string.Empty).Where(c => !char.IsWhiteSpace(c)));
        return "todesk://connect&" + id + "&" + Uri.EscapeDataString(password ?? string.Empty);
    }

    /// <summary>todesk:// 协议是否已注册，用来区分「真没装」和「装了但 exe 没探测到」。</summary>
    public static bool IsProtocolRegistered()
    {
        try
        {
            using var key = Registry.ClassesRoot.OpenSubKey(@"todesk\shell\open\command");
            return key is not null;
        }
        catch
        {
            return false;
        }
    }

    private static bool HasProcess(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        try
        {
            var processes = Process.GetProcessesByName(name);
            try
            {
                return processes.Length > 0;
            }
            finally
            {
                foreach (var p in processes)
                {
                    try { p.Dispose(); } catch { }
                }
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>弹出文件选择框让用户指定 ToDesk.exe。</summary>
    public static string? PickPath(Window? owner)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 ToDesk.exe",
            Filter = "ToDesk 主程序|ToDesk.exe|可执行文件|*.exe|所有文件|*.*",
            CheckFileExists = true,
            Multiselect = false
        };

        return dlg.ShowDialog(owner) == true ? dlg.FileName : null;
    }

    public static void SavePath(string path)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, path);
        }
        catch
        {
            // 记住路径只是便利功能，失败不影响本次连接
        }
    }

    private static IEnumerable<string> CommonPaths()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        };

        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)).Distinct())
        {
            yield return Path.Combine(root, "ToDesk", "ToDesk.exe");
            yield return Path.Combine(root, "ToDesk", "ToDesk", "ToDesk.exe");
        }

        yield return @"C:\Program Files\ToDesk\ToDesk.exe";
        yield return @"C:\Program Files (x86)\ToDesk\ToDesk.exe";
    }

    /// <summary>从卸载注册表项的 DisplayIcon / InstallLocation 里找 ToDesk.exe。</summary>
    private static IEnumerable<string> RegistryPaths()
    {
        object[] keys =
        {
            Registry.LocalMachine, @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
            Registry.CurrentUser,  @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"
        };

        var result = new List<string>();
        try
        {
            for (int i = 0; i < keys.Length; i += 2)
            {
                if (keys[i] is not RegistryKey hive || keys[i + 1] is not string sub)
                {
                    continue;
                }

                using var parent = hive.OpenSubKey(sub);
                if (parent is null)
                {
                    continue;
                }

                foreach (var name in parent.GetSubKeyNames())
                {
                    using var item = parent.OpenSubKey(name);
                    var display = item?.GetValue("DisplayName")?.ToString();
                    if (string.IsNullOrEmpty(display) ||
                        display.IndexOf("todesk", StringComparison.OrdinalIgnoreCase) < 0)
                    {
                        continue;
                    }

                    var icon = item?.GetValue("DisplayIcon")?.ToString();
                    if (!string.IsNullOrEmpty(icon))
                    {
                        var clean = icon.Split(',').First().Trim().Trim('"');
                        if (clean.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                        {
                            result.Add(clean);
                        }
                    }

                    var dir = item?.GetValue("InstallLocation")?.ToString();
                    if (!string.IsNullOrEmpty(dir))
                    {
                        result.Add(Path.Combine(dir.Trim().Trim('"'), "ToDesk.exe"));
                    }
                }
            }
        }
        catch
        {
            // 注册表不可读时忽略，交由用户手动选择
        }

        return result;
    }
}
