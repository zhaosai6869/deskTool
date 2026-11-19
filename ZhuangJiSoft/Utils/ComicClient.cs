using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DeskApp.Utils;

/// <summary>
/// 漫画客户端：通用 HTML 抓取（自动 Referer/UA、GBK/UTF-8 编码自适应、重试）与图片下载。
/// 不绑定站点，仅依赖用户提供的网址与通用解析规则。
/// </summary>
public static class ComicClient
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private static readonly HttpClient Http;

    static ComicClient()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = true,
            CookieContainer = new System.Net.CookieContainer()
        };
        Http = new HttpClient(handler) { Timeout = System.Threading.Timeout.InfiniteTimeSpan };
    }

    /// <summary>抓取网页 HTML，自动识别编码（UTF-8 / GB18030 / GBK）。</summary>
    public static async Task<string> GetHtmlAsync(string url, ComicConfig config, CancellationToken ct)
    {
        var (bytes, finalUrl) = await GetBytesAsync(url, url, config, ct);

        // 1) 先看 HTTP 响应头 / HTML meta 声明的编码
        var charset = DetectCharsetFromBytes(bytes);
        var enc = ToEncoding(charset);

        // 2) 用该编码解码，若出现大量替换符则回退 UTF-8 / GB18030 试探
        var text = enc.GetString(bytes);
        if (CountReplacement(text) > 3)
        {
            foreach (var candidate in new[] { Encoding.UTF8, Encoding.GetEncoding("GB18030") })
            {
                var alt = candidate.GetString(bytes);
                if (CountReplacement(alt) < CountReplacement(text))
                {
                    text = alt;
                }
            }
        }
        _ = finalUrl;
        return text;
    }

    /// <summary>下载单张图片到指定路径，带 Referer 防盗链；返回是否成功及字节数。</summary>
    public static async Task<(bool Ok, string Message, long Bytes)> DownloadImageAsync(
        string imageUrl, string referer, string targetPath, ComicConfig config, CancellationToken ct)
    {
        var errors = new List<string>();
        for (var attempt = 0; attempt <= config.Retries; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, imageUrl);
                req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                req.Headers.TryAddWithoutValidation("Referer", referer);
                if (Uri.TryCreate(referer, UriKind.Absolute, out var refUri))
                {
                    req.Headers.TryAddWithoutValidation("Origin", refUri.GetLeftPart(UriPartial.Authority));
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    errors.Add($"HTTP {(int)resp.StatusCode}");
                    continue;
                }

                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var temp = targetPath + ".part";
                await using (var src = await resp.Content.ReadAsStreamAsync(linked.Token))
                await using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
                {
                    await src.CopyToAsync(dst, linked.Token);
                }

                // 校验图片文件头：JPEG / PNG / GIF / WEBP / BMP
                if (!IsImage(temp))
                {
                    TryDelete(temp);
                    errors.Add("返回内容非图片");
                    continue;
                }

                File.Move(temp, targetPath, overwrite: true);
                return (true, string.Empty, new FileInfo(targetPath).Length);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                errors.Add("超时");
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
            if (!ct.IsCancellationRequested && config.DelayMs > 0)
            {
                try { await Task.Delay(config.DelayMs, ct); } catch { /* 取消时退出 */ }
            }
        }
        return (false, string.Join("；", errors), 0);
    }

    private static async Task<(byte[] Bytes, string FinalUrl)> GetBytesAsync(
        string url, string referer, ComicConfig config, CancellationToken ct)
    {
        var errors = new List<string>();
        for (var attempt = 0; attempt <= config.Retries; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                req.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,*/*;q=0.8");
                req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
                req.Headers.TryAddWithoutValidation("Referer", referer);

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, linked.Token);
                var charset = resp.Content.Headers.ContentType?.CharSet?.Trim('"') ?? string.Empty;
                var bytes = await resp.Content.ReadAsByteArrayAsync(linked.Token);

                if (!resp.IsSuccessStatusCode)
                {
                    errors.Add($"HTTP {(int)resp.StatusCode}");
                    continue;
                }
                // 把响应头 charset 拼进字节前无法直接传，简单起见用 BOM/meta 检测；
                // 这里把 charset 存到一个短命字典由 DetectCharsetFromBytes 读取
                _lastHeaderCharset = charset;
                return (bytes, resp.RequestMessage?.RequestUri?.AbsoluteUri ?? url);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                errors.Add("超时");
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }
        throw new InvalidOperationException("网页抓取失败：" + string.Join("；", errors));
    }

    [ThreadStatic] private static string? _lastHeaderCharset;

    private static string DetectCharsetFromBytes(byte[] bytes)
    {
        if (!string.IsNullOrEmpty(_lastHeaderCharset)) return _lastHeaderCharset!;
        // BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return "utf-8";
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return "utf-16le";
        // 从 <meta charset=...> 检测（ASCII 扫描）
        var head = Encoding.ASCII.GetString(bytes, 0, Math.Min(bytes.Length, 4096));
        var m = System.Text.RegularExpressions.Regex.Match(head,
            @"charset\s*=\s*[""']?([\w-]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        return "utf-8";
    }

    private static Encoding ToEncoding(string charset)
    {
        if (string.IsNullOrWhiteSpace(charset)) return Encoding.UTF8;
        if (charset.Contains("utf-8", StringComparison.OrdinalIgnoreCase) ||
            charset.Contains("utf8", StringComparison.OrdinalIgnoreCase)) return Encoding.UTF8;
        try { return Encoding.GetEncoding(charset); }
        catch { return Encoding.UTF8; }
    }

    private static int CountReplacement(string s)
    {
        var count = 0;
        foreach (var c in s) if (c == '\uFFFD') count++;
        return count;
    }

    private static bool IsImage(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var b = new byte[12];
            var n = fs.Read(b, 0, b.Length);
            if (n < 3) return false;
            if (b[0] == 0xFF && b[1] == 0xD8) return true; // JPEG
            if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true; // PNG
            if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return true; // GIF
            if (n >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
                b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return true; // WEBP
            if (b[0] == 0x42 && b[1] == 0x4D) return true; // BMP
            return false;
        }
        catch { return false; }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }
}
