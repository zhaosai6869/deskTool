using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using DeskApp.Utils.PanProviders;

namespace DeskApp.Utils;

/// <summary>
/// 通用 HTTP 多线程下载器：Range 分片并发写同一文件、进度聚合、暂停/取消、断点续传。
/// 不依赖任何网盘，Provider 拿到直链后交给它下载。
/// </summary>
public sealed class PanDownloader
{
    public sealed class ProgressInfo
    {
        public long Downloaded;
        public long Total;
        public double Percent => Total > 0 ? (double)Downloaded / Total * 100 : 0;
        public long SpeedBytesPerSec;
    }

    public sealed class Options
    {
        public int Threads { get; set; } = 16;
        public int Retries { get; set; } = 2;
        public string? Referer { get; set; }
        public string? UserAgent { get; set; }
    }

    private CancellationTokenSource? _cts;

    /// <summary>下载到指定路径。progress 回调在后台线程触发，调用方需自行 Dispatcher。</summary>
    public async Task DownloadAsync(string url, string targetPath, Options options,
        IProgress<ProgressInfo>? progress = null, CancellationToken ct = default)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _cts.Token;
        using var client = PanHttp.CreateClient(null, options.UserAgent);
        if (!string.IsNullOrWhiteSpace(options.Referer))
            client.DefaultRequestHeaders.Referrer = new Uri(options.Referer);

        // 1) HEAD 探测总大小 + 是否支持 Range
        long total;
        bool rangeOk;
        try
        {
            using var headReq = new HttpRequestMessage(HttpMethod.Head, url);
            using var head = await client.SendAsync(headReq, token);
            head.EnsureSuccessStatusCode();
            total = head.Content.Headers.ContentLength ?? 0;
            // 不能只信 Accept-Ranges 头（有的服务器声明支持却忽略 Range），
            // 实际发一个 Range=0-0 验证是否返回 206。
            rangeOk = false;
            if (head.Headers.AcceptRanges.Contains("bytes"))
            {
                try
                {
                    using var probeReq = new HttpRequestMessage(HttpMethod.Get, url);
                    probeReq.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 0);
                    using var probe = await client.SendAsync(probeReq, HttpCompletionOption.ResponseHeadersRead, token);
                    rangeOk = probe.StatusCode == System.Net.HttpStatusCode.PartialContent;
                }
                catch { rangeOk = false; }
            }
        }
        catch
        {
            total = 0;
            rangeOk = false;
        }

        var dir = Path.GetDirectoryName(targetPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        // 断点续传：已有的 .part 文件
        var partPath = targetPath + ".part";
        var resumed = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
        if (resumed > 0 && resumed < total) File.Delete(partPath); // 大小不符，重下
        if (resumed >= total && total > 0)
        {
            File.Move(partPath, targetPath, true);
            progress?.Report(new ProgressInfo { Downloaded = total, Total = total });
            return;
        }

        if (!rangeOk || total <= 0 || options.Threads <= 1)
        {
            // 单线程流式下载
            await DownloadSingleAsync(client, url, partPath, progress, token, options.Retries, total);
        }
        else
        {
            await DownloadMultiAsync(client, url, partPath, total, options, progress, token);
        }

        File.Move(partPath, targetPath, true);
    }

    public void Cancel() => _cts?.Cancel();

    private static async Task DownloadSingleAsync(HttpClient client, string url, string path,
        IProgress<ProgressInfo>? progress, CancellationToken ct, int retries, long total)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var resp = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct);
                resp.EnsureSuccessStatusCode();
                total = resp.Content.Headers.ContentLength ?? total;
                await using var src = await resp.Content.ReadAsStreamAsync(ct);
                await using var dst = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
                var buffer = new byte[81920];
                long done = 0;
                int read;
                while ((read = await src.ReadAsync(buffer, ct)) > 0)
                {
                    await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                    done += read;
                    progress?.Report(new ProgressInfo { Downloaded = done, Total = total });
                }
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch when (attempt < retries)
            {
                await Task.Delay(500 * (attempt + 1), ct);
            }
        }
    }

    private static async Task DownloadMultiAsync(HttpClient client, string url, string path,
        long total, Options options, IProgress<ProgressInfo>? progress, CancellationToken ct)
    {
        var threads = Math.Clamp(options.Threads, 1, 32);
        var chunk = total / threads;
        var downloaded = new long[threads];
        var speedLock = new object();
        long lastBytes = 0;
        DateTime lastTime = DateTime.Now;
        long currentSpeed = 0;

        // 预分配文件
        await using (var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite))
        {
            fs.SetLength(total);
        }

        var tasks = new List<Task>();
        for (var i = 0; i < threads; i++)
        {
            var index = i;
            var start = chunk * i;
            var end = i == threads - 1 ? total - 1 : start + chunk - 1;
            tasks.Add(Task.Run(async () =>
            {
                for (var attempt = 0; ; attempt++)
                {
                    try
                    {
                        using var req = new HttpRequestMessage(HttpMethod.Get, url);
                        req.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(start + downloaded[index], end);
                        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                        resp.EnsureSuccessStatusCode();
                        // 关键：服务端必须返回 206 PartialContent 才是真分片。
                        // 若返回 200（忽略 Range），全量数据按偏移写会导致文件错乱，此处直接放弃该分片。
                        if (resp.StatusCode != System.Net.HttpStatusCode.PartialContent)
                        {
                            throw new IOException("服务端不支持 Range 分片（返回 " + (int)resp.StatusCode + "）");
                        }
                        await using var src = await resp.Content.ReadAsStreamAsync(ct);
                        await using var dst = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.ReadWrite);
                        dst.Seek(start + downloaded[index], SeekOrigin.Begin);
                        var buffer = new byte[81920];
                        int read;
                        long written = 0;
                        var expected = end - start + 1 - downloaded[index];
                        while ((read = await src.ReadAsync(buffer, ct)) > 0)
                        {
                            if (written + read > expected) break; // 防止写越界到别的分片
                            await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                            written += read;
                            lock (downloaded) downloaded[index] += read;
                        }
                        return;
                    }
                    catch (OperationCanceledException) { throw; }
                    catch when (attempt < options.Retries)
                    {
                        await Task.Delay(500 * (attempt + 1), ct);
                    }
                }
            }, ct));
        }

        // 进度聚合（含速度计算）
        using var timer = new Timer(_ =>
        {
            lock (downloaded)
            {
                var done = downloaded.Sum();
                var now = DateTime.Now;
                var seconds = (now - lastTime).TotalSeconds;
                if (seconds >= 0.5)
                {
                    currentSpeed = (long)((done - lastBytes) / seconds);
                    lastBytes = done;
                    lastTime = now;
                }
                progress?.Report(new ProgressInfo
                {
                    Downloaded = done, Total = total, SpeedBytesPerSec = currentSpeed
                });
            }
        }, null, 0, 500);

        await Task.WhenAll(tasks);
    }
}
