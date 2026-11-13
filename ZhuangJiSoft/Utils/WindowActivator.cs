using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;

namespace DeskApp.Utils;

/// <summary>
/// 通用 Win32 窗口激活工具：把第三方客户端的主窗口（或指定的会话窗口）恢复、
/// 激活并放到 Z 序顶部。向日葵与 ToDesk 共用，避免两套重复的 P/Invoke。
///
/// 要点：
/// 1) 不能只靠 Process.MainWindowHandle —— 本机 AweSun 三个进程该值都是 0，
///    托盘程序 / 多进程客户端经常没有可识别的主窗口句柄，所以补 EnumWindows 兜底。
/// 1b) 缩到托盘时连可见窗口都没有（本机 AweSun 只有一个 visible=False 的
///     FLUTTER_RUNNER_WIN32_WINDOW 主窗口），因此也收集隐藏的大窗口并用 SW_SHOW 恢复。
/// 2) AttachThreadInput 绕过 Windows 前台锁定；只用 HWND_TOP 而非 HWND_TOPMOST，
///    避免给第三方程序留下永久置顶状态。
/// </summary>
public static class WindowActivator
{
    /// <summary>
    /// 反复尝试激活，直到成功或超时。exe 用于取主进程名，aliases 为兼容新旧版本的进程名。
    /// preferLargest 为 true 时优先挑面积最大的可见窗口（远端会话窗口一般远大于主面板）。
    /// </summary>
    public static async Task<bool> ActivateAsync(
        string exe, TimeSpan timeout, bool preferLargest = false, params string[] aliases)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            var hwnd = FindWindow(exe, preferLargest, aliases);
            if (hwnd != IntPtr.Zero && ActivateWindow(hwnd))
            {
                return true;
            }

            await Task.Delay(250);
        }

        var last = FindWindow(exe, preferLargest, aliases);
        return last != IntPtr.Zero && ActivateWindow(last);
    }

    /// <summary>查找属于目标程序的顶层窗口；供验证与诊断使用。</summary>
    public static IntPtr FindWindow(string exe, bool preferLargest, params string[] aliases)
    {
        var names = BuildNames(exe, aliases);
        if (names.Count == 0)
        {
            return IntPtr.Zero;
        }

        var pids = CollectPids(names);
        if (pids.Count == 0)
        {
            return IntPtr.Zero;
        }

        // ① EnumWindows 收集属于目标 PID 集合的顶层窗口（含托盘期隐藏的主窗口）
        var windows = EnumerateWindows(pids);

        // ② 进程自己暴露的 MainWindowHandle 作为补充，避免个别窗口没被枚举到
        foreach (var hwnd in MainHandles(pids))
        {
            if (hwnd != IntPtr.Zero && !windows.Any(w => w.Handle == hwnd) &&
                TryDescribe(hwnd, out var info))
            {
                windows.Add(info);
            }
        }

        // 有可见窗口时只用可见的；全部缩在托盘时（本机 AweSun 就是这种）才用隐藏主窗口兜底，
        // ActivateWindow 里的 ShowWindow(SW_SHOW) 会把它恢复出来。
        var visible = windows.Where(w => w.Visible).ToList();
        var pool = visible.Count > 0 ? visible : windows;
        if (pool.Count == 0)
        {
            return IntPtr.Zero;
        }

        // EnumWindows 按 Z 序返回，普通情况直接取最靠前的那个；
        // preferLargest 为 true 时挑面积最大的（远端会话窗口一般远大于主面板）。
        return preferLargest
            ? pool.OrderByDescending(w => w.Area).First().Handle
            : pool[0].Handle;
    }

    /// <summary>恢复 / 激活 / 提到 Z 序顶部。返回是否真的成为前台窗口。</summary>
    public static bool ActivateWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            ShowWindow(hwnd, IsIconic(hwnd) ? SW_RESTORE : SW_SHOW);

            var current = GetCurrentThreadId();
            GetWindowThreadProcessId(GetForegroundWindow(), out var foreground);
            GetWindowThreadProcessId(hwnd, out var target);

            var attachedForeground = foreground != 0 && foreground != current &&
                                     AttachThreadInput(current, foreground, true);
            var attachedTarget = target != 0 && target != current &&
                                 AttachThreadInput(current, target, true);

            BringWindowToTop(hwnd);
            SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_SHOWWINDOW); // HWND_TOP
            var ok = SetForegroundWindow(hwnd);

            if (attachedTarget)
            {
                AttachThreadInput(current, target, false);
            }

            if (attachedForeground)
            {
                AttachThreadInput(current, foreground, false);
            }

            return ok || GetForegroundWindow() == hwnd;
        }
        catch
        {
            return false;
        }
    }

    private static HashSet<string> BuildNames(string exe, string[] aliases)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var own = Path.GetFileNameWithoutExtension(exe ?? string.Empty);
        if (!string.IsNullOrWhiteSpace(own))
        {
            names.Add(own);
        }

        foreach (var alias in aliases ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(alias))
            {
                names.Add(alias);
            }
        }

        return names;
    }

    /// <summary>
    /// 收集目标进程的 PID。除精确名字外还接受前缀（ToDesk_Service / awesun_guard 等），
    /// 这些进程通常没有可见窗口，会在 EnumWindows 阶段自然被过滤掉。
    /// </summary>
    private static HashSet<int> CollectPids(HashSet<string> names)
    {
        var pids = new HashSet<int>();
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                var name = p.ProcessName;
                if (names.Contains(name) ||
                    names.Any(n => name.StartsWith(n, StringComparison.OrdinalIgnoreCase)))
                {
                    pids.Add(p.Id);
                }
            }
            catch
            {
                // 进程刚好退出
            }
            finally
            {
                p.Dispose();
            }
        }

        return pids;
    }

    private static List<IntPtr> MainHandles(HashSet<int> pids)
    {
        var list = new List<IntPtr>();
        foreach (var pid in pids)
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Refresh();
                if (p.MainWindowHandle != IntPtr.Zero)
                {
                    list.Add(p.MainWindowHandle);
                }
            }
            catch
            {
                // 进程刚好退出
            }
        }

        return list;
    }

    /// <summary>
    /// 判断一个句柄是否值得当作「客户端主窗口」：有标题、尺寸够大、不是 IME/GDI+ 之类的辅助窗口。
    /// 不要求可见 —— 托盘态的主窗口是隐藏的，正是需要被恢复的对象。
    /// </summary>
    private static bool TryDescribe(IntPtr hwnd, out WindowInfo info)
    {
        info = default;
        if (hwnd == IntPtr.Zero)
        {
            return false;
        }

        try
        {
            if (GetWindowTextLength(hwnd) <= 0)
            {
                return false;
            }

            var cls = new StringBuilder(128);
            GetClassName(hwnd, cls, cls.Capacity);
            var className = cls.ToString();
            // 排除输入法候选框、GDI+ 钩子、WPF 内部通知窗等辅助窗口：
            // 它们同属目标进程，尺寸可能比真正的主窗口还大，会抢走 preferLargest。
            if (className.IndexOf("IME", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("GDI+", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("CiceroUIWndFrame", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("SystemResourceNotifyWindow", StringComparison.OrdinalIgnoreCase) >= 0 ||
                className.IndexOf("MediaContextNotificationWindow", StringComparison.OrdinalIgnoreCase) >= 0)
            {
                return false;
            }

            GetWindowRect(hwnd, out var rect);
            var width = Math.Max(0, rect.Right - rect.Left);
            var height = Math.Max(0, rect.Bottom - rect.Top);
            // 最小化后的窗口尺寸会缩到 160x28，用 IsIconic 放行；其余小窗口一律忽略。
            if ((width < 200 || height < 150) && !IsIconic(hwnd))
            {
                return false;
            }

            info = new WindowInfo(hwnd, (long)width * height, IsWindowVisible(hwnd));
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static List<WindowInfo> EnumerateWindows(HashSet<int> pids)
    {
        var list = new List<WindowInfo>();
        try
        {
            EnumWindows((hwnd, _) =>
            {
                try
                {
                    GetWindowThreadProcessId(hwnd, out var pid);
                    if (pids.Contains((int)pid) && TryDescribe(hwnd, out var info))
                    {
                        list.Add(info);
                    }
                }
                catch
                {
                    // 单个窗口查询失败不影响其余窗口
                }

                return true;
            }, IntPtr.Zero);
        }
        catch
        {
            // EnumWindows 失败时返回已收集结果
        }

        return list;
    }

    private readonly record struct WindowInfo(IntPtr Handle, long Area, bool Visible);

    private const int SW_SHOW = 5;
    private const int SW_RESTORE = 9;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_SHOWWINDOW = 0x0040;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool BringWindowToTop(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }
}
