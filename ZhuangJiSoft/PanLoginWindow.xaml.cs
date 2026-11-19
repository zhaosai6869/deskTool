using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using DeskApp.Models;
using DeskApp.Utils;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Controls;

namespace DeskApp;

/// <summary>
/// 通用网盘登录窗口：内嵌各家官方站点，用户在官方页面完成登录。
/// 登录成功后自动检测关键 Cookie 并保存凭据（也支持手动点「保存凭据」）。
/// 使用固定的 WebView2 用户目录复用缓存，避免每次冷启动加载缓慢。
/// 账号密码不经过本程序。
/// </summary>
public partial class PanLoginWindow : FluentWindow
{
    /// <summary>宽松的凭据 Cookie 名关键词：覆盖各站千变万化的登录态命名。</summary>
    private static readonly System.Text.RegularExpressions.Regex CredentialKeyRegex =
        new(@"token|auth|session|user|uid|login|stoken|puus|bduc|buss|sid|ticket|passport|identity",
            System.Text.RegularExpressions.RegexOptions.Compiled |
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);

    private readonly PanSite _site;
    private readonly string _loginUrl;
    private readonly string[] _loginHints;
    private CancellationTokenSource? _poll;
    private bool _closing;

    public PanCredential? Credential { get; private set; }

    /// <summary>WebView2 用户数据目录：固定位置复用缓存，显著加快二次加载。</summary>
    private static string WebViewDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskApp", "WebView2Pan");

    public PanLoginWindow(PanSite site)
    {
        InitializeComponent();
        _site = site;
        var info = PanSites.Get(site);
        _loginUrl = info.LoginUrl;
        _loginHints = info.LoginCookieHints ?? [];
        TitleText.Text = $"登录 {info.Name}";
        Loaded += Window_Loaded;
        Closed += Window_Closed;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            // 固定用户目录 → 复用 cookie 与缓存，页面加载更快
            Directory.CreateDirectory(WebViewDataDir);
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: WebViewDataDir);
            await LoginView.EnsureCoreWebView2Async(env);

            LoginView.CoreWebView2.NavigationCompleted += OnNavCompleted;
            LoginView.CoreWebView2.Navigate(_loginUrl);
            TipText.Text = $"请在 {PanSites.NameOf(_site)} 官方页面完成登录，成功后会自动保存凭据。";
            StartAutoDetect();
        }
        catch (Exception ex)
        {
            LoadingMask.Visibility = Visibility.Collapsed;
            StatusText.Text = "登录页加载失败：" + ex.Message;
        }
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
    /// 多信号判定登录成功：①登录标志 Cookie ②页面已进入网盘主界面 ③localStorage 出现凭据。
    /// 任一命中即自动保存；同时把读到的 Cookie 数量与前几个名字显示在状态栏，便于定位。
    /// WebView2 API 与 UI 操作必须切回 UI 线程，否则跨线程异常会被吞掉。
    /// </summary>
    private void StartAutoDetect()
    {
        _poll?.Cancel();
        _poll = new CancellationTokenSource();
        var token = _poll.Token;

        _ = Task.Run(async () =>
        {
            // 页面刚打开时可能残留上次会话的 cookie，先等 2 秒再开始判定
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

                        var cookies = await ReadAllCookiesAsync(web);
                        var lsJson = await ReadLocalStorageAsync(web);

                        // 信号①：登录标志 Cookie
                        //   优先用站点精确名单；没有则用宽松凭据关键词兜底
                        //   （如 BDUSS 含 buss、__puus 含 puus、各种 token/session/user 等）
                        var hit = _loginHints.FirstOrDefault(h =>
                        {
                            var c = cookies.FirstOrDefault(x =>
                                string.Equals(x.Name, h, StringComparison.OrdinalIgnoreCase));
                            return c is not null
                                   && !string.IsNullOrWhiteSpace(c.Value)
                                   && c.Value.Length >= 4;
                        });
                        if (string.IsNullOrEmpty(hit))
                        {
                            hit = cookies.FirstOrDefault(c =>
                                !string.IsNullOrWhiteSpace(c.Value)
                                && c.Value.Length >= 6
                                && CredentialKeyRegex.IsMatch(c.Name))?.Name;
                        }

                        // 信号②：页面 URL 已进入网盘主界面（不再停留在登录页）
                        var urlLoggedIn = LooksLoggedIn(source);

                        // 信号③：localStorage 出现凭据类内容
                        var lsLoggedIn = !string.IsNullOrWhiteSpace(lsJson) && lsJson.Length > 40;

                        if (!string.IsNullOrEmpty(hit) || urlLoggedIn || lsLoggedIn)
                        {
                            var reason = !string.IsNullOrEmpty(hit) ? $"登录 Cookie「{hit}」"
                                : urlLoggedIn ? "已进入网盘主界面" : "本地凭据";
                            StatusText.Text = $"检测到登录成功（{reason}），正在保存…";
                            await SaveCredentialAsync(web, auto: true);
                            return true;
                        }

                        // 未命中：持续显示诊断，便于用户反馈实际读到了什么
                        var names = string.Join(",", cookies.Take(10).Select(c => c.Name));
                        var page = source.Length > 55 ? source.Substring(0, 55) + "…" : source;
                        StatusText.Text = $"等待登录… Cookie {cookies.Count} 个 [{names}] · {page}";
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

    /// <summary>根据当前页面 URL 判断是否已离开登录页、进入网盘主界面。</summary>
    private static bool LooksLoggedIn(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) return false;
        var lower = url.ToLowerInvariant();
        var host = uri.Host.ToLowerInvariant();

        // 仍在登录页/认证页
        if (host.Contains("passport") || host.Contains("login") || host.Contains("auth") ||
            host.Contains("signin") || lower.Contains("/login") ||
            lower.Contains("/signin") || lower.Contains("passport"))
            return false;

        // 已进入各家网盘主界面路径
        if (lower.Contains("/disk/home") || lower.Contains("/disk") ||
            lower.Contains("/home") || lower.Contains("/index") ||
            lower.Contains("/drive") || lower.Contains("/main") ||
            lower.Contains("/file") || lower.Contains("/web/"))
            return true;

        return false;
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
            return;
        }
        await SaveCredentialAsync(web, auto: false);
    }

    /// <summary>读取 Cookie + localStorage，封装为凭据并关闭窗口。</summary>
    private async Task SaveCredentialAsync(CoreWebView2 web, bool auto)
    {
        if (_closing) return;
        StatusText.Text = "正在读取凭据…";
        try
        {
            var cookies = await ReadAllCookiesAsync(web);
            var cookieHeader = string.Join("; ", cookies.Select(c => $"{c.Name}={c.Value}"));
            var lsJson = await ReadLocalStorageAsync(web);

            if (string.IsNullOrWhiteSpace(cookieHeader) && string.IsNullOrWhiteSpace(lsJson))
            {
                if (!auto) StatusText.Text = "未读取到凭据，请确认已在页面中完成登录后再点保存。";
                return;
            }

            Credential = new PanCredential
            {
                Cookies = cookieHeader,
                AccessToken = lsJson,
                UpdatedAt = DateTime.Now
            };
            _closing = true;
            _poll?.Cancel();
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            if (!auto) StatusText.Text = "读取凭据失败：" + ex.Message;
        }
    }

    /// <summary>
    /// 按多个 URI（根域 + 当前页）读取 Cookie 并按 Name 去重，
    /// 解决只按当前页 URI 读取时漏掉父域/根域登录 Cookie 的问题。
    /// </summary>
    private static async Task<List<CoreWebView2Cookie>> ReadAllCookiesAsync(CoreWebView2 web)
    {
        var map = new Dictionary<string, CoreWebView2Cookie>(StringComparer.OrdinalIgnoreCase);
        var uris = new List<string>();
        var source = web.Source;
        if (!string.IsNullOrEmpty(source))
        {
            uris.Add(source);
            try
            {
                var host = new Uri(source).Host;
                // 取主域（如 pan.baidu.com -> baidu.com），覆盖父域 cookie
                var parts = host.Split('.');
                if (parts.Length >= 2)
                {
                    var root = string.Join(".", parts[^2], parts[^1]);
                    uris.Add("https://" + root + "/");
                    uris.Add("https://" + host + "/");
                }
            }
            catch { /* 忽略 URI 解析异常 */ }
        }

        foreach (var uri in uris.Distinct())
        {
            try
            {
                var list = await web.CookieManager.GetCookiesAsync(uri);
                foreach (var c in list)
                {
                    if (!string.IsNullOrEmpty(c.Name)) map[c.Name] = c;
                }
            }
            catch { /* 单个 URI 读取失败不影响其他 */ }
        }
        return map.Values.ToList();
    }

    /// <summary>读取 localStorage 中包含 token 迹象的键，返回 JSON 对象字符串。</summary>
    private static async Task<string> ReadLocalStorageAsync(CoreWebView2 web)
    {
        const string script = @"(function(){
            try {
                var out = {};
                for (var i = 0; i < localStorage.length; i++) {
                    var k = localStorage.key(i);
                    if (/token|auth|cookie|credential|session|ticket/i.test(k)) {
                        var v = localStorage.getItem(k);
                        out[k] = v && v.length > 4000 ? v.slice(0, 4000) : v;
                    }
                }
                return out;
            } catch (e) { return {}; }
        })()";
        var result = await web.ExecuteScriptAsync(script);
        if (string.IsNullOrWhiteSpace(result) || result == "null") return string.Empty;
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(result);
            if (json.ValueKind == JsonValueKind.String)
            {
                var inner = json.GetString();
                if (!string.IsNullOrWhiteSpace(inner)) json = JsonSerializer.Deserialize<JsonElement>(inner);
            }
            if (json.ValueKind != JsonValueKind.Object) return string.Empty;
            var builder = new Dictionary<string, string>();
            foreach (var p in json.EnumerateObject())
            {
                builder[p.Name] = p.Value.ValueKind == JsonValueKind.String
                    ? p.Value.GetString() ?? string.Empty
                    : p.Value.ToString();
            }
            return builder.Count == 0 ? string.Empty : JsonSerializer.Serialize(builder);
        }
        catch
        {
            return string.Empty;
        }
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
