using System;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeskApp.Utils;

/// <summary>
/// 视频流式下载（参考 wx-downloader download.py 的 stream_video / publish）：
/// 写缓存目录 .part → 校验长度完整 + MP4 头（ftyp/moov/mdat/free/wide）→ 复制成品（绝不覆盖已有文件）。
/// </summary>
public static class SphDownloader
{
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        AllowAutoRedirect = true
    });

    /// <summary>
    /// 把标题清理成安全的文件名片段：控制字符与 Windows 非法字符替换为下划线，截断到 60 字符。
    /// </summary>
    public static string SafeName(string value)
    {
        var replaced = Regex.Replace(value ?? string.Empty, @"[\x00-\x1f\x7f/\\:*?""<>|\s]+", "_")
            .Trim('_', '.', ' ');
        if (replaced.Length > 60)
        {
            replaced = replaced[..60].Trim('_', '.', ' ');
        }

        return string.IsNullOrEmpty(replaced) ? "video" : replaced;
    }

    /// <summary>分享链接的短哈希，用于成品文件名防同名冲突。</summary>
    public static string Identity(string sourceUrl) =>
        Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(sourceUrl)))[..12];

    /// <summary>
    /// 下载媒体到成品路径。progress 定期回调（已收字节, 总字节, 当前进度文本）。
    /// 返回成品字节数。网络层失败抛 SphTransientException，业务失败抛 SphException。
    /// userAgent / referer 缺省时用视频号模块的默认头；抖音等其它来源按需传入。
    /// </summary>
    public static async Task<long> DownloadAsync(
        string mediaUrl, string targetPath, string cacheDir,
        int timeoutSeconds, Func<long, long, string, Task>? progress, CancellationToken ct,
        string? userAgent = null, string? referer = null)
    {
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var tempPath = Path.Combine(cacheDir, $"sph-{Guid.NewGuid():N}.part");
        var complete = false;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            using var request = new HttpRequestMessage(HttpMethod.Get, mediaUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", userAgent ?? YuanbaoClient.DefaultUserAgent);
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");
            if (!string.IsNullOrEmpty(referer))
            {
                request.Headers.TryAddWithoutValidation("Referer", referer);
            }

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var status = (int)response.StatusCode;
            if (status is >= 500 or 429)
            {
                throw new SphTransientException($"视频下载返回 HTTP {status}");
            }

            if (status != 200)
            {
                throw new SphException($"视频下载返回 HTTP {status}");
            }

            var contentType = (response.Content.Headers.ContentType?.MediaType ?? string.Empty).ToLowerInvariant();
            if (contentType.Contains("text/") || contentType.Contains("json") ||
                contentType.Contains("xml") || contentType.Contains("mpegurl"))
            {
                throw new SphException("媒体地址返回网页或错误信息，未保存为视频");
            }

            var total = response.Content.Headers.ContentLength ?? 0;
            await using var remote = await response.Content.ReadAsStreamAsync(cts.Token);
            await using var output = File.Create(tempPath);

            var size = 0L;
            var last = 0d;
            var head = new byte[32];
            var headFilled = 0;
            var buffer = new byte[256 * 1024];
            int read;

            while ((read = await remote.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token)) > 0)
            {
                if (headFilled < 32)
                {
                    var take = Math.Min(read, 32 - headFilled);
                    Buffer.BlockCopy(buffer, 0, head, headFilled, take);
                    headFilled += take;
                }

                await output.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                size += read;

                var now = Environment.TickCount64;
                if (now - last >= 1000 && progress is not null)
                {
                    last = now;
                    await progress(size, total, ProgressText(size, total));
                }
            }

            await output.FlushAsync(cts.Token);
            // 先关闭写句柄，否则 File.Copy 读不到被独占占用的 .part 文件
            await output.DisposeAsync();

            if (size == 0 || (total > 0 && size != total))
            {
                throw new SphException($"视频传输不完整：收到 {size} 字节，预期 {total}");
            }

            if (!IsMp4Head(head))
            {
                throw new SphException("下载内容不是有效的 MP4 文件，未保存成品");
            }

            if (progress is not null)
            {
                await progress(size, total, ProgressText(size, total));
            }

            if (File.Exists(targetPath))
            {
                throw new SphException("目标文件已存在，已保留原文件未覆盖");
            }

            // 发布成品：File.Copy 不覆盖已有文件，与原实现 "xb" 语义一致
            File.Copy(tempPath, targetPath, overwrite: false);
            complete = true;
            return size;
        }
        catch (HttpRequestException ex)
        {
            throw new SphTransientException("网络请求失败或超时，请检查网络后重试：" + ex.Message);
        }
        catch (IOException ex) when (!complete)
        {
            throw new SphTransientException("写入下载文件失败：" + ex.Message);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>MP4 前 32 字节偏移 4~8 处必须是已知 box 类型（ftyp/moov/mdat/free/wide）之一。</summary>
    private static bool IsMp4Head(byte[] head)
    {
        var boxes = new[] { "ftyp", "moov", "mdat", "free", "wide" };
        foreach (var box in boxes)
        {
            var match = true;
            for (var i = 0; i < 4; i++)
            {
                if (head[4 + i] != box[i])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    private static string ProgressText(long size, long total)
    {
        var sizeText = size < 1024 * 1024 ? $"{size / 1024.0:F0} KB" : $"{size / 1024.0 / 1024.0:F1} MB";
        return total > 0 ? $"{sizeText} / {total / 1024.0 / 1024.0:F1} MB（{size * 100 / total}%）" : sizeText;
    }
}
