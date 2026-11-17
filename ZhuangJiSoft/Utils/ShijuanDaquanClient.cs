using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using DeskApp.Models;

namespace DeskApp.Utils;

/// <summary>站点详情页上的下载配额：「今日还可下载 N 套（每人每天 M 套）」。</summary>
public sealed record SjQuota(int Remaining, int DailyTotal);

public sealed class SjListPage
{
    public List<ShijuanDaquanItem> Items { get; } = new();

    public int TotalCount { get; set; }

    public int TotalPages { get; set; } = 1;
}

/// <summary>
/// 试卷大全（sjdaquan.com）客户端。站点为服务端渲染 HTML：
/// 列表/搜索页解析卡片，详情页读取剩余配额，登录走 POST /user/login，
/// 下载页 /download/{id}.html 会 302 到 OSS 签名 PDF 直链。
/// Cookie 由共享 HttpClientHandler 维护，登录态在进程内复用。
/// </summary>
public static class ShijuanDaquanClient
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private static readonly HttpClientHandler Handler = new()
    {
        AllowAutoRedirect = true,
        UseCookies = true,
        CookieContainer = new CookieContainer(),
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    };

    private static readonly HttpClient Http = new(Handler)
    {
        Timeout = Timeout.InfiniteTimeSpan
    };

    private static readonly Regex CardStartRegex = new(@"class=""pc-card", RegexOptions.Compiled);
    private static readonly Regex IdRegex = new(@"/(?:shijuan|download)/(\d+)\.html", RegexOptions.Compiled);
    private static readonly Regex TitleRegex = new(
        @"<div\s+class=""pc-title"">(.*?)</div>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex AttrsRegex = new(
        @"<div\s+class=""pc-attrs"">(.*?)</div>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TagRegex = new(
        @"<span\s+class=""pc-tag[^""]*"">(.*?)</span>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex GradeTagRegex = new(
        @"<span\s+class=""pc-tag[^""]*pc-tag-grade[^""]*"">(.*?)</span>",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex StatRegex = new(
        @"<span\s+class=""pc-stat"">(.*?)</span>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex BadgeRegex = new(
        @"<span\s+class=""pc-badge[^""]*"">(.*?)</span>", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex TotalCountRegex = new(@"共\s*([\d,]+)\s*套", RegexOptions.Compiled);
    // 站点分页链接在 HTML 里常写成 &amp;page=2，两种都要认
    private static readonly Regex PageRegex = new(@"(?:[?&]|&amp;)page=(\d+)", RegexOptions.Compiled);
    private static readonly Regex TagStripRegex = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex QuotaRegex = new(
        @"今日还可下载\s*(\d+)\s*套\s*[（(]\s*每人每天\s*(\d+)\s*套\s*[)）]", RegexOptions.Compiled);
    private static readonly Regex RemainingRegex = new(@"今日还可下载\s*(\d+)\s*套", RegexOptions.Compiled);
    private static readonly Regex DailyRegex = new(@"每人每天\s*(\d+)\s*套", RegexOptions.Compiled);

    /// <summary>抓一个列表/搜索页。</summary>
    public static async Task<SjListPage> FetchListAsync(
        SjQuery query, int timeoutSeconds, CancellationToken ct)
    {
        var html = await GetHtmlAsync(ShijuanDaquanCatalog.BuildUrl(query), timeoutSeconds, ct);
        var result = new SjListPage();

        var total = TotalCountRegex.Match(html);
        if (total.Success && int.TryParse(total.Groups[1].Value.Replace(",", string.Empty), out var count))
        {
            result.TotalCount = count;
        }

        foreach (Match page in PageRegex.Matches(html))
        {
            if (int.TryParse(page.Groups[1].Value, out var p) && p > result.TotalPages && p <= 10000)
            {
                result.TotalPages = p;
            }
        }

        var starts = CardStartRegex.Matches(html);
        for (var i = 0; i < starts.Count; i++)
        {
            var start = starts[i].Index;
            var end = i + 1 < starts.Count ? starts[i + 1].Index : html.Length;
            var block = html[start..Math.Min(end, start + 4000)];

            var idMatch = IdRegex.Match(block);
            if (!idMatch.Success || !int.TryParse(idMatch.Groups[1].Value, out var id))
            {
                continue;
            }

            var titleMatch = TitleRegex.Match(block);
            if (!titleMatch.Success)
            {
                continue;
            }

            var title = Clean(titleMatch.Groups[1].Value);
            if (string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var attrs = AttrsRegex.Match(block).Success
                ? Clean(AttrsRegex.Match(block).Groups[1].Value)
                : string.Empty;
            var parts = attrs.Split('·', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var version = parts.Length > 0 ? parts[0] : string.Empty;
            var year = parts.Length > 1 ? parts[1] : string.Empty;

            var tags = TagRegex.Matches(block)
                .Select(m => Clean(m.Groups[1].Value))
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .ToList();
            var gradeTag = GradeTagRegex.Match(block);
            var grade = gradeTag.Success ? Clean(gradeTag.Groups[1].Value) : string.Empty;
            var paperType = tags.Count > 0 ? tags[0] : string.Empty;
            if (string.IsNullOrWhiteSpace(paperType))
            {
                var badge = BadgeRegex.Match(block);
                paperType = badge.Success ? Clean(badge.Groups[1].Value) : string.Empty;
            }

            if (string.IsNullOrWhiteSpace(grade) && tags.Count > 1)
            {
                grade = tags[1];
            }

            result.Items.Add(new ShijuanDaquanItem
            {
                Id = id,
                Title = title,
                Version = version,
                Year = year,
                PaperType = paperType,
                Grade = grade,
                Rating = StatRegex.Match(block) is { Success: true } stat ? Clean(stat.Groups[1].Value) : string.Empty,
                DetailUrl = ShijuanDaquanCatalog.DetailUrl(id),
                DownloadUrl = ShijuanDaquanCatalog.DownloadUrl(id)
            });
        }

        return result;
    }

    /// <summary>读取详情页上的今日剩余下载次数（该请求不消耗下载配额）。</summary>
    public static async Task<SjQuota?> FetchQuotaAsync(int id, int timeoutSeconds, CancellationToken ct)
    {
        var html = await GetHtmlAsync(ShijuanDaquanCatalog.DetailUrl(id), timeoutSeconds, ct);
        var text = Clean(html);

        var full = QuotaRegex.Match(text);
        if (full.Success)
        {
            return new SjQuota(int.Parse(full.Groups[1].Value), int.Parse(full.Groups[2].Value));
        }

        var remaining = RemainingRegex.Match(text);
        var daily = DailyRegex.Match(text);
        if (remaining.Success)
        {
            var total = daily.Success ? int.Parse(daily.Groups[1].Value) : 0;
            return new SjQuota(int.Parse(remaining.Groups[1].Value), total);
        }

        return null;
    }

    /// <summary>
    /// 站点登录：POST /user/login（multipart FormData），返回 JSON { code, msg, url }。
    /// 成功时 CookieContainer 自动保存 PHPSESSID。
    /// </summary>
    public static async Task<(bool Ok, string Message)> LoginAsync(
        string username, string password, int timeoutSeconds, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var form = new MultipartFormDataContent();
        form.Add(new StringContent(username.Trim(), Encoding.UTF8), "username");
        form.Add(new StringContent(password, Encoding.UTF8), "password");
        form.Add(new StringContent("/user/center", Encoding.UTF8), "back");

        using var request = new HttpRequestMessage(HttpMethod.Post,
            ShijuanDaquanCatalog.BaseUrl + "/user/login")
        {
            Content = form
        };
        using var response = await Http.SendAsync(request, linked.Token);
        var json = await response.Content.ReadAsStringAsync(linked.Token);

        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            var code = -1;
            if (root.TryGetProperty("code", out var codeElement))
            {
                if (codeElement.ValueKind == JsonValueKind.Number && codeElement.TryGetInt32(out var n))
                {
                    code = n;
                }
                else if (codeElement.ValueKind == JsonValueKind.String &&
                         int.TryParse(codeElement.GetString(), out var n2))
                {
                    code = n2;
                }
            }

            var message = root.TryGetProperty("msg", out var msgElement)
                ? msgElement.GetString() ?? string.Empty
                : string.Empty;
            return (code == 0 && response.IsSuccessStatusCode,
                string.IsNullOrWhiteSpace(message) ? (code == 0 ? "登录成功" : "登录失败") : message);
        }
        catch (JsonException)
        {
            return (false, "站点返回异常：" + Truncate(Clean(json), 80));
        }
    }

    /// <summary>退出登录：让本进程的站点 Cookie 全部过期。</summary>
    public static void Logout()
    {
        foreach (Cookie cookie in Handler.CookieContainer.GetCookies(new Uri(ShijuanDaquanCatalog.BaseUrl)))
        {
            cookie.Expired = true;
        }
    }

    /// <summary>
    /// 下载一份试卷：/download/{id}.html → 302 → OSS 签名 PDF 直链。
    /// 若站点返回 HTML（配额用尽/需验证），把页面提示作为失败原因返回，不做任何绕过。
    /// </summary>
    public static async Task<(bool Ok, string Message, long Bytes)> DownloadAsync(
        int id, string targetPath, int timeoutSeconds, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var response = await Http.GetAsync(
            ShijuanDaquanCatalog.DownloadUrl(id), HttpCompletionOption.ResponseHeadersRead, linked.Token);
        var media = response.Content.Headers.ContentType?.MediaType ?? string.Empty;
        if (!response.IsSuccessStatusCode || media.Contains("text/html", StringComparison.OrdinalIgnoreCase))
        {
            var text = Clean(await response.Content.ReadAsStringAsync(linked.Token));
            var reason = string.IsNullOrWhiteSpace(text)
                ? $"站点返回 HTTP {(int)response.StatusCode}"
                : Truncate(text, 120);
            return (false, reason, 0);
        }

        var temp = targetPath + ".part";
        try
        {
            await using (var source = await response.Content.ReadAsStreamAsync(linked.Token))
            await using (var dest = new FileStream(temp, FileMode.Create, FileAccess.Write,
                             FileShare.None, 81920, useAsync: true))
            {
                await source.CopyToAsync(dest, linked.Token);
            }

            // 站点文件是 PDF；非 PDF 说明被限额页/验证页拦截，避免把 HTML 存成 .pdf
            var head = new byte[5];
            await using (var check = new FileStream(temp, FileMode.Open, FileAccess.Read,
                             FileShare.Read, 8, useAsync: true))
            {
                _ = await check.ReadAsync(head, linked.Token);
            }

            if (Encoding.ASCII.GetString(head) != "%PDF-")
            {
                File.Delete(temp);
                return (false, "返回的不是 PDF 文件，可能已触发下载限制", 0);
            }

            File.Move(temp, targetPath, overwrite: true);
            return (true, string.Empty, new FileInfo(targetPath).Length);
        }
        catch (OperationCanceledException)
        {
            TryDelete(temp);
            throw;
        }
        catch
        {
            TryDelete(temp);
            throw;
        }
    }

    private static async Task<string> GetHtmlAsync(string url, int timeoutSeconds, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        using var response = await Http.SendAsync(request, linked.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(linked.Token);
    }

    private static string Clean(string html)
    {
        var text = WebUtility.HtmlDecode(TagStripRegex.Replace(html, " "));
        return Regex.Replace(text, @"\s+", " ").Trim();
    }

    private static string Truncate(string value, int max) =>
        value.Length <= max ? value : value[..max] + "…";

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // 清理临时文件失败不影响主流程
        }
    }
}
