using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Controls;

namespace DeskApp;

/// <summary>
/// 智慧教育平台登录窗口：内嵌官方登录页，用户在官方页面完成账号密码 / 短信 / 扫码登录，
/// 登录后自动从 localStorage 读取 ND_UC_AUTH-*&amp;token 中的 access_token / mac_key / diff。
/// 账号密码不经过本程序；凭据仅保存在本机。
/// </summary>
public partial class SmartEduLoginWindow : FluentWindow
{
    private const string LoginUrl = "https://auth.smartedu.cn/uias/login";
    private const string TokenKeyPattern = "^ND_UC_AUTH-.*&ncet-xedu&token$";

    // 注意：ExecuteScriptAsync 会把脚本返回值再做一次 JSON 序列化。
    // 这里直接返回 JS 对象（不要用 JSON.stringify），C# 侧只需反序列化一次。
    // 若返回字符串会出现双重编码，导致 GetField 拿到 String 而非 Object。
    private const string ReadTokenScript = @"(function(){
        try {
            var keys = Object.keys(localStorage);
            // 官方格式：ND_UC_AUTH-{uid}&{app}&token；宽松兜底以兼容改版
            var key = keys.find(k => /^ND_UC_AUTH-.*&ncet-xedu&token$/.test(k))
                   || keys.find(k => /^ND_UC_AUTH-[^&]+&[^&]+&token$/.test(k))
                   || keys.find(k => /^ND_UC_AUTH-.*&token$/.test(k));
            var info = { host: location.hostname, keyCount: keys.length, found: !!key };
            if (!key) {
                info.sample = keys.slice(0, 5).map(k => k.length > 44 ? k.slice(0, 44) + '…' : k);
                return info;
            }
            info.key = key;
            var raw = localStorage.getItem(key);
            var wrapper = JSON.parse(raw);
            var value = (wrapper && wrapper.value) ? wrapper.value : raw;
            var token = (typeof value === 'string') ? JSON.parse(value) : value;
            info.access_token = token.access_token || '';
            info.mac_key = token.mac_key || '';
            info.diff = token.diff || 0;
            info.ok = info.access_token.length > 0 && info.mac_key.length > 0;
            return info;
        } catch (e) {
            return { host: location.hostname, error: String(e) };
        }
    })()";

    private CancellationTokenSource? _polling;
    private bool _completed;

    public SmartEduLoginWindow()
    {
        InitializeComponent();
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        Loaded += Window_Loaded;
        Closed += Window_Closed;
    }

    /// <summary>登录成功后的三要素 JSON（{access_token, mac_key, diff}）。</summary>
    public string? TokenJson { get; private set; }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoginView.EnsureCoreWebView2Async();
            LoginView.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
            LoginView.CoreWebView2.Navigate(LoginUrl);
            StatusText.Text = "请在页面中完成登录，登录成功后将自动读取凭据…";
            StartPolling();
        }
        catch (Exception ex)
        {
            LoadingMask.Visibility = Visibility.Collapsed;
            StatusText.Text = "登录页加载失败：" + ex.Message;
        }
    }

    private void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        LoadingMask.Visibility = Visibility.Collapsed;
        if (!e.IsSuccess)
        {
            StatusText.Text = "页面加载失败，可点「重新加载」重试";
        }
    }

    private void StartPolling()
    {
        _polling?.Cancel();
        _polling = new CancellationTokenSource();
        var token = _polling.Token;
        _ = Task.Run(async () =>
        {
            // 平台登录后 token 才写入 localStorage，每 1.5 秒探测一次
            while (!token.IsCancellationRequested && !_completed)
            {
                try
                {
                    // ExecuteScriptAsync 必须在 UI 线程发起；Dispatcher.Invoke 取回 Task 后再 await
                    var scriptTask = Dispatcher.Invoke(() =>
                        LoginView.CoreWebView2 is null
                            ? null
                            : LoginView.CoreWebView2.ExecuteScriptAsync(ReadTokenScript));
                    var result = scriptTask is null ? null : await scriptTask;

                    if (!string.IsNullOrWhiteSpace(result) && result != "null")
                    {
                        JsonElement json;
                        try
                        {
                            json = JsonSerializer.Deserialize<JsonElement>(result);
                        }
                        catch
                        {
                            continue;
                        }

                        // 兼容历史双重编码：若最外层是字符串，再解一层
                        if (json.ValueKind == JsonValueKind.String)
                        {
                            var inner = json.GetString();
                            if (!string.IsNullOrWhiteSpace(inner))
                            {
                                try { json = JsonSerializer.Deserialize<JsonElement>(inner); }
                                catch { /* 保留原值，下面字段读取会落空 */ }
                            }
                        }

                        var access = GetField(json, "access_token");
                        var mac = GetField(json, "mac_key");
                        var diff = GetField(json, "diff");
                        if (access.Length > 0 && mac.Length > 0)
                        {
                            TokenJson = JsonSerializer.Serialize(new
                            {
                                access_token = access,
                                mac_key = mac,
                                diff = long.TryParse(diff, out var d) ? d : 0
                            });
                            await Dispatcher.InvokeAsync(() =>
                            {
                                StatusText.Text = "已获取登录凭据，正在返回…";
                                _completed = true;
                                DialogResult = true;
                                Close();
                            });
                            return;
                        }

                        // 未取到时回报诊断信息，便于判断是未登录还是域/key 不匹配
                        var host = GetField(json, "host");
                        var err = GetField(json, "error");
                        var found = GetField(json, "found");
                        var sample = GetField(json, "sample");
                        var tip = !string.IsNullOrEmpty(err)
                            ? "读取凭据出错：" + err
                            : found.Equals("false", StringComparison.OrdinalIgnoreCase)
                                ? "当前页(" + host + ")未发现凭据，localStorage: " + sample
                                : "凭据字段不完整（域 " + host + "）";
                        await Dispatcher.InvokeAsync(() => StatusText.Text = "等待登录… " + tip);
                    }
                }
                catch
                {
                    // 页面跳转中脚本执行失败属正常，继续轮询
                }

                try
                {
                    await Task.Delay(1500, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
            }
        }, token);
    }

    private static string GetField(JsonElement root, string name) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty(name, out var v)
            ? v.ToString()
            : string.Empty;

    private void RetryBtn_Click(object sender, RoutedEventArgs e)
    {
        LoadingMask.Visibility = Visibility.Visible;
        LoginView.CoreWebView2?.Reload();
        StatusText.Text = "正在重新加载…";
    }

    private void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _completed = true;
        _polling?.Cancel();
        _polling?.Dispose();
    }
}
