using System;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeskApp.Utils;

/// <summary>
/// 快手分享链接解析（参考 JoeanAmier/KS-Downloader 的 live_api 路线，仅借思路、自研实现）：
/// ① v.kuaishou.com 短链（或 kuaishou.com 完整链接）跟随重定向得到
///    www.kuaishou.com/short-video/&lt;作品ID&gt;?...&amp;photoId=...&amp;userId=...；
/// ② 从落地 URL 提取 photoId（路径）与 principalId（userId/efid 查询参数）；
/// ③ GET https://live.kuaishou.com/live_api/profile/feedbyid?photoId=&amp;principalId=
///    （桌面 UA + PC_DATA_HEADERS 式请求头，实测无需 Cookie / TLS 指纹模拟），
///    data.currentWork.playUrl 即无水印 MP4 直链，author.name 为作者名。
/// 已过期短链会重定向到 new-reco 首页 →「链接无效」；图文作品不支持。
/// </summary>
public partial class KuaishouClient
{
    /// <summary>桌面端 UA：短链展开、API 与下载共用。</summary>
    public const string DesktopUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36";

    /// <summary>下载媒体的 Referer（防盗链要求）。</summary>
    public const string Referer = "https://www.kuaishou.com/";

    private const string FeedByIdApi = "https://live.kuaishou.com/live_api/profile/feedbyid";

    private readonly HttpClient _http;

    public KuaishouClient(int timeoutSeconds)
    {
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = true,
            UseCookies = false,
            AutomaticDecompression = System.Net.DecompressionMethods.All
        })
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
    }

    /// <summary>
    /// 解析分享链接。输入可以是 v.kuaishou.com 短链或 kuaishou.com/short-video|long-video|photo 完整链接。
    /// 返回归一化作品链接、无水印媒体直链、标题、作者。网络类失败抛 SphTransientException，业务失败抛 SphException。
    /// </summary>
    public async Task<KuaishouVideo> ResolveAsync(string sourceUrl, CancellationToken ct)
    {
        // ① 短链展开（HttpClient 自动跟随重定向，最终地址即落地 URL）
        string finalUrl;
        using (var request = new HttpRequestMessage(HttpMethod.Get, sourceUrl.Trim()))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? sourceUrl.Trim();
        }

        // ② 提取作品类型与 ID；过期短链会落到 new-reco 等首页路径
        var match = ContentIdRegex().Match(finalUrl);
        if (!match.Success)
        {
            throw new SphException("链接无效：请输入快手分享链接（App 里 分享 → 复制链接），短链可能已过期");
        }

        var contentType = match.Groups[1].Value;
        var contentId = match.Groups[2].Value;
        if (contentType == "photo")
        {
            throw new SphException("该作品是图片作品，暂只支持普通视频");
        }

        var canonical = $"https://www.kuaishou.com/{contentType}/{contentId}";

        // principalId：落地 URL 的 userId（缺失时用 efid 兜底）
        var principalId = QueryValue(finalUrl, "userId") ?? QueryValue(finalUrl, "efid") ?? string.Empty;

        // ③ live_api feedbyid 取作品数据
        var work = await FetchCurrentWorkAsync(contentId, principalId, ct);

        // 图文集：imgUrls 非空
        if (TryGetValueKind(work, "imgUrls", JsonValueKind.Array, out var imgs) && imgs.GetArrayLength() > 0)
        {
            throw new SphException("该作品是图文图集，暂只支持普通视频");
        }

        if (!TryGetValueKind(work, "playUrl", JsonValueKind.String, out var playUrl) ||
            string.IsNullOrEmpty(playUrl.GetString()))
        {
            throw new SphException("作品数据缺少有效的视频下载地址（作品可能已删除）");
        }

        var author = Field(work, "author") is { ValueKind: JsonValueKind.Object } authorNode &&
                     Field(authorNode, "name") is { ValueKind: JsonValueKind.String } nameNode
            ? nameNode.GetString() ?? string.Empty
            : string.Empty;

        // 该接口不含文案，标题用「快手作品」，文件名以 作者-哈希 区分
        return new KuaishouVideo(canonical, playUrl.GetString()!, "快手作品", author, contentId);
    }

    /// <summary>调用 live_api/profile/feedbyid，返回 data.currentWork 对象；result != 1 时按业务失败处理。</summary>
    private async Task<JsonElement> FetchCurrentWorkAsync(string photoId, string principalId, CancellationToken ct)
    {
        var api = $"{FeedByIdApi}?photoId={Uri.EscapeDataString(photoId)}&principalId={Uri.EscapeDataString(principalId)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, api);
        request.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        request.Headers.TryAddWithoutValidation("Origin", "https://www.kuaishou.com");
        request.Headers.TryAddWithoutValidation("Referer", "https://www.kuaishou.com/new-reco?source=NewReco");

        using var response = await _http.SendAsync(request, ct);
        var status = (int)response.StatusCode;
        if (status is >= 500 or 429)
        {
            throw new SphTransientException($"接口返回 HTTP {status}");
        }

        if (status is < 200 or >= 300)
        {
            throw new SphException($"接口返回 HTTP {status}");
        }

        JsonDocument doc;
        try
        {
            doc = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        }
        catch (JsonException)
        {
            throw new SphException("接口返回的数据无法解析");
        }

        using (doc)
        {
            if (!TryGetValueKind(doc.RootElement, "data", JsonValueKind.Object, out var data) ||
                !TryGetValueKind(data, "currentWork", JsonValueKind.Object, out var work))
            {
                throw new SphException("作品不存在或已删除（分享链接可能已过期）");
            }

            return work.Clone();
        }
    }

    /// <summary>从 URL 查询串里取指定参数（不区分大小写键）。</summary>
    private static string? QueryValue(string url, string key)
    {
        var idx = url.IndexOf('?', StringComparison.Ordinal);
        if (idx < 0)
        {
            return null;
        }

        foreach (var pair in url[(idx + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && string.Equals(pair[..eq], key, StringComparison.OrdinalIgnoreCase))
            {
                return Uri.UnescapeDataString(pair[(eq + 1)..]);
            }
        }

        return null;
    }

    private static bool TryGetValueKind(JsonElement parent, string name, JsonValueKind kind, out JsonElement value)
    {
        if (parent.ValueKind == JsonValueKind.Object &&
            parent.TryGetProperty(name, out var v) && v.ValueKind == kind)
        {
            value = v;
            return true;
        }

        value = default;
        return false;
    }

    /// <summary>取对象属性的 JsonElement（不存在或类型不符时返回 null 元组）。</summary>
    private static JsonElement? Field(JsonElement parent, string name)
    {
        return parent.ValueKind == JsonValueKind.Object &&
               parent.TryGetProperty(name, out var v)
            ? v
            : null;
    }

    [GeneratedRegex(@"(short-video|long-video|photo)/([A-Za-z0-9_-]+)")]
    private static partial Regex ContentIdRegex();
}

/// <summary>解析结果快照：归一化作品链接（去重键）、无水印媒体直链、标题、作者、作品 ID。</summary>
public record KuaishouVideo(string CanonicalUrl, string MediaUrl, string Title, string Author, string VideoId);
