using System;
using System.Runtime.InteropServices;
using System.Text;

namespace DeskApp.Utils;

/// <summary>
/// 用 Shell COM（IShellLinkW + IPersistFile）解析 .lnk 快捷方式的目标与参数，无需 WScript.Shell。
/// </summary>
public static class ShortcutResolver
{
    /// <summary>返回 (目标完整路径, 参数)；解析失败返回 null。</summary>
    public static (string Target, string Arguments)? Resolve(string shortcutPath)
    {
        object? instance = null;
        try
        {
            var type = Type.GetTypeFromCLSID(new Guid("00021401-0000-0000-C000-000000000046")); // CLSID_ShellLink
            if (type is null)
            {
                return null;
            }

            instance = Activator.CreateInstance(type);
            if (instance is null)
            {
                return null;
            }

            var persist = (IPersistFile)instance;
            persist.Load(shortcutPath, 0);

            var link = (IShellLinkW)instance;
            var targetBuilder = new StringBuilder(1024);
            link.GetPath(targetBuilder, targetBuilder.Capacity, IntPtr.Zero, 0);
            var argsBuilder = new StringBuilder(1024);
            link.GetArguments(argsBuilder, argsBuilder.Capacity);

            var target = targetBuilder.ToString();
            return string.IsNullOrWhiteSpace(target) ? null : (target, argsBuilder.ToString());
        }
        catch
        {
            return null;
        }
        finally
        {
            if (instance is not null)
            {
                Marshal.ReleaseComObject(instance);
            }
        }
    }

    [ComImport, Guid("000214F9-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszFile, int cchMax, IntPtr pfd, uint fFlags);

        void GetIDList(out IntPtr ppidl);

        void GetDescription([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszName, int cchMax);

        void GetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszDir, int cchMax);

        void GetArguments([MarshalAs(UnmanagedType.LPWStr)] StringBuilder pszArgs, int cchMax);

        void GetHotkey(out ushort pwHotkey);

        void GetShowCmd(out int piShowCmd);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string pszFile);

        void SetIDList(IntPtr pidl);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string pszName);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string pszDir);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string pszArgs);

        void SetHotkey(ushort wHotkey);

        void SetShowCmd(int iShowCmd);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName);
    }

    [ComImport, Guid("0000010B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPersistFile
    {
        void GetClassID(out Guid pclsid);

        void IsDirty();

        void Load([MarshalAs(UnmanagedType.LPWStr)] string pszFileName, uint dwMode);

        void Save([MarshalAs(UnmanagedType.LPWStr)] string? pszFileName, bool fRemember);

        void SaveCompleted([MarshalAs(UnmanagedType.LPWStr)] string pszFileName);

        void GetCurFile([MarshalAs(UnmanagedType.LPWStr)] out string ppszFileName);
    }
}
