using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Wpf;

namespace DeskApp.Utils;

/// <summary>
/// 抖音访客 Cookie（ttwid / UIFID_TEMP / __ac_signature 等）：分享页 2026-11 起对无 Cookie 客户端
/// 只返回壳页（无 videoInfoRes），带访客 Cookie 才内嵌作品数据。Cookie 由隐藏 WebView2 访问
/// douyin.com 自动生成（无需用户登录），DPAPI 加密存 data\douyin_guest.json。
/// 失效（分享页变壳页）时调用 RefreshAsync 重新获取。
/// </summary>
public static class DouyinGuestAuth
{
    private static string AuthPath => Path.Combine(AppContext.BaseDirectory, "data", "douyin_guest.json");

    /// <summary>读取已保存的访客 Cookie 头；没有或解密失败返回 null。</summary>
    public static string? Load()
    {
        try
        {
            if (!File.Exists(AuthPath))
            {
                return null;
            }

            var doc = JsonSerializer.Deserialize<GuestFile>(File.ReadAllText(AuthPath));
            if (doc is null || string.IsNullOrEmpty(doc.CookieEncrypted))
            {
                return null;
            }

            return RdpCrypto.Decrypt(doc.CookieEncrypted);
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string cookieHeader)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AuthPath)!);
            File.WriteAllText(AuthPath, JsonSerializer.Serialize(new GuestFile
            {
                CookieEncrypted = RdpCrypto.Encrypt(cookieHeader) ?? string.Empty,
                SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm")
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // 保存失败时本次下载仍可用内存里的 Cookie
        }
    }

    public static void Clear()
    {
        try
        {
            File.Delete(AuthPath);
        }
        catch
        {
            // 文件不存在或被占用
        }
    }

    /// <summary>
    /// 用隐藏 WebView2 窗口访问 douyin.com 重新生成访客 Cookie（出现 ttwid 即成功，最多等 25 秒）。
    /// 返回拼好的 Cookie 头。失败抛 SphException（中文提示）。
    /// </summary>
    public static async Task<string> RefreshAsync()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null)
        {
            throw new SphException("界面未就绪，无法获取抖音访客凭证");
        }

        return await dispatcher.InvokeAsync(async () =>
        {
            // 移到屏幕外的 1×1 FluentWindow：不出现在任务栏、不打扰用户，但 WebView2 需要真实窗口句柄。
            // 必须用 FluentWindow：DeskApp 引用了 WPF-UI ControlsDictionary，裸 Window 会触发
            // AllowsTransparency 崩溃（与 YuanbaoLoginWindow / SshTerminalWindow 同一套做法）。
            var win = new Wpf.Ui.Controls.FluentWindow
            {
                Width = 1, Height = 1,
                WindowStyle = WindowStyle.None,
                ShowInTaskbar = false,
                ShowActivated = false,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = SystemParameters.VirtualScreenLeft - 2000,
                Top = SystemParameters.VirtualScreenTop - 2000
            };
            var webView = new WebView2();
            win.Content = webView;
            win.Show();

            try
            {
                await webView.EnsureCoreWebView2Async();
                webView.CoreWebView2.Navigate("https://www.douyin.com/");

                for (var i = 0; i < 50; i++)
                {
                    await Task.Delay(500);
                    try
                    {
                        var cookies = await webView.CoreWebView2.CookieManager
                            .GetCookiesAsync("https://www.douyin.com/");
                        if (cookies.Any(c => c.Name == "ttwid"))
                        {
                            var header = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
                            Save(header);
                            return header;
                        }
                    }
                    catch
                    {
                        // CookieManager 偶发失败时下轮重试
                    }
                }

                throw new SphException("获取抖音访客凭证超时，请稍后重试");
            }
            finally
            {
                win.Close();
            }
        }, DispatcherPriority.Normal).Task.Unwrap();
    }

    private class GuestFile
    {
        public string CookieEncrypted { get; set; } = string.Empty;

        public string SavedAt { get; set; } = string.Empty;
    }
}
