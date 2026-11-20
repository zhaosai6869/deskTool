using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;

namespace DeskApp.Utils;

/// <summary>
/// B 漫网页会话：在隐藏 WebView2 里打开官方页面，让页面自己完成签名、解密、鉴权，
/// 我们再从 DOM / 网络请求里"收割"结果（章节链接、带 token 的图片地址）。
/// 这样完全绕开 twirp 的 x-bili-data-sn / ultra_sign 风控签名与 index 加密。
/// 与登录窗口共用同一 WebView2 用户目录，扫码登录一次后长期有效。
/// 所有 WebView2 / UI 操作都切回 UI 线程执行。
/// </summary>
public static class BiliWebSession
{
    private const string HomeUrl = "https://manga.bilibili.com/";
    private const string ImageHostMark = "manga.hdslb.com/bfs/manga/";

    private static readonly SemaphoreSlim InitGate = new(1, 1);
    private static readonly SemaphoreSlim CallGate = new(1, 1);

    private static CoreWebView2Environment? _env;
    private static Window? _host;
    private static WebView2? _view;

    /// <summary>固定的 WebView2 用户目录：登录窗口与会话共用，避免重复登录。</summary>
    public static string WebViewDataDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DeskApp", "WebView2Bili");

    /// <summary>获取共享的 WebView2 环境（同一用户目录只能创建一个环境实例）。必须在 UI 线程调用。</summary>
    public static async Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        if (_env is not null) return _env;
        Directory.CreateDirectory(WebViewDataDir);
        _env = await CoreWebView2Environment.CreateAsync(userDataFolder: WebViewDataDir);
        return _env;
    }

    /// <summary>懒加载隐藏窗口与其 WebView2，并先打开 B 漫首页让站点脚本就绪。</summary>
    private static async Task<CoreWebView2> EnsureViewAsync()
    {
        if (_view?.CoreWebView2 is not null) return _view.CoreWebView2;

        await InitGate.WaitAsync();
        try
        {
            if (_view?.CoreWebView2 is not null) return _view.CoreWebView2;

            var dispatcher = Application.Current?.Dispatcher
                ?? throw new InvalidOperationException("应用尚未启动，无法创建 B 漫网页会话");

            var tcs = new TaskCompletionSource<CoreWebView2>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = dispatcher.InvokeAsync(async () =>
            {
                try
                {
                    var env = await GetEnvironmentAsync();
                    _host = new Window
                    {
                        Title = "B漫会话",
                        Width = 1024,
                        Height = 720,
                        Left = -4000,
                        Top = -4000,
                        ShowInTaskbar = false,
                        WindowStyle = WindowStyle.None,
                        ResizeMode = ResizeMode.NoResize
                    };
                    _view = new WebView2();
                    _host.Content = _view;
                    _host.Show();
                    await _view.EnsureCoreWebView2Async(env);
                    tcs.TrySetResult(_view.CoreWebView2);
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            });

            var web = await tcs.Task;
            await NavigateAsync(web, HomeUrl, CancellationToken.None);
            return web;
        }
        finally
        {
            InitGate.Release();
        }
    }

    /// <summary>在 UI 线程上执行一段返回字符串的 JS。</summary>
    private static async Task<string> EvalAsync(CoreWebView2 web, string script)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("应用尚未启动");
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher.InvokeAsync(async () =>
        {
            try
            {
                var raw = await web.ExecuteScriptAsync(script);
                tcs.TrySetResult(raw ?? string.Empty);
            }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        return await tcs.Task;
    }

    /// <summary>解析 ExecuteScriptAsync 的返回值（JSON 字符串字面量）为纯字符串。</summary>
    private static string Unwrap(string raw)
    {
        if (string.IsNullOrEmpty(raw) || raw == "null") return string.Empty;
        try
        {
            if (raw.Length >= 2 && raw[0] == '"')
            {
                return System.Text.Json.JsonSerializer.Deserialize<string>(raw) ?? raw;
            }
        }
        catch { /* 原样返回 */ }
        return raw;
    }

    /// <summary>导航到指定 URL 并等待加载完成。必须在 UI 线程调用。</summary>
    private static async Task NavigateAsync(CoreWebView2 web, string url, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        EventHandler<CoreWebView2NavigationCompletedEventArgs>? handler = null;
        handler = (_, e) =>
        {
            web.NavigationCompleted -= handler;
            tcs.TrySetResult(e.IsSuccess);
        };
        web.NavigationCompleted += handler;
        web.Navigate(url);
        using var reg = ct.Register(() => tcs.TrySetCanceled());
        await tcs.Task;
    }

    // ───────────────────────── 章节采集 ─────────────────────────

    /// <summary>从详情页 DOM 抓取章节链接（页脚本已自行完成签名接口请求并渲染）。</summary>
    public static async Task<List<(string EpId, string Title)>> CollectChaptersAsync(
        string comicId, CancellationToken ct)
    {
        await CallGate.WaitAsync(ct);
        try
        {
            var web = await EnsureViewAsync();
            await NavigateOnUiAsync(web, $"https://manga.bilibili.com/detail/mc{comicId}", ct);
            // 等页面脚本渲染章节列表
            await DelayOnUiAsync(6000, ct);

            var script =
                "(function(){var a=document.querySelectorAll('a[href*=\"/mc" + comicId + "/\"]');" +
                "var o=[];for(var i=0;i<a.length;i++){var h=a[i].getAttribute('href')||'';" +
                "var m=h.match(/\\/mc\\d+\\/(\\d+)/);if(m)o.push(m[1]+'|'+(a[i].textContent||'').trim().slice(0,40));}" +
                "return JSON.stringify(o);})()";
            var raw = await EvalAsync(web, script);
            var text = Unwrap(raw);

            var result = new List<(string, string)>();
            if (string.IsNullOrEmpty(text) || text == "[]") return result;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(text);
                foreach (var item in doc.RootElement.EnumerateArray())
                {
                    var s = item.GetString();
                    if (string.IsNullOrEmpty(s)) continue;
                    var bar = s.IndexOf('|');
                    var id = bar > 0 ? s[..bar] : s;
                    var title = bar > 0 ? s[(bar + 1)..] : "";
                    if (!string.IsNullOrEmpty(id) && result.All(r => r.Item1 != id))
                    {
                        result.Add((id, title));
                    }
                }
            }
            catch { /* 解析失败返回空 */ }
            return result;
        }
        finally
        {
            CallGate.Release();
        }
    }

    // ───────────────────────── 图片采集 ─────────────────────────

    /// <summary>
    /// 导航到章节阅读页，拦截页面自己发出的带 token 图片请求，滚动触发懒加载，
    /// 返回去重后的图片 URL（保持加载顺序）。
    /// </summary>
    public static async Task<List<string>> CollectImagesAsync(
        string comicId, string epId, CancellationToken ct)
    {
        await CallGate.WaitAsync(ct);
        try
        {
            var web = await EnsureViewAsync();
            var found = new List<string>();
            var seen = new HashSet<string>();

            EventHandler<CoreWebView2WebResourceRequestedEventArgs>? handler = (_, e) =>
            {
                var u = e.Request.Uri;
                if (string.IsNullOrEmpty(u)) return;
                if (u.IndexOf(ImageHostMark, StringComparison.OrdinalIgnoreCase) < 0) return;
                if (u.IndexOf("token=", StringComparison.OrdinalIgnoreCase) < 0) return;
                // 以图片主体路径（去参数、去 @处理规格）去重
                var hash = u.Split('?')[0].Split('@')[0];
                lock (seen)
                {
                    if (seen.Add(hash)) found.Add(u);
                }
            };

            var register = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dispatcher = Application.Current?.Dispatcher
                ?? throw new InvalidOperationException("应用尚未启动");
            _ = dispatcher.InvokeAsync(() =>
            {
                try
                {
                    web.AddWebResourceRequestedFilter("*" + ImageHostMark + "*", CoreWebView2WebResourceContext.All);
                    web.WebResourceRequested += handler;
                    register.TrySetResult(true);
                }
                catch (Exception ex) { register.TrySetException(ex); }
            });
            await register.Task;

            try
            {
                await NavigateOnUiAsync(web, $"https://manga.bilibili.com/mc{comicId}/{epId}", ct);
                await DelayOnUiAsync(5000, ct);

                // 滚动触发懒加载，直到图片数量连续 5 轮不再增长
                var stable = 0;
                var prev = -1;
                for (var i = 0; i < 80; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    await EvalAsync(web, "window.scrollBy(0,1200);");
                    await DelayOnUiAsync(500, ct);
                    int count;
                    lock (seen) count = seen.Count;
                    if (count == prev && count > 0) stable++; else stable = 0;
                    prev = count;
                    if (stable >= 5) break;
                }
                await DelayOnUiAsync(1200, ct);

                lock (seen) return found.ToList();
            }
            finally
            {
                var unreg = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = dispatcher.InvokeAsync(() =>
                {
                    try
                    {
                        web.WebResourceRequested -= handler;
                        try { web.RemoveWebResourceRequestedFilter("*" + ImageHostMark + "*", CoreWebView2WebResourceContext.All); } catch { }
                    }
                    finally { unreg.TrySetResult(true); }
                });
                await unreg.Task;
            }
        }
        finally
        {
            CallGate.Release();
        }
    }

    /// <summary>在 UI 线程导航。</summary>
    private static async Task NavigateOnUiAsync(CoreWebView2 web, string url, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("应用尚未启动");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher.InvokeAsync(async () =>
        {
            try { await NavigateAsync(web, url, ct); tcs.TrySetResult(true); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        await tcs.Task;
    }

    /// <summary>在 UI 线程上延迟（避免直接在后台线程 Task.Delay 后操作页面）。</summary>
    private static async Task DelayOnUiAsync(int ms, CancellationToken ct)
    {
        var dispatcher = Application.Current?.Dispatcher
            ?? throw new InvalidOperationException("应用尚未启动");
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = dispatcher.InvokeAsync(async () =>
        {
            try { await Task.Delay(ms, ct); tcs.TrySetResult(true); }
            catch (OperationCanceledException) { tcs.TrySetCanceled(ct); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        });
        await tcs.Task;
    }
}
