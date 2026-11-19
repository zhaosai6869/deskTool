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
    private int _diagTick;

    /// <summary>打开页面时记录的初始 Cookie 名字集合与 URL（未登录基线），用于识别"新增"的登录 Cookie。</summary>
    private HashSet<string>? _baselineCookieNames;
    private string? _baselineUrl;

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
            AppendDiag($"目标站点：{PanSites.NameOf(_site)}  登录页：{_loginUrl}");
            AppendDiag($"WebView2 数据目录：{WebViewDataDir}");

            // 固定用户目录 → 复用 cookie 与缓存，页面加载更快
            Directory.CreateDirectory(WebViewDataDir);
            var env = await CoreWebView2Environment.CreateAsync(userDataFolder: WebViewDataDir);
            await LoginView.EnsureCoreWebView2Async(env);

            var web = LoginView.CoreWebView2;
            web.NavigationCompleted += OnNavCompleted;
            // 扫码登录常在弹窗/新标签完成，把弹窗强制并回主窗口，确保 cookie 与 localStorage 在同一上下文
            web.NewWindowRequested += (s, a) =>
            {
                AppendDiag($"拦截弹窗：{a.Uri}（并回主窗口）");
                a.Handled = true;
                if (!string.IsNullOrEmpty(a.Uri)) web.Navigate(a.Uri);
            };
            web.Navigate(_loginUrl);
            TipText.Text = $"请在 {PanSites.NameOf(_site)} 官方页面完成登录（扫码/账密均可）。登录成功后点底部绿色按钮保存；也可等自动识别。";
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

    /// <summary>把一行诊断追加到日志框（自动滚动，限长）。</summary>
    private void AppendDiag(string line)
    {
        var text = $"[{DateTime.Now:HH:mm:ss}] {line}";
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.Invoke(() => AppendDiag(line));
            return;
        }
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
                        var currentNames = new HashSet<string>(
                            cookies.Select(c => c.Name), StringComparer.OrdinalIgnoreCase);

                        // ① 首轮：只记录未登录基线，绝不做判定。
                        //    这样未登录时就存在的设备 Cookie（如夸克 __puus）永远不会被当成登录凭证。
                        if (_baselineCookieNames is null)
                        {
                            _baselineCookieNames = currentNames;
                            _baselineUrl = source;
                            AppendDiag($"建立未登录基线：Cookie {currentNames.Count} 个 [{string.Join(",", currentNames.Take(12))}] URL={source}");
                            StatusText.Text = "已记录初始状态，请在此页面完成登录（登录后再点保存，或等自动识别）";
                            return false;
                        }

                        // ② 计算"相对基线新增"的 Cookie（长度≥6 且名字是凭据相关）
                        var newCookies = cookies.Where(c =>
                            !_baselineCookieNames.Contains(c.Name)
                            && !string.IsNullOrWhiteSpace(c.Value)
                            && c.Value.Length >= 6).ToList();

                        // 新增 Cookie 中命中站点精确名单，或缺省时用宽松凭据关键词
                        var hit = newCookies.FirstOrDefault(c =>
                            _loginHints.Any(h => string.Equals(h, c.Name, StringComparison.OrdinalIgnoreCase)))?.Name;
                        if (string.IsNullOrEmpty(hit))
                        {
                            hit = newCookies.FirstOrDefault(c =>
                                CredentialKeyRegex.IsMatch(c.Name))?.Name;
                        }

                        // ③ URL 从登录页跳转到了网盘主界面（且确实发生了变化）
                        var urlChanged = !string.Equals(source, _baselineUrl, StringComparison.OrdinalIgnoreCase);
                        var urlLoggedIn = urlChanged && LooksLoggedIn(source);

                        // ④ localStorage 出现凭据类内容（相对更早，用长度粗略判定）
                        var lsLoggedIn = !string.IsNullOrWhiteSpace(lsJson) && lsJson.Length > 40;

                        if (!string.IsNullOrEmpty(hit) || urlLoggedIn || lsLoggedIn)
                        {
                            var reason = !string.IsNullOrEmpty(hit) ? $"新增登录 Cookie「{hit}」"
                                : urlLoggedIn ? "页面已跳转到网盘主界面" : "本地凭据";
                            AppendDiag($"判定登录成功（{reason}）→ 自动保存");
                            StatusText.Text = $"检测到登录成功（{reason}），正在保存…";
                            await SaveCredentialAsync(web, auto: true);
                            return true;
                        }

                        // 未命中：诊断写入日志框（每 5 轮记一次，避免刷屏）
                        if (_diagTick++ % 5 == 0)
                        {
                            var added = string.Join(",", newCookies.Select(c => c.Name));
                            var lsKeys = await ReadLocalStorageKeysAsync(web);
                            AppendDiag($"等待登录 | URL={source}");
                            AppendDiag($"  Cookie 总数 {cookies.Count}，相对基线新增: [{added}]");
                            AppendDiag($"  基线 {_baselineCookieNames.Count} 个  凭据信号: cookie={hit ?? "无"} url变化={urlChanged} ls={lsLoggedIn}");
                            AppendDiag($"  localStorage 键: [{lsKeys}]");
                        }
                        StatusText.Text = $"等待登录… 已读到 Cookie {cookies.Count} 个（详见下方诊断日志）";
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
            AppendDiag("手动保存失败：WebView2 未就绪");
            return;
        }
        AppendDiag("用户点击「保存凭据」，开始读取…");
        await SaveCredentialAsync(web, auto: false);
    }

    private void CopyDiag_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(DiagLog.Text);
            StatusText.Text = "诊断日志已复制到剪贴板，可粘贴发给开发者";
        }
        catch (Exception ex)
        {
            StatusText.Text = "复制失败：" + ex.Message;
        }
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

            AppendDiag($"读取结果：Cookie {cookies.Count} 个，header {cookieHeader.Length} 字符，localStorage {lsJson.Length} 字符");
            AppendDiag($"  Cookie 名单: [{string.Join(",", cookies.Take(20).Select(c => c.Name))}]");

            if (string.IsNullOrWhiteSpace(cookieHeader) && string.IsNullOrWhiteSpace(lsJson))
            {
                var msg = "未读取到凭据。请确认：①已在此页面完成登录 ②页面已跳转到网盘主界面 ③登录没有在外部浏览器完成。";
                StatusText.Text = msg;
                AppendDiag("保存中止：" + msg);
                return;
            }

            Credential = new PanCredential
            {
                Cookies = cookieHeader,
                AccessToken = lsJson,
                UpdatedAt = DateTime.Now
            };
            AppendDiag("凭据已封装，关闭窗口并回传");
            _closing = true;
            _poll?.Cancel();
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取凭据失败：" + ex.Message;
            AppendDiag("读取凭据异常：" + ex);
        }
    }

    /// <summary>
    /// 只按当前登录站点域名读取 Cookie（当前页 + 该站主域/host），
    /// 绝不读取其它网盘域的 Cookie，避免共享 WebView2 目录下的跨站 Cookie 串味误判。
    /// </summary>
    private static async Task<List<CoreWebView2Cookie>> ReadAllCookiesAsync(CoreWebView2 web)
    {
        var map = new Dictionary<string, CoreWebView2Cookie>(StringComparer.OrdinalIgnoreCase);
        var uris = new List<string>();
        var source = web.Source;
        if (!string.IsNullOrEmpty(source))
        {
            // 只取当前实际页面所在域，跨站（如百度 cookie 出现在夸克窗口）一律忽略
            if (Uri.TryCreate(source, UriKind.Absolute, out var srcUri)
                && !string.IsNullOrWhiteSpace(srcUri.Host))
            {
                uris.Add(source);
                uris.Add("https://" + srcUri.Host + "/");
                var parts = srcUri.Host.Split('.');
                if (parts.Length >= 2)
                {
                    var root = string.Join(".", parts[^2], parts[^1]);
                    uris.Add("https://" + root + "/");
                }
            }
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

    /// <summary>读取 localStorage 中包含 token 迹象的键，返回 JSON 对象字符串。任何异常都吞掉返回空。</summary>
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
        try
        {
            var result = await web.ExecuteScriptAsync(script);
            if (string.IsNullOrWhiteSpace(result) || result == "null") return string.Empty;
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

    /// <summary>仅读取 localStorage 的所有键名（诊断用），失败返回错误文本。</summary>
    private static async Task<string> ReadLocalStorageKeysAsync(CoreWebView2 web)
    {
        try
        {
            var raw = await web.ExecuteScriptAsync("Object.keys(localStorage).join(',')");
            if (string.IsNullOrWhiteSpace(raw)) return "空";
            var json = JsonSerializer.Deserialize<JsonElement>(raw);
            return json.ValueKind == JsonValueKind.String ? json.GetString() ?? "?" : raw;
        }
        catch (Exception ex)
        {
            return "读取异常:" + ex.GetType().Name;
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
