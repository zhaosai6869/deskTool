using System;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeskApp.Utils;

/// <summary>
/// 抖音分享链接解析（路线 A：分享页协议，参考 aiaoxd/douyinDownloader 的协议模式）：
/// ① v.douyin.com 短链跟随重定向展开出视频 ID（也接受 video/、modal_id=、note/ 完整链接）；
/// ② 用移动端 UA + 访客 Cookie 请求 https://www.douyin.com/share/video/&lt;id&gt;/，页面内嵌
///    window._ROUTER_DATA JSON 里 loaderData[*].videoInfoRes.item_list[0] 即作品对象
///    （2026-11 起无 Cookie 只返回壳页，Cookie 由 DouyinGuestAuth 提供）；
/// ③ 分享页只给带水印的 720p playwm，用 play_addr.uri 换 /aweme/v1/play/?ratio=1080p
///    接口拿无水印 1080p（已含音轨单文件）。
/// 访客 Cookie 失效（壳页）抛 DouyinGuestStaleException，由调用方刷新后重试。
/// </summary>
public partial class DouyinClient
{
    /// <summary>桌面端 UA：短链展开与视频下载使用。</summary>
    public const string DesktopUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private const string MobileUserAgent =
        "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1";

    /// <summary>下载视频的 Referer（防盗链要求）。</summary>
    public const string Referer = "https://www.douyin.com/";

    /// <summary>协议模式清晰度上限 1080p（实测 4k/origin 都回落）。</summary>
    private const string Ratio = "1080p";

    private readonly HttpClient _http;

    public DouyinClient(int timeoutSeconds)
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
    /// 解析分享链接。输入可以是 v.douyin.com 短链、douyin.com/video/xxx 完整链接或 modal_id 链接；
    /// guestCookie 为 DouyinGuestAuth 提供的访客 Cookie 头。
    /// 返回归一化视频页链接、媒体直链、标题、作者。网络/风控类失败抛 SphTransientException，
    /// 访客 Cookie 失效抛 DouyinGuestStaleException，其余业务失败抛 SphException。
    /// </summary>
    public async Task<DouyinVideo> ResolveAsync(string sourceUrl, string? guestCookie, CancellationToken ct)
    {
        var videoId = await ExtractVideoIdAsync(sourceUrl, ct);
        if (string.IsNullOrEmpty(videoId))
        {
            throw new SphException("链接无效：请输入抖音分享链接（v.douyin.com 短链或 douyin.com/video/xxx）");
        }

        var canonical = $"https://www.douyin.com/video/{videoId}";
        var item = await FetchShareItemAsync(videoId, guestCookie, ct);
        if (item is null)
        {
            throw new DouyinGuestStaleException("访客 Cookie 失效，需要重新获取");
        }

        var aweme = item.Value;

        // 图文 / 实况图作品：本项目只做普通视频
        if (aweme.TryGetProperty("images", out var images) &&
            images.ValueKind == JsonValueKind.Array && images.GetArrayLength() > 0)
        {
            throw new SphException("该作品是图文或实况图，暂只支持普通视频");
        }

        if (!aweme.TryGetProperty("video", out var video) ||
            !video.TryGetProperty("play_addr", out var playAddr))
        {
            throw new SphException("作品数据缺少视频地址");
        }

        // 首选 play_addr.uri 换无水印 play 接口；缺失时兜底把 url_list 里的 playwm 改成 play
        string? uri = null;
        if (playAddr.TryGetProperty("uri", out var uriEl) && uriEl.ValueKind == JsonValueKind.String)
        {
            uri = uriEl.GetString();
        }

        string mediaUrl;
        if (!string.IsNullOrEmpty(uri))
        {
            mediaUrl = $"https://aweme.snssdk.com/aweme/v1/play/?ratio={Ratio}&video_id={uri}";
        }
        else
        {
            var fallback = FirstUrl(playAddr);
            if (string.IsNullOrEmpty(fallback))
            {
                throw new SphException("作品数据缺少有效的视频下载地址");
            }

            mediaUrl = fallback.Replace("playwm", "play", StringComparison.Ordinal);
        }

        var title = StringOrNull(aweme, "desc") ?? "douyin";
        var author = aweme.TryGetProperty("author", out var authorEl)
            ? StringOrNull(authorEl, "nickname") ?? string.Empty
            : string.Empty;

        return new DouyinVideo(canonical, mediaUrl, title, author, videoId);
    }

    /// <summary>从分享链接 / 视频页 / modal_id 链接中解析视频 ID；短链先跟随重定向展开。</summary>
    public async Task<string?> ExtractVideoIdAsync(string link, CancellationToken ct)
    {
        var value = link.Trim();
        if (value.Contains("v.douyin.com", StringComparison.OrdinalIgnoreCase))
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, value);
            request.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            // HttpClient 的 ResponseMessage.RequestUri 就是重定向后的最终地址
            value = response.RequestMessage?.RequestUri?.ToString() ?? value;
        }

        return VideoIdRegex().IsMatch(value) ? VideoIdRegex().Match(value).Groups[1].Value : null;
    }

    /// <summary>请求分享页并取出 item_list[0]（作品对象）。壳页（无数据）返回 null 表示访客 Cookie 失效。</summary>
    private async Task<JsonElement?> FetchShareItemAsync(string videoId, string? guestCookie, CancellationToken ct)
    {
        var url = $"https://www.douyin.com/share/video/{videoId}/";
        string html;
        using (var request = new HttpRequestMessage(HttpMethod.Get, url))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", MobileUserAgent);
            request.Headers.TryAddWithoutValidation("Referer", Referer);
            request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml,application/xml;q=0.9,*/*;q=0.8");
            if (!string.IsNullOrEmpty(guestCookie))
            {
                request.Headers.TryAddWithoutValidation("Cookie", guestCookie);
            }

            using var response = await _http.SendAsync(request, ct);
            var status = (int)response.StatusCode;
            if (status is >= 500 or 429)
            {
                throw new SphTransientException($"分享页返回 HTTP {status}");
            }

            if (status is < 200 or >= 300)
            {
                throw new SphException($"分享页返回 HTTP {status}");
            }

            html = await response.Content.ReadAsStringAsync(ct);
        }

        if (!html.Contains("_ROUTER_DATA", StringComparison.Ordinal))
        {
            // 无 _ROUTER_DATA：访客 Cookie 失效（壳页）或命中 argus 风控挑战页
            return null;
        }

        var match = RouterDataRegex().Match(html);
        if (!match.Success)
        {
            throw new SphException("分享页数据无法定位");
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(match.Groups[1].Value);
        }
        catch (JsonException)
        {
            throw new SphException("分享页数据解析失败");
        }

        using (doc)
        {
            if (!doc.RootElement.TryGetProperty("loaderData", out var loaderData) ||
                loaderData.ValueKind != JsonValueKind.Object)
            {
                throw new SphException("分享页数据缺少 loaderData");
            }

            foreach (var node in loaderData.EnumerateObject())
            {
                if (node.Value.ValueKind == JsonValueKind.Object &&
                    node.Value.TryGetProperty("videoInfoRes", out var videoInfoRes) &&
                    videoInfoRes.ValueKind == JsonValueKind.Object &&
                    videoInfoRes.TryGetProperty("item_list", out var itemList) &&
                    itemList.ValueKind == JsonValueKind.Array &&
                    itemList.GetArrayLength() > 0)
                {
                    return itemList[0].Clone();
                }
            }
        }

        // 有 _ROUTER_DATA 但没有 videoInfoRes.item_list：同样是壳页（Cookie 失效）
        return null;
    }

    private static string? FirstUrl(JsonElement playAddr)
    {
        if (playAddr.TryGetProperty("url_list", out var urls) && urls.ValueKind == JsonValueKind.Array)
        {
            foreach (var u in urls.EnumerateArray())
            {
                if (u.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(u.GetString()))
                {
                    return u.GetString();
                }
            }
        }

        return null;
    }

    private static string? StringOrNull(JsonElement parent, string name)
    {
        return parent.ValueKind == JsonValueKind.Object &&
               parent.TryGetProperty(name, out var v) &&
               v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }

    [GeneratedRegex(@"(?:video/|modal_id=|note/)(\d+)")]
    private static partial Regex VideoIdRegex();

    [GeneratedRegex(@"_ROUTER_DATA\s*=\s*(\{.*?\})\s*</script>", RegexOptions.Singleline)]
    private static partial Regex RouterDataRegex();
}

/// <summary>解析结果快照：归一化视频页链接（去重键）、媒体直链、标题、作者、视频 ID。</summary>
public record DouyinVideo(string CanonicalUrl, string MediaUrl, string Title, string Author, string VideoId);

/// <summary>访客 Cookie 失效（分享页只返回壳页）：刷新 DouyinGuestAuth 后重试一次即可恢复。</summary>
public class DouyinGuestStaleException : Exception
{
    public DouyinGuestStaleException(string message) : base(message) { }
}
