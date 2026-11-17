using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using DeskApp.Models;

namespace DeskApp.Utils;

/// <summary>
/// 第一试卷网抓取客户端。站点是 gb2312/gb18030 编码，列表页与详情页都是静态 HTML。
/// 题库扫描只请求列表页；真实下载地址在下载时请求详情页获取，避免一次抓几千个详情页。
/// </summary>
public static class FirstPaperClient
{
    public const string BaseUrl = "https://www.shijuan1.com";
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private static readonly HttpClient Http;
    private static readonly Encoding Gb18030;
    private static readonly Regex TitleLinkRegex = new(
        @"<a\s[^>]*class\s*=\s*['""]?title['""][^>]*>(?<title>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex HrefRegex = new(
        @"href\s*=\s*['""]?(?<href>[^'""\s>]+)['""]?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TdRegex = new(
        @"<td\b[^>]*>(?<value>.*?)</td>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex TypeIdRegex = new(@"list_(\d+)_\d+\.html", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex PageInfoRegex = new(
        @"共\s*<strong>\s*(\d+)\s*</strong>\s*页\s*<strong>\s*(\d+)\s*</strong>\s*条",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex DownListRegex = new(
        @"<ul\s+class\s*=\s*['""]?downurllist['""]?[^>]*>(?<list>.*?)</ul>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AnchorRegex = new(
        @"<a\s[^>]*href\s*=\s*['""]?(?<href>[^'""\s>]+)['""][^>]*>(?<text>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    static FirstPaperClient()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Gb18030 = Encoding.GetEncoding("gb18030");
        Http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            MaxConnectionsPerServer = 6,
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
        });
    }

    public sealed class ListPage
    {
        public List<FirstPaperRecord> Items { get; } = new();
        public int TypeId { get; set; }
        public int TotalPages { get; set; } = 1;
        public int TotalCount { get; set; }
    }

    /// <summary>抓取并解析一个列表页；第一页的 URL 形如 /a/sjsx3/。</summary>
    public static async Task<ListPage> FetchListPageAsync(
        string pageUrl, string subjectKey, string gradeKey, int timeoutSeconds, CancellationToken ct)
    {
        var html = await FetchHtmlAsync(pageUrl, timeoutSeconds, ct);
        var result = new ListPage();

        var typeMatch = TypeIdRegex.Match(html);
        if (typeMatch.Success)
        {
            result.TypeId = int.Parse(typeMatch.Groups[1].Value);
        }

        var pageMatch = PageInfoRegex.Match(html);
        if (pageMatch.Success)
        {
            result.TotalPages = Math.Max(1, int.Parse(pageMatch.Groups[1].Value));
            result.TotalCount = int.Parse(pageMatch.Groups[2].Value);
        }

        var baseUri = new Uri(pageUrl);
        foreach (Match link in TitleLinkRegex.Matches(html))
        {
            var hrefMatch = HrefRegex.Match(link.Groups["title"].Value.Length == 0
                ? link.Value
                : link.Value[..(link.Value.IndexOf('>') + 1)]);
            if (!hrefMatch.Success)
            {
                continue;
            }

            var detailUrl = new Uri(baseUri, WebUtility.HtmlDecode(hrefMatch.Groups["href"].Value)).AbsoluteUri;
            if (!detailUrl.Contains("/a/", StringComparison.OrdinalIgnoreCase) ||
                !detailUrl.EndsWith(".html", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var title = Clean(link.Groups["title"].Value);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var rowStart = html.LastIndexOf("<tr", link.Index, StringComparison.OrdinalIgnoreCase);
            var rowEnd = html.IndexOf("</tr>", link.Index, StringComparison.OrdinalIgnoreCase);
            var cells = new List<string>();
            if (rowStart >= 0 && rowEnd > rowStart)
            {
                cells = TdRegex.Matches(html[rowStart..rowEnd])
                    .Select(m => Clean(m.Groups["value"].Value))
                    .ToList();
            }

            string Cell(int index) => index >= 0 && index < cells.Count ? cells[index] : string.Empty;

            result.Items.Add(new FirstPaperRecord
            {
                SubjectKey = subjectKey,
                GradeKey = gradeKey,
                DetailUrl = detailUrl,
                Title = title,
                FileType = Cell(1),
                Version = Cell(3),
                SizeText = Cell(4),
                UploadDate = Cell(5),
                Available = true
            });
        }

        return result;
    }

    /// <summary>拼分页地址：第一页用栏目首页，后续用 list_typeid_page.html。</summary>
    public static string BuildPageUrl(string firstPageUrl, int typeId, int page)
    {
        if (page <= 1 || typeId <= 0)
        {
            return firstPageUrl;
        }

        return new Uri(new Uri(firstPageUrl), $"list_{typeId}_{page}.html").AbsoluteUri;
    }

    /// <summary>请求详情页，取「本地下载」的真实文件地址。</summary>
    public static async Task<string> ResolveDownloadUrlAsync(
        string detailUrl, int timeoutSeconds, CancellationToken ct)
    {
        var html = await FetchHtmlAsync(detailUrl, timeoutSeconds, ct);
        var block = DownListRegex.Match(html);
        if (block.Success)
        {
            foreach (Match anchor in AnchorRegex.Matches(block.Groups["list"].Value))
            {
                var href = WebUtility.HtmlDecode(anchor.Groups["href"].Value).Trim();
                var text = Clean(anchor.Groups["text"].Value);
                if (href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (text.Contains("下载", StringComparison.Ordinal) ||
                    href.Contains("/uploads/", StringComparison.OrdinalIgnoreCase))
                {
                    return new Uri(new Uri(detailUrl), href).AbsoluteUri;
                }
            }
        }

        // 兜底：页面里任何 /uploads/ 下的文件链接
        foreach (Match anchor in AnchorRegex.Matches(html))
        {
            var href = WebUtility.HtmlDecode(anchor.Groups["href"].Value).Trim();
            if (href.Contains("/uploads/", StringComparison.OrdinalIgnoreCase) &&
                !href.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) &&
                !href.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                return new Uri(new Uri(detailUrl), href).AbsoluteUri;
            }
        }

        throw new SphException("详情页未找到下载链接，可能已下架或页面结构变化");
    }

    /// <summary>
    /// 通用文件流式下载（试卷是 rar/doc/zip，不能复用视频模块的 MP4 头校验）。
    /// 写 .part → 校验 HTTP 状态 / Content-Length / 非空 → 发布成品，绝不覆盖已有文件。
    /// </summary>
    public static async Task<long> DownloadFileAsync(
        string fileUrl, string targetPath, string cacheDir, int timeoutSeconds,
        Func<long, long, string, Task>? progress, CancellationToken ct)
    {
        Directory.CreateDirectory(cacheDir);
        Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
        var tempPath = Path.Combine(cacheDir, $"firstpaper-{Guid.NewGuid():N}.part");
        var published = false;

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            using var request = new HttpRequestMessage(HttpMethod.Get, fileUrl);
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
            request.Headers.TryAddWithoutValidation("Referer", BaseUrl + "/");
            request.Headers.TryAddWithoutValidation("Accept-Encoding", "identity");

            using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            var status = (int)response.StatusCode;
            if (status is >= 500 or 429)
            {
                throw new SphTransientException($"下载返回 HTTP {status}");
            }

            if (status != 200)
            {
                throw new SphException($"下载返回 HTTP {status}");
            }

            var contentType = (response.Content.Headers.ContentType?.MediaType ?? string.Empty).ToLowerInvariant();
            if (contentType is "text/html" or "text/json" or "application/json")
            {
                throw new SphException("下载地址返回网页或错误信息，未保存为试卷");
            }

            var total = response.Content.Headers.ContentLength ?? 0;
            await using var remote = await response.Content.ReadAsStreamAsync(cts.Token);
            await using var output = File.Create(tempPath);

            var size = 0L;
            var last = 0L;
            var buffer = new byte[128 * 1024];
            int read;
            while ((read = await remote.ReadAsync(buffer.AsMemory(0, buffer.Length), cts.Token)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cts.Token);
                size += read;

                var now = Environment.TickCount64;
                if (now - last >= 500 && progress is not null)
                {
                    last = now;
                    await progress(size, total, ProgressText(size, total));
                }
            }

            await output.FlushAsync(cts.Token);
            await output.DisposeAsync();

            if (size == 0 || (total > 0 && size != total))
            {
                throw new SphException($"文件传输不完整：收到 {size} 字节，预期 {total}");
            }

            if (progress is not null)
            {
                await progress(size, total, ProgressText(size, total));
            }

            if (File.Exists(targetPath))
            {
                throw new SphException("目标文件已存在，已保留原文件未覆盖");
            }

            File.Move(tempPath, targetPath);
            published = true;
            return size;
        }
        catch (HttpRequestException ex)
        {
            throw new SphTransientException("网络请求失败或超时：" + ex.Message);
        }
        catch (IOException ex) when (!published)
        {
            throw new SphTransientException("写入下载文件失败：" + ex.Message);
        }
        finally
        {
            try { File.Delete(tempPath); } catch { }
        }
    }

    private static async Task<string> FetchHtmlAsync(string url, int timeoutSeconds, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Referer", BaseUrl + "/");
        using var response = await Http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token);
        var status = (int)response.StatusCode;
        if (status is >= 500 or 429)
        {
            throw new SphTransientException($"站点返回 HTTP {status}");
        }

        if (status != 200)
        {
            throw new SphException($"站点返回 HTTP {status}");
        }

        var bytes = await response.Content.ReadAsByteArrayAsync(cts.Token);
        return Gb18030.GetString(bytes);
    }

    private static string Clean(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var text = TagRegex.Replace(html, " ");
        return WebUtility.HtmlDecode(text).Replace('\u00a0', ' ').Trim();
    }

    private static string ProgressText(long size, long total)
    {
        var sizeText = size < 1024 * 1024 ? $"{size / 1024.0:F0} KB" : $"{size / 1024.0 / 1024.0:F1} MB";
        return total > 0 ? $"{sizeText}/{total / 1024.0 / 1024.0:F1} MB {size * 100 / total}%" : sizeText;
    }
}
