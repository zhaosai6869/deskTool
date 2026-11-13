using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using Microsoft.Web.WebView2.Core;
using DeskApp.Utils;
using Wpf.Ui.Controls;

namespace DeskApp;

/// <summary>
/// 元宝登录窗（参考 wx-downloader auth.py 的 Playwright 登录，改用 WebView2）：
/// 打开 yuanbao.tencent.com 让用户扫码 / 手机号登录，轮询 CookieManager 直到出现
/// hy_user / hy_token 即保存凭据（DPAPI 加密）并自动关闭。
/// 同时通过 WebResourceRequested 捕获 authorization 等附加请求头，供解析接口使用。
/// </summary>
public partial class YuanbaoLoginWindow : FluentWindow
{
    private readonly Dictionary<string, string> _extraHeaders = new();
    private readonly CancellationTokenSource _cts = new();
    private string _userAgent = string.Empty;

    /// <summary>登录是否成功（凭据已保存）。</summary>
    public bool Success { get; private set; }

    public YuanbaoLoginWindow()
    {
        InitializeComponent();
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        Loaded += Window_Loaded;
        Closed += (_, _) => _cts.Cancel();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoginView.EnsureCoreWebView2Async();
            LoginView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = true;

            // 捕获发往元宝接口的附加请求头（authorization / t-userid / x-id / x-device-id / x-hy92 / x-hy93）
            LoginView.CoreWebView2.AddWebResourceRequestedFilter(
                "https://yuanbao.tencent.com/*", CoreWebView2WebResourceContext.All);
            LoginView.CoreWebView2.WebResourceRequested += OnWebResourceRequested;

            LoginView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            LoginView.CoreWebView2.Navigate("https://yuanbao.tencent.com/");
            _ = PollCookieAsync(_cts.Token);
        }
        catch (Exception ex)
        {
            StatusText.Text = "WebView2 初始化失败：" + ex.Message;
        }
    }

    private void OnWebResourceRequested(object? sender, CoreWebView2WebResourceRequestedEventArgs e)
    {
        try
        {
            foreach (var name in YuanbaoAuth.ExtraHeaderNames)
            {
                var value = e.Request.Headers.GetHeader(name);
                if (!string.IsNullOrEmpty(value))
                {
                    _extraHeaders[name] = value;
                }
            }

            var ua = e.Request.Headers.GetHeader("User-Agent");
            if (!string.IsNullOrEmpty(ua))
            {
                _userAgent = ua;
            }
        }
        catch
        {
            // 单个请求头读取失败不影响登录
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            StatusText.Text = "元宝页面加载失败：" + e.WebErrorStatus + "，请检查网络后重试。";
        }
    }

    /// <summary>每秒读一次元宝 Cookie，出现 hy_user + hy_token 即保存并关闭。</summary>
    private async Task PollCookieAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var cookies = await LoginView.CoreWebView2.CookieManager
                    .GetCookiesAsync("https://yuanbao.tencent.com/api/weixin/get_parse_result");
                var names = cookies.Select(c => c.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
                if (names.Contains("hy_user") && names.Contains("hy_token"))
                {
                    var cookieHeader = YuanbaoAuth.BuildCookieHeader(cookies
                        .Where(c => DomainOf(c).EndsWith("yuanbao.tencent.com", StringComparison.OrdinalIgnoreCase))
                        .Select(c => (c.Name, c.Value, c.Path)));

                    if (string.IsNullOrEmpty(cookieHeader) ||
                        !cookieHeader.Split(';').Select(p => p.Split('=', 2)[0].Trim())
                            .Contains("hy_user"))
                    {
                        // 域名过滤过狠时不丢凭据：直接用全部元宝 Cookie
                        cookieHeader = string.Join("; ",
                            cookies.Select(c => $"{c.Name}={c.Value}"));
                    }

                    var agent = _userAgent;
                    if (string.IsNullOrEmpty(agent))
                    {
                        agent = await LoginView.ExecuteScriptAsync("navigator.userAgent");
                        agent = agent.Trim('"');
                    }

                    YuanbaoAuth.Save(cookieHeader, agent, _extraHeaders);
                    Success = true;
                    StatusText.Text = "登录凭据已保存。";
                    await Task.Delay(600, CancellationToken.None);
                    Close();
                    return;
                }
            }
            catch (TaskCanceledException)
            {
                return;
            }
            catch
            {
                // CookieManager 偶发失败时下轮重试
            }

            await Task.Delay(1000, ct);
        }
    }

    private static string DomainOf(CoreWebView2Cookie cookie)
    {
        var domain = cookie.Domain ?? string.Empty;
        return domain.StartsWith('.') ? domain[1..] : domain;
    }

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
        {
            DragMove();
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);
}
