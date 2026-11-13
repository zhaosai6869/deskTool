using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

namespace DeskApp.Utils;

/// <summary>
/// 资源管理器定位工具。直接启动 explorer.exe /select 会被 360 等安全软件判为
/// 「利用资源管理器入侵电脑」的可疑操作（误报），这里改走 Shell 官方 API：
/// SHParseDisplayName + SHOpenFolderAndSelectItems，由系统外壳自己打开并选中，不创建 explorer 进程。
/// </summary>
public static class ShellHelper
{
    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHParseDisplayName(
        string pszName, IntPtr pbc, out IntPtr ppidl, uint sfgaoIn, out uint psfgaoOut);

    [DllImport("shell32.dll", PreserveSig = true)]
    private static extern int SHOpenFolderAndSelectItems(
        IntPtr pidlFolder, uint cidl, IntPtr[]? apidl, uint dwFlags);

    [DllImport("ole32.dll")]
    private static extern void CoTaskMemFree(IntPtr pv);

    /// <summary>
    /// 在资源管理器中打开并选中文件；path 是目录时直接打开该目录。
    /// 走 Shell API 不会被安全软件当成可疑进程调用；API 失败时才回退 ShellExecute 打开目录。
    /// </summary>
    public static bool OpenAndSelect(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        if (OpenAndSelectCore(path))
        {
            return true;
        }

        // 回退：ShellExecute 打开目录（不显式拼 explorer.exe 命令行）
        var target = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(target) || !Directory.Exists(target))
        {
            return false;
        }

        try
        {
            Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool OpenAndSelectCore(string path)
    {
        IntPtr pidl = IntPtr.Zero;
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return false;
            }

            if (SHParseDisplayName(path, IntPtr.Zero, out pidl, 0, out _) != 0)
            {
                return false;
            }

            // cidl = 0：不指定子项，由外壳打开父目录并选中该 pidl 本身
            return SHOpenFolderAndSelectItems(pidl, 0, null, 0) == 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            if (pidl != IntPtr.Zero)
            {
                CoTaskMemFree(pidl);
            }
        }
    }
}
