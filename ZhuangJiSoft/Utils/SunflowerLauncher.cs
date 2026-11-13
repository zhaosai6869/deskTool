using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Win32;

namespace DeskApp.Utils;

/// <summary>
/// 向日葵（AweSun）主程序路径解析与启动。
/// 免费半自动方案只负责「拉起客户端」，不调用任何命令行/URL 连接参数：
/// 官方免费版没有可带识别码+密码直连的接口，企业 CLI 需要试用权限。
/// 路径优先级：记住的路径 → 常见安装目录 → 注册表卸载项 → 用户手动指定一次并记住。
/// </summary>
public static class SunflowerLauncher
{
    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "data", "sunflower_path.txt");

    /// <summary>返回可用的主程序完整路径；找不到返回 null。</summary>
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

        foreach (var path in CommonPaths().Concat(RegistryPaths()))
        {
            if (File.Exists(path))
            {
                return path;
            }
        }

        return null;
    }

    /// <summary>判断主程序是否已在运行；新版 AweSun 与老版 SunloginClient 互备。</summary>
    public static bool IsRunning(string exe)
    {
        var name = Path.GetFileNameWithoutExtension(exe);
        return HasProcess(name) || HasProcess("AweSun") || HasProcess("SunloginClient");
    }

    /// <summary>无参数拉起主程序；免费版需在 GUI 里粘贴识别码并输入访问密码。</summary>
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
    /// 把向日葵主窗口恢复、激活并放到 Z 序顶部。
    /// 已运行的客户端常最小化到托盘或被别的窗口盖住，只判断 IsRunning 不会把它切回前台。
    /// Win32 实现统一收敛到 WindowActivator，ToDesk 也复用同一套逻辑。
    /// </summary>
    public static Task<bool> ActivateAsync(string exe, TimeSpan timeout)
    {
        return WindowActivator.ActivateAsync(
            exe, timeout, false, "AweSun", "SunloginClient");
    }

    /// <summary>弹出文件选择框让用户指定主程序（兼容新旧两种文件名）。</summary>
    public static string? PickPath(Window? owner)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择向日葵主程序",
            Filter = "向日葵主程序|AweSun.exe;SunloginClient.exe|可执行文件|*.exe|所有文件|*.*",
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

    private static IEnumerable<string> CommonPaths()
    {
        var roots = new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData)
        };

        var tails = new[]
        {
            @"Oray\AweSun\AweSun.exe",                             // 新版 AweSun
            @"Oray\SunLogin\SunloginClient\SunloginClient.exe",   // 老版向日葵
            @"Sunlogin\SunloginClient\SunloginClient.exe"
        };

        foreach (var root in roots.Where(r => !string.IsNullOrEmpty(r)).Distinct())
        {
            foreach (var tail in tails)
            {
                yield return Path.Combine(root, tail);
            }
        }
    }

    /// <summary>
    /// 从卸载注册表项的 DisplayIcon / InstallLocation 里找主程序。
    /// 本机装在 D:\Program Files\Oray\AweSun（非系统盘），靠 DisplayIcon 命中。
    /// </summary>
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
                        !(display.IndexOf("向日葵", StringComparison.Ordinal) >= 0 ||
                          display.IndexOf("AweSun", StringComparison.OrdinalIgnoreCase) >= 0 ||
                          display.IndexOf("Sunlogin", StringComparison.OrdinalIgnoreCase) >= 0))
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
                        var d = dir.Trim().Trim('"');
                        result.Add(Path.Combine(d, "AweSun.exe"));
                        result.Add(Path.Combine(d, "SunloginClient.exe"));
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
