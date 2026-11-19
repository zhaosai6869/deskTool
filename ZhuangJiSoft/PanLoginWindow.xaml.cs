using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using DeskApp.Models;
using DeskApp.Utils;
using Microsoft.Web.WebView2.Core;
using Wpf.Ui.Controls;

namespace DeskApp;

/// <summary>
/// 通用网盘登录窗口：内嵌各家官方站点，用户在官方页面完成登录，
/// 然后点「登录完成，保存凭据」，程序从 WebView2 读取该站的 Cookie + localStorage，
/// 加密保存到 PanConfig。账号密码不经过本程序。
/// </summary>
public partial class PanLoginWindow : FluentWindow
{
    private readonly PanSite _site;
    private readonly string _loginUrl;

    public PanCredential? Credential { get; private set; }

    public PanLoginWindow(PanSite site)
    {
        InitializeComponent();
        _site = site;
        var info = PanSites.Get(site);
        _loginUrl = info.LoginUrl;
        TitleText.Text = $"登录 {info.Name}";
        Loaded += Window_Loaded;
        Closed += (_, _) => LoginView.CoreWebView2?.Stop();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await LoginView.EnsureCoreWebView2Async();
            LoginView.CoreWebView2.NavigationCompleted += OnNavCompleted;
            LoginView.CoreWebView2.Navigate(_loginUrl);
            TipText.Text = $"请在 {PanSites.NameOf(_site)} 官方页面完成登录，然后点右上角「登录完成，保存凭据」。";
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
        StatusText.Text = e.IsSuccess ? "页面已加载，请登录…" : "页面加载异常，可点「重新加载」重试";
    }

    private void Reload_Click(object sender, RoutedEventArgs e)
    {
        LoadingMask.Visibility = Visibility.Visible;
        LoginView.CoreWebView2?.Reload();
    }

    /// <summary>读取 WebView2 当前上下文的 Cookie 与常见 token，封装为凭据。</summary>
    private async void Save_Click(object sender, RoutedEventArgs e)
    {
        var web = LoginView.CoreWebView2;
        if (web is null)
        {
            StatusText.Text = "页面尚未加载完成";
            return;
        }

        StatusText.Text = "正在读取凭据…";
        try
        {
            // 1) Cookie：用脚本读取当前页 cookie（含各子域可访问部分）
            var cookies = await ReadCookiesAsync(web);

            // 2) localStorage token：探测常见键名（各家不同），拼成 JSON 供 Provider 使用
            var lsJson = await ReadLocalStorageAsync(web);

            if (string.IsNullOrWhiteSpace(cookies) && string.IsNullOrWhiteSpace(lsJson))
            {
                StatusText.Text = "未读取到凭据，请确认已在页面中完成登录后再点保存。";
                return;
            }

            Credential = new PanCredential
            {
                Cookies = cookies,
                AccessToken = lsJson, // 复用字段存 localStorage JSON（Provider 自行解析）
                UpdatedAt = DateTime.Now
            };
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = "读取凭据失败：" + ex.Message;
        }
    }

    private static async Task<string> ReadCookiesAsync(CoreWebView2 web)
    {
        // 优先用 CookieManager 的 GetCookiesAsync（按 URI，可读 HttpOnly），
        // 老版本 SDK 没有该 API 时回退 document.cookie。
        try
        {
            var uri = web.Source;
            var cookies = await web.CookieManager.GetCookiesAsync(uri);
            var parts = cookies.Select(c => $"{c.Name}={c.Value}").ToList();
            if (parts.Count > 0) return string.Join("; ", parts);
        }
        catch
        {
            // SDK 版本不支持时回退
        }

        var result = await web.ExecuteScriptAsync("document.cookie || ''");
        if (string.IsNullOrWhiteSpace(result) || result == "null") return string.Empty;
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(result);
            return json.ValueKind == JsonValueKind.String ? json.GetString() ?? string.Empty : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
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
            // ExecuteScriptAsync 返回值可能被 JSON 序列化为字符串
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

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
