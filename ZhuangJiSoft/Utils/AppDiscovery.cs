using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Win32;

namespace DeskApp.Utils;

/// <summary>系统里发现的一个可启动应用（名称 + 目标 + 参数）。</summary>
public record AppEntry(string Name, string Target, string Arguments = "");

/// <summary>
/// 枚举系统已安装的应用：
/// ① 注册表卸载信息（HKLM 两个视图 + HKCU）——DisplayIcon 指向 exe 的桌面应用；
/// ② 开始菜单 .lnk 快捷方式（全机器 + 当前用户，递归）——覆盖绿色/便携软件。
/// 两者按目标 exe 去重合并，按名称排序。
/// </summary>
public static class AppDiscovery
{
    public static List<AppEntry> Discover()
    {
        var map = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

        // ① 注册表卸载信息
        foreach (var root in new RegistryKey?[]
                 {
                     Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall"),
                     Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall"),
                     Registry.CurrentUser.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall")
                 })
        {
            if (root is null)
            {
                continue;
            }

            using (root)
            {
                foreach (var subName in root.GetSubKeyNames())
                {
                    using var sub = root.OpenSubKey(subName);
                    if (sub is null)
                    {
                        continue;
                    }

                    if (Convert.ToInt32(sub.GetValue("SystemComponent", 0)) == 1)
                    {
                        continue;
                    }

                    var name = sub.GetValue("DisplayName") as string;
                    if (string.IsNullOrWhiteSpace(name))
                    {
                        continue;
                    }

                    var exe = ExeFromIcon(sub.GetValue("DisplayIcon") as string);
                    if (exe is null || IsUninstaller(name, exe))
                    {
                        continue;
                    }

                    // 注册表 DisplayIcon 不带参数
                    map.TryAdd(exe, new AppEntry(name, exe, string.Empty));
                }
            }
        }

        // ② 开始菜单快捷方式
        foreach (var folder in new[] {
                     Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu),
                     Environment.GetFolderPath(Environment.SpecialFolder.StartMenu)
                 })
        {
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            {
                continue;
            }

            foreach (var lnk in SafeEnumerateFiles(folder))
            {
                try
                {
                    var resolved = ShortcutResolver.Resolve(lnk);
                    if (resolved is null)
                    {
                        continue;
                    }

                    var target = resolved.Value.Target;
                    if (!target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ||
                        !File.Exists(target))
                    {
                        continue;
                    }

                    var name = Path.GetFileNameWithoutExtension(lnk);
                    if (IsUninstaller(name, target))
                    {
                        continue;
                    }

                    map.TryAdd(target, new AppEntry(name, target, resolved.Value.Arguments));
                }
                catch
                {
                    // 单个快捷方式解析失败不影响整体
                }
            }
        }

        return map.Values
            .OrderBy(a => a.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>判断是否是卸载程序（注册表里很多卸载器也会写 DisplayIcon，混进来会在下拉里显示「卸载 xxx」）。</summary>
    private static bool IsUninstaller(string name, string exePath)
    {
        if (name.StartsWith("卸载", StringComparison.Ordinal) ||
            name.StartsWith("Uninstall", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("卸载程序", StringComparison.Ordinal))
        {
            return true;
        }

        var fileName = Path.GetFileName(exePath);
        return fileName.StartsWith("unins", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("uninstall.exe", StringComparison.OrdinalIgnoreCase) ||
               fileName.Equals("uninst.exe", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>DisplayIcon → exe 完整路径；只接受以 .exe 结尾的（.ico/.dll/.icl 图标项丢弃）。</summary>
    private static string? ExeFromIcon(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon))
        {
            return null;
        }

        var value = displayIcon.Trim().Trim('"', '\'');
        // 常见后缀「,0」「,-1」是图标索引
        var comma = value.LastIndexOf(',');
        if (comma > 0)
        {
            value = value[..comma].Trim().Trim('"');
        }

        return value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
               File.Exists(value)
            ? value
            : null;
    }

    /// <summary>递归枚举 .lnk；单个目录拒绝访问（如 ACL 异常的系统目录）时跳过该目录继续。</summary>
    private static IEnumerable<string> SafeEnumerateFiles(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var dir = pending.Pop();
            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.lnk");
            }
            catch
            {
                files = Array.Empty<string>();
            }

            foreach (var file in files)
            {
                yield return file;
            }

            string[] subDirs;
            try
            {
                subDirs = Directory.GetDirectories(dir);
            }
            catch
            {
                subDirs = Array.Empty<string>();
            }

            foreach (var sub in subDirs)
            {
                pending.Push(sub);
            }
        }
    }
}
