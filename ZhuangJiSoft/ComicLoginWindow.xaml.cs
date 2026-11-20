using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Controls;

namespace DeskApp;

/// <summary>
/// 哔哩哔哩漫画登录窗口：内嵌官方站点，用户在官方页面完成登录（扫码 / 账密 / 短信）。
/// 登录成功后自动从 Cookie 中读取 SESSDATA 并回传，也支持手动点「保存凭据」。
/// 账号密码不经过本程序。使用固定 WebView2 用户目录复用缓存。
/// </summary>
public partial class ComicLoginWindow : FluentWindow
{
    private const string HomeUrl = "https://manga.bilibili.com/";
    private const string CookieName = "SESSDATA";

    private CancellationTokenSource? _poll;
    private bool _closing;
    private int _diagTick;
    private DateTime _openedAt;

    /// <summary>打开页面时的初始 Cookie（名称→值），用于识别"新出现/已刷新"的登录凭据。</summary>
    private Dictionary<string, string>? _baseline;
    private string? _baselineUrl;

    /// <summary>登录成功后回传的 SESSDATA 值（不含 SESSDATA= 前缀）。</summary>
    public string SessData { get; private set; } = string.Empty;

    private static string WebViewDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskApp", "WebView2Bili");

    public ComicLoginWindow()
    {
        InitializeComponent();
        Loaded += Window_Loaded;
        Closed += Window_Closed;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        _openedAt = DateTime.Now;
        try
        {
            AppendDiag($"登录页：{HomeUrl}");
            AppendDiag($"WebView2 数据目录：{WebViewDataDir}");

            Directory.CreateDirectory(WebViewDataDir);
            // 与下载时的网页会话共用同一个 WebView2 环境：登录后 cookie 直接被会话复用，无需重复登录
            var env = await DeskApp.Utils.BiliWebSession.GetEnvironmentAsync();
            await LoginView.EnsureCoreWebView2Async(env);

            var web = LoginView.CoreWebView2;
            web.NavigationCompleted += OnNavCompleted;
            // B 站登录常在弹窗/新标签完成（passport.bilibili.com），强制并回主窗口以保证同一 Cookie 上下文
            web.NewWindowRequested += (s, a) =>
            {
                AppendDiag($"拦截弹窗：{a.Uri}（并回主窗口）");
                a.Handled = true;
                if (!string.IsNullOrEmpty(a.Uri)) web.Navigate(a.Uri);
            };
            web.Navigate(HomeUrl);
            StatusText.Text = "页面加载中…";
            StartAutoDetect();
        }
        catch (Exception ex)
        {
            LoadingMask.Visibility = Visibility.Collapsed;
            StatusText.Text = "登录页加载失败：" + ex.Message;
            AppendDiag("初始化异常：" + ex);
        }
    }

    private void AppendDiag(string line)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendDiag(line));
            return;
        }
        var text = $"[{DateTime.Now:HH:mm:ss}] {line}";
        DiagLog.AppendText(text + Environment.NewLine);
        if (DiagLog.Text.Length > 12000)
            DiagLog.Text = DiagLog.Text[^12000..];
        DiagLog.ScrollToEnd();
    }

    private void OnNavCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        LoadingMask.Visibility = Visibility.Collapsed;
        if (!e.IsSuccess)
        {
            StatusText.Text = "页面加载异常，可点「重新加载」重试";
        }
    }

    /// <summary>
    /// 轮询判定登录：以 SESSDATA 为准。
    /// ① 相对基线新出现或值发生变化 → 刚登录，立即保存；
    /// ② 已停在漫画站页面且 SESSDATA 有效（复用旧登录态）→ 保存；
    /// ③ 无论如何，20 秒后只要 SESSDATA 存在且不在登录页也保存（兜底"本来就已登录"）。
    /// WebView2 API 与 UI 操作必须切回 UI 线程，否则跨线程异常会被吞掉。
    /// </summary>
    private void StartAutoDetect()
    {
        _poll?.Cancel();
        _poll = new CancellationTokenSource();
        var token = _poll.Token;

        _ = Task.Run(async () =>
        {
            try { await Task.Delay(2000, token); }
            catch (OperationCanceledException) { return; }

            while (!token.IsCancellationRequested && !_closing)
            {
                try
                {
                    var done = await Dispatcher.Invoke(async () =>
                    {
                        if (_closing) return true;
                        var web = LoginView.CoreWebView2;
                        var source = web?.Source;
                        if (web is null || string.IsNullOrEmpty(source)) return false;

                        var cookies = await ReadCookiesAsync(web);
                        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var c in cookies)
                        {
                            if (!string.IsNullOrEmpty(c.Name)) map[c.Name] = c.Value;
                        }

                        // ① 首轮：只记录未登录基线，不做判定
                        if (_baseline is null)
                        {
                            _baseline = map;
                            _baselineUrl = source;
                            AppendDiag($"建立初始基线：Cookie {map.Count} 个 [{string.Join(",", map.Keys.Take(12))}]");
                            StatusText.Text = "已记录初始状态，请在页面中完成登录（登录后自动识别，也可点右下角绿色按钮）";
                            return false;
                        }

                        map.TryGetValue(CookieName, out var sess);
                        var hasSess = !string.IsNullOrWhiteSpace(sess) && sess!.Length >= 8;
                        var changed = hasSess
                            && (!_baseline.TryGetValue(CookieName, out var old) || !string.Equals(old, sess, StringComparison.Ordinal));

                        var urlChanged = !string.Equals(source, _baselineUrl, StringComparison.OrdinalIgnoreCase);
                        var onManga = source!.Contains("manga.bilibili.com", StringComparison.OrdinalIgnoreCase);
                        var onLoginPage = LooksLikeLoginPage(source);

                        var reason = string.Empty;
                        if (changed) reason = "检测到新的 SESSDATA";
                        else if (hasSess && onManga && !onLoginPage && urlChanged) reason = "页面已进入漫画站";
                        else if (hasSess && onManga && !onLoginPage && (DateTime.Now - _openedAt).TotalSeconds > 20) reason = "已存在有效登录态";

                        if (reason.Length > 0)
                        {
                            AppendDiag($"判定登录成功（{reason}）→ 自动保存");
                            StatusText.Text = $"检测到登录成功（{reason}），正在保存…";
                            return await SaveCredentialAsync(web);
                        }

                        if (_diagTick++ % 5 == 0)
                        {
                            AppendDiag($"等待登录 | URL={source}");
                            AppendDiag($"  Cookie {map.Count} 个，SESSDATA={(hasSess ? "已就绪" : "无")}，变化={changed}，登录页={onLoginPage}");
                        }
                        StatusText.Text = $"等待登录… 已读到 Cookie {map.Count} 个（详见下方诊断日志）";
                        return false;
                    });

                    if (done) return;
                }
                catch
                {
                    // 页面跳转中脚本执行失败或窗口关闭，继续下一轮
                }

                try { await Task.Delay(1500, token); }
                catch (OperationCanceledException) { return; }
            }
        }, token);
    }

    /// <summary>是否仍停留在登录/认证页（passport 等）。</summary>
    private static bool LooksLikeLoginPage(string url)
    {
        var lower = url.ToLowerInvariant();
        return lower.Contains("passport.bilibili.com") || lower.Contains("/login") ||
               lower.Contains("signin") || lower.Contains("account.bilibili.com/login");
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        LoadingMask.Visibility = Visibility.Visible;
        LoginView.CoreWebView2?.Reload();
    }

    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var web = LoginView.CoreWebView2;
        if (web is null)
        {
            StatusText.Text = "页面尚未加载完成";
            AppendDiag("手动保存失败：WebView2 未就绪");
            return;
        }
        AppendDiag("用户点击「保存凭据」，开始读取…");
        await SaveCredentialAsync(web);
    }

    private void CopyDiag_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DiagLog.Text);
            StatusText.Text = "诊断日志已复制到剪贴板";
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
    }

    /// <summary>读取 Cookie，取出 SESSDATA 并关闭窗口。返回 true 表示已成功保存。</summary>
    private async Task<bool> SaveCredentialAsync(CoreWebView2 web)
    {
        if (_closing) return true;
        StatusText.Text = "正在读取凭据…";
        try
        {
            var cookies = await ReadCookiesAsync(web);
            var sess = cookies.FirstOrDefault(c =>
                string.Equals(c.Name, CookieName, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(c.Value));

            AppendDiag($"读取结果：Cookie {cookies.Count} 个，SESSDATA={(sess is null ? "未找到" : sess.Value.Length + " 字符")}");

            if (sess is null || string.IsNullOrWhiteSpace(sess.Value) || sess.Value.Length < 8)
            {
                var msg = "未读取到 SESSDATA。请确认：①已在页面完成登录 ②登录没有在外部浏览器完成。";
                StatusText.Text = msg;
                AppendDiag("保存中止：" + msg);
                return false;
            }

            SessData = sess.Value.Trim();
            AppendDiag("SESSDATA 已读取，关闭窗口并回传");
            _closing = true;
            _poll?.Cancel();
            DialogResult = true;
            Close();
            return true;
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取凭据失败：" + ex.Message;
            AppendDiag("读取凭据异常：" + ex);
            return false;
        }
    }

    /// <summary>读取 bilibili 各域 Cookie（含父域 .bilibili.com），按 名称+域 去重。</summary>
    private static async Task<List<CoreWebView2Cookie>> ReadCookiesAsync(CoreWebView2 web)
    {
        var map = new Dictionary<string, CoreWebView2Cookie>(StringComparer.OrdinalIgnoreCase);
        var uris = new List<string>
        {
            "https://manga.bilibili.com/",
            "https://www.bilibili.com/",
            "https://passport.bilibili.com/",
            "https://api.bilibili.com/"
        };
        var source = web.Source;
        if (!string.IsNullOrEmpty(source)) uris.Add(source);

        foreach (var uri in uris.Distinct())
        {
            try
            {
                var list = await web.CookieManager.GetCookiesAsync(uri);
                foreach (var c in list)
                {
                    if (string.IsNullOrEmpty(c.Name)) continue;
                    map[c.Name + "|" + c.Domain] = c;
                }
            }
            catch { /* 单个 URI 读取失败不影响其他 */ }
        }
        return map.Values.ToList();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _closing = true;
        _poll?.Cancel();
        _poll?.Dispose();
        _poll = null;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        _closing = true;
        DialogResult = false;
        Close();
    }
}
