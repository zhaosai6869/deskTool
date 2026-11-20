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
/// 我们再从「页面组件数据 / 页面解密后的 canvas」中收割结果。
///
/// 章节：读详情页 Vue 组件的 seasonData.episodeList（完整列表，不是折叠的 DOM）。
/// 图片：B 漫新版对 mangaup.hdslb.com + cpx 原图做了 ECDH + Go wasm 的 DRM，
///       纯 HTTP 无法复现。这里在页面里逐页跳转，页面 wasm 会把每页解密后画到 canvas；
///       我们用「注入时保存的原始 toDataURL」导出 JPEG（站点 hook 了 toDataURL 使其返回
///       undefined），再按图片内容去重，落到临时文件交给 ComicClient。
///
/// 与登录窗口共用同一 WebView2 用户目录，扫码登录一次后长期有效。
/// 所有 WebView2 / UI 操作都切回 UI 线程执行。
/// </summary>
public static class BiliWebSession
{
    private const string HomeUrl = "https://manga.bilibili.com/";

    private static readonly SemaphoreSlim InitGate = new(1, 1);
    private static readonly SemaphoreSlim CallGate = new(1, 1);

    private static CoreWebView2Environment? _env;
    private static Window? _host;
    private static WebView2? _view;

    /// <summary>
    /// 页面加载前注入：保存原始 toDataURL（站点会 hook 它），并准备收割容器。
    /// 必须在文档创建时注入，才能抢在站点脚本覆盖之前拿到原生方法。
    /// </summary>
    private const string HarvestScript = @"(function(){
      try{ window.__origToDataURL = HTMLCanvasElement.prototype.toDataURL; }catch(e){}
      window.__CAP = {};
      window.__capNow = function(){
        var cs = document.querySelectorAll('canvas');
        for(var i=0;i<cs.length;i++){
          try{
            var d = window.__origToDataURL.call(cs[i],'image/jpeg',0.92);
            if(d && d.length > 20000){
              var key = d.length + '_' + d.substring(0,100) + '_' + d.substring(d.length-60);
              if(!window.__CAP[key]) window.__CAP[key] = d;
            }
          }catch(e){}
        }
      };
    })();";

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
                        Width = 1280,
                        Height = 900,
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
                    // 在首次导航之前注入收割脚本
                    await _view.CoreWebView2.AddScriptToExecuteOnDocumentCreatedAsync(HarvestScript);
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

    /// <summary>
    /// 从详情页 Vue 组件的 seasonData.episodeList 读取完整章节列表。
    /// 旧版抓 DOM 里的 a[href] 只能拿到折叠状态下露出的 5 章，改为读权威数据源。
    /// 返回 (epId, title)，按 ord（话序）升序排列。
    /// </summary>
    public static async Task<List<(string EpId, string Title)>> CollectChaptersAsync(
        string comicId, CancellationToken ct)
    {
        await CallGate.WaitAsync(ct);
        try
        {
            var web = await EnsureViewAsync();
            await NavigateOnUiAsync(web, $"https://manga.bilibili.com/detail/mc{comicId}", ct);
            // 等页面脚本请求签名接口并填充组件数据
            await DelayOnUiAsync(6000, ct);

            var script = @"(function(){
              var all=document.querySelectorAll('*');
              for(var i=0;i<all.length;i++){
                var vm=all[i].__vue__; if(!vm)continue;
                var p=vm,d=0;
                while(p&&d<15){
                  if(p.$data&&p.$data.seasonData){
                    var ep=p.$data.seasonData.episodeList||[];var outp=[];
                    for(var k=0;k<ep.length;k++){
                      var e=ep[k];if(!e)continue;
                      outp.push(e.id+'|'+(e.ord||0)+'|'+(e.title||e._shortTitle||''));
                    }
                    return JSON.stringify(outp);
                  }
                  p=p.$parent||p.parent;d++;
                }
              }
              return '[]';
            })()";
            var text = Unwrap(await EvalAsync(web, script));

            var result = new List<(string EpId, string Title, long Ord)>();
            if (!string.IsNullOrEmpty(text) && text != "[]")
            {
                try
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(text);
                    foreach (var item in doc.RootElement.EnumerateArray())
                    {
                        var s = item.GetString();
                        if (string.IsNullOrEmpty(s)) continue;
                        var parts = s.Split('|', 3);
                        if (parts.Length < 1 || string.IsNullOrEmpty(parts[0])) continue;
                        var id = parts[0];
                        var ord = parts.Length > 1 && long.TryParse(parts[1], out var o) ? o : long.MaxValue;
                        var title = parts.Length > 2 ? parts[2] : "";
                        if (result.All(r => r.EpId != id)) result.Add((id, title, ord));
                    }
                }
                catch { /* 解析失败返回空 */ }
            }
            return result.OrderBy(r => r.Ord)
                .Select(r => (r.EpId, r.Title)).ToList();
        }
        finally
        {
            CallGate.Release();
        }
    }

    // ───────────────────────── 图片采集 ─────────────────────────

    /// <summary>
    /// 读取该话的权威图片张数（页面组件 allEpisodeReaderImages[epId].length）。
    /// </summary>
    private static async Task<int> GetImageCountAsync(CoreWebView2 web, string epId)
    {
        var script = @"(function(){
          var all=document.querySelectorAll('*');
          for(var i=0;i<all.length;i++){
            var vm=all[i].__vue__; if(!vm)continue;
            var p=vm,d=0;
            while(p&&d<15){
              if(p.allEpisodeReaderImages){
                var a=p.allEpisodeReaderImages['" + epId + @"']||[];
                return String(a.length);
              }
              p=p.$parent||p.parent;d++;
            }
          }
          return '0';
        })()";
        var raw = Unwrap(await EvalAsync(web, script));
        return int.TryParse(raw, out var n) ? n : 0;
    }

    /// <summary>
    /// 逐页跳转阅读器，让页面 wasm 解密每一页并把明文画到 canvas；导出为 JPEG 后
    /// 按内容去重，写入临时目录，返回本地文件绝对路径列表（顺序即页序）。
    ///
    /// 说明：站点 hook 了 canvas.toDataURL（返回 undefined），收割脚本在文档创建时
    /// 保存了原始方法 window.__origToDataURL，这里通过它导出明文 JPEG。
    /// </summary>
    public static async Task<List<string>> CollectImagesAsync(
        string comicId, string epId, CancellationToken ct)
    {
        await CallGate.WaitAsync(ct);
        try
        {
            var web = await EnsureViewAsync();
            await NavigateOnUiAsync(web, $"https://manga.bilibili.com/mc{comicId}/{epId}", ct);
            await DelayOnUiAsync(7000, ct);

            var count = await GetImageCountAsync(web, epId);
            if (count <= 0)
            {
                // 组件数据没就绪，退回：尝试从页面拦截的明文图 URL
                return new List<string>();
            }

            // 重置收割容器并从第 0 页开始
            await EvalAsync(web, @"(function(){
              window.__CAP={};
              var all=document.querySelectorAll('*');
              for(var i=0;i<all.length;i++){
                var vm=all[i].__vue__;if(!vm)continue;var p=vm,d=0;
                while(p&&d<15){if(p.allEpisodeReaderImages){
                  try{p.goTargetPage(0);}catch(e){}return 'ok';}
                  p=p.$parent||p.parent;d++;}
              }
              return 'no-vm';
            })()");
            await DelayOnUiAsync(1200, ct);

            // 逐页翻，每页导出当前 canvas
            for (var pg = 0; pg < count; pg++)
            {
                ct.ThrowIfCancellationRequested();
                var target = pg;
                await EvalAsync(web, @"(function(t){
                  var all=document.querySelectorAll('*');
                  for(var i=0;i<all.length;i++){
                    var vm=all[i].__vue__;if(!vm)continue;var p=vm,d=0;
                    while(p&&d<15){if(p.allEpisodeReaderImages){
                      try{p.goTargetPage(t);}catch(e){}return 'ok';}
                      p=p.$parent||p.parent;d++;}
                  }
                  return 'no-vm';
                })(" + target + ")");
                await DelayOnUiAsync(520, ct);
                await EvalAsync(web, "window.__capNow();");
            }
            await DelayOnUiAsync(1000, ct);
            await EvalAsync(web, "window.__capNow();");

            // 固化收割数组
            await EvalAsync(web, "window.__CAP_ARR=Object.keys(window.__CAP).map(function(k){return window.__CAP[k];});");
            var nRaw = Unwrap(await EvalAsync(web, "String(window.__CAP_ARR.length)"));
            var n = int.TryParse(nRaw, out var nn) ? nn : 0;
            if (n <= 0) return new List<string>();

            var tempDir = Path.Combine(Path.GetTempPath(), "DeskApp", "BiliComic", comicId, epId);
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { }
            Directory.CreateDirectory(tempDir);

            var paths = new List<string>();
            for (var i = 0; i < n; i++)
            {
                ct.ThrowIfCancellationRequested();
                var dataUrl = Unwrap(await EvalAsync(web, "window.__CAP_ARR[" + i + "]"));
                var comma = dataUrl.IndexOf(',');
                var b64 = comma >= 0 ? dataUrl[(comma + 1)..] : dataUrl;
                if (string.IsNullOrEmpty(b64)) continue;
                byte[] bytes;
                try { bytes = Convert.FromBase64String(b64); }
                catch { continue; }
                if (bytes.Length < 1000) continue;

                var file = Path.Combine(tempDir, $"{i + 1:D4}.jpg");
                await File.WriteAllBytesAsync(file, bytes, ct);
                paths.Add(file);
            }
            return paths;
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
