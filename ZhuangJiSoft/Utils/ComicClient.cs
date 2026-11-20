using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats;
using SixLabors.ImageSharp.Formats.Jpeg;
using HeyRed.ImageSharp.Heif.Formats.Avif;

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
        // B 漫等站点由内嵌网页会话把已解密的明文图落到本地临时文件，
        // 这里直接复制，不再走 HTTP（也不需要 avif 解码）。
        if (!string.IsNullOrEmpty(imageUrl) &&
            imageUrl.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase) &&
            File.Exists(imageUrl))
        {
            try
            {
                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.Copy(imageUrl, targetPath, overwrite: true);
                return (true, string.Empty, new FileInfo(targetPath).Length);
            }
            catch (Exception ex)
            {
                return (false, "复制已解密图片失败：" + ex.Message, 0);
            }
        }

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
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, linked.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    errors.Add($"HTTP {(int)resp.StatusCode}");
                    continue;
                }

                var bytes = await resp.Content.ReadAsByteArrayAsync(linked.Token);

                // B 漫图床返回 avif：WPF/WIC 与导出器均不支持，统一转成 JPEG。
                // 目标扩展名由调用方按 URL 推算，avif URL 主体是 .jpg@... → 默认落到 .jpg，正好匹配。
                if (IsAvif(bytes))
                {
                    try
                    {
                        bytes = ConvertAvifToJpeg(bytes);
                    }
                    catch (Exception ex)
                    {
                        errors.Add("avif 解码失败：" + ex.Message);
                        continue;
                    }
                }

                // 校验图片文件头：JPEG / PNG / GIF / WEBP / BMP
                if (!IsImageBytes(bytes))
                {
                    errors.Add("返回内容非图片");
                    continue;
                }

                var dir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var temp = targetPath + ".part";
                await File.WriteAllBytesAsync(temp, bytes, linked.Token);
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

    /// <summary>抓取任意文本资源（JSON / 纯文本），强制按 UTF-8 解码。</summary>
    public static Task<string> GetStringAsync(string url, string referer, ComicConfig config,
        CancellationToken ct, params (string Name, string Value)[] headers)
    {
        return SendTextAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "application/json,text/plain,*/*");
            req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            if (!string.IsNullOrWhiteSpace(referer))
            {
                req.Headers.TryAddWithoutValidation("Referer", referer);
            }
            foreach (var (name, value) in headers)
            {
                req.Headers.TryAddWithoutValidation(name, value);
            }
            return req;
        }, config, ct);
    }

    /// <summary>提交 application/x-www-form-urlencoded 表单，返回 UTF-8 文本（用于站点接口）。</summary>
    public static Task<string> PostFormAsync(string url, string referer, string form,
        ComicConfig config, CancellationToken ct, params (string Name, string Value)[] headers)
    {
        return SendTextAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(form, Encoding.UTF8, "application/x-www-form-urlencoded")
            };
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "application/json,text/plain,*/*");
            req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            if (!string.IsNullOrWhiteSpace(referer))
            {
                req.Headers.TryAddWithoutValidation("Referer", referer);
            }
            foreach (var (name, value) in headers)
            {
                req.Headers.TryAddWithoutValidation(name, value);
            }
            return req;
        }, config, ct);
    }

    /// <summary>提交 application/json 请求体，返回 UTF-8 文本（B 漫等 twirp 接口）。</summary>
    public static Task<string> PostJsonAsync(string url, string referer, string json,
        ComicConfig config, CancellationToken ct, params (string Name, string Value)[] headers)
    {
        return SendTextAsync(() =>
        {
            var req = new HttpRequestMessage(HttpMethod.Post, url)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };
            req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            req.Headers.TryAddWithoutValidation("Accept", "application/json, text/plain, */*");
            req.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9,en;q=0.8");
            if (!string.IsNullOrWhiteSpace(referer))
            {
                req.Headers.TryAddWithoutValidation("Referer", referer);
            }
            foreach (var (name, value) in headers)
            {
                if (string.IsNullOrWhiteSpace(value)) continue;
                req.Headers.TryAddWithoutValidation(name, value);
            }
            return req;
        }, config, ct);
    }

    /// <summary>下载任意二进制资源（如 B 漫的 .index 索引文件）；失败返回 null。</summary>
    public static async Task<byte[]?> DownloadBytesAsync(string url, string referer, ComicConfig config,
        CancellationToken ct, params (string Name, string Value)[] headers)
    {
        for (var attempt = 0; attempt <= config.Retries; attempt++)
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Get, url);
                req.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                if (!string.IsNullOrWhiteSpace(referer))
                {
                    req.Headers.TryAddWithoutValidation("Referer", referer);
                }
                foreach (var (name, value) in headers)
                {
                    if (string.IsNullOrWhiteSpace(value)) continue;
                    req.Headers.TryAddWithoutValidation(name, value);
                }

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, linked.Token);
                if (!resp.IsSuccessStatusCode) continue;
                return await resp.Content.ReadAsByteArrayAsync(linked.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 超时，重试
            }
            catch
            {
                // 其它错误，重试
            }
        }
        return null;
    }

    private static async Task<string> SendTextAsync(Func<HttpRequestMessage> factory,
        ComicConfig config, CancellationToken ct)
    {
        var errors = new List<string>();
        for (var attempt = 0; attempt <= config.Retries; attempt++)
        {
            try
            {
                using var req = factory();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(config.TimeoutSeconds));
                using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseContentRead, linked.Token);
                var bytes = await resp.Content.ReadAsByteArrayAsync(linked.Token);
                if (!resp.IsSuccessStatusCode)
                {
                    errors.Add($"HTTP {(int)resp.StatusCode}");
                    continue;
                }
                return Encoding.UTF8.GetString(bytes);
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
        throw new InvalidOperationException("请求失败：" + string.Join("；", errors));
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
        try { return IsImageBytes(File.ReadAllBytes(path)); }
        catch { return false; }
    }

    private static bool IsImageBytes(byte[] b)
    {
        if (b is null || b.Length < 3) return false;
        if (b[0] == 0xFF && b[1] == 0xD8) return true; // JPEG
        if (b[0] == 0x89 && b[1] == 0x50 && b[2] == 0x4E && b[3] == 0x47) return true; // PNG
        if (b[0] == 0x47 && b[1] == 0x49 && b[2] == 0x46) return true; // GIF
        if (b.Length >= 12 && b[0] == 0x52 && b[1] == 0x49 && b[2] == 0x46 && b[3] == 0x46 &&
            b[8] == 0x57 && b[9] == 0x45 && b[10] == 0x42 && b[11] == 0x50) return true; // WEBP
        if (b[0] == 0x42 && b[1] == 0x4D) return true; // BMP
        return false;
    }

    /// <summary>avif 文件头：偏移 4 处为 'ftyp'，偏移 8 处为 'avif' 或 'avis'。</summary>
    private static bool IsAvif(byte[] b)
        => b != null && b.Length >= 12 &&
           b[4] == (byte)'f' && b[5] == (byte)'t' && b[6] == (byte)'y' && b[7] == (byte)'p' &&
           ((b[8] == (byte)'a' && b[9] == (byte)'v' && b[10] == (byte)'i' && b[11] == (byte)'f') ||
            (b[8] == (byte)'a' && b[9] == (byte)'v' && b[10] == (byte)'i' && b[11] == (byte)'s'));

    /// <summary>用 HeyRed(LibHeif) + ImageSharp 把 avif 解码并重编码为 JPEG。</summary>
    private static byte[] ConvertAvifToJpeg(byte[] avif)
    {
        // LibHeif 原生库需确认已加载；失败会以 DllNotFoundException 抛出，由调用方转成友好提示。
        var config = new Configuration(new AvifConfigurationModule());
        var options = new DecoderOptions { Configuration = config };
        using var image = Image.Load(options, avif);
        using var ms = new MemoryStream();
        image.Save(ms, new JpegEncoder { Quality = 92 });
        return ms.ToArray();
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 忽略 */ }
    }
}
