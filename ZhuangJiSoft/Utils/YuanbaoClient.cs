using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DeskApp.Utils;

/// <summary>
/// 视频号分享链接解析（参考 wx-downloader client.py 的两步解析）：
/// ① POST yuanbao.tencent.com/api/weixin/get_parse_result（带元宝 Cookie）拿到 playable_url；
/// ② 用 playable_url 里的 token/eid 调 channels.weixin.qq.com finder-preview 接口拿媒体直链。
/// 第二步不带元宝 Cookie；返回标题/作者/媒体地址。
/// </summary>
public class YuanbaoClient
{
    public const string DefaultUserAgent =
        "Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/148.0.0.0 Safari/537.36";

    private const string PreviewOrigin = "https://channels.weixin.qq.com";

    private const string FeedInfoUrl = PreviewOrigin + "/finder-preview/api/feed/get_feed_info";

    private readonly HttpClient _http;
    private readonly YuanbaoAuth _auth;

    public YuanbaoClient(YuanbaoAuth auth, int timeoutSeconds)
    {
        _auth = auth;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseCookies = false
        })
        {
            Timeout = TimeSpan.FromSeconds(timeoutSeconds)
        };
    }

    /// <summary>解析分享链接；quality 为 h264 / h265。失败抛 SphException（中文提示）。</summary>
    public async Task<SphVideo> ResolveAsync(string sourceUrl, string quality, CancellationToken ct)
    {
        var headers = new Dictionary<string, string>
        {
            ["User-Agent"] = string.IsNullOrEmpty(_auth.UserAgent) ? DefaultUserAgent : _auth.UserAgent,
            ["Origin"] = YuanbaoAuth.Origin,
            ["Referer"] = YuanbaoAuth.Origin + "/",
            ["Accept"] = "application/json",
            ["x-source"] = "web",
            ["Cookie"] = _auth.Cookie
        };
        foreach (var name in YuanbaoAuth.ExtraHeaderNames)
        {
            if (_auth.ExtraHeaders.TryGetValue(name, out var value) && !string.IsNullOrEmpty(value))
            {
                headers[name] = value;
            }
        }

        // ① 元宝解析接口：分享链接 → 视频号预览地址（playable_url，含 token/eid）
        var parseBody = JsonSerializer.Serialize(new
        {
            type = "video_channel_url",
            url = sourceUrl,
            scene = 1
        });

        JsonDocument parsed;
        using (var request = NewRequest(YuanbaoAuth.ParseUrl, parseBody, headers))
        using (var response = await _http.SendAsync(request, ct))
        {
            parsed = await ReadJsonAsync(response, "元宝解析接口");
        }

        using (parsed)
        {
            var code = parsed.RootElement.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number
                ? c.GetInt32() : -1;
            if (code != 0)
            {
                throw new SphException($"元宝解析失败（code={code}），请重新登录并检查链接");
            }

            if (!TryGet(parsed.RootElement, "data", out var data) ||
                !TryGetValue(data, "playable_url", out var playableEl) ||
                playableEl.ValueKind != JsonValueKind.String)
            {
                throw new SphException("元宝未返回视频预览地址");
            }

            var playableUrl = playableEl.GetString()!;
            if (!Uri.TryCreate(playableUrl, UriKind.Absolute, out var playable) ||
                playable.Scheme != "https" ||
                playable.Host != "channels.weixin.qq.com")
            {
                throw new SphException("元宝未返回有效的预览 token/eid");
            }

            var query = ParseQuery(playable.Query);
            if (!query.ContainsKey("token") || !query.ContainsKey("eid"))
            {
                throw new SphException("元宝未返回有效的预览 token/eid");
            }

            return await GetFeedInfoAsync(playableUrl, query["token"], query["eid"],
                headers, quality, ct, data);
        }
    }

    /// <summary>② 视频号预览接口：token/eid → 媒体直链（不带元宝 Cookie）。parsedData 为元宝返回的 data，用于标题兜底。</summary>
    private async Task<SphVideo> GetFeedInfoAsync(
        string playableUrl, string token, string eid, Dictionary<string, string> headers,
        string quality, CancellationToken ct, JsonElement parsedData)
    {
        var rid = ((long)DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString("x") + "-" +
                  Convert.ToHexString(RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        var feedUrl = $"{FeedInfoUrl}?_rid={Uri.EscapeDataString(rid)}" +
                      $"&_pageUrl={Uri.EscapeDataString(PreviewOrigin + "/finder-preview/pages/feed")}";

        var feedBody = JsonSerializer.Serialize(new
        {
            baseReq = new { generalToken = token },
            exportId = eid
        });

        JsonDocument profile;
        using (var request = NewRequest(feedUrl, feedBody, new Dictionary<string, string>
                 {
                     ["User-Agent"] = headers["User-Agent"],
                     ["Origin"] = PreviewOrigin,
                     ["Referer"] = playableUrl,
                     ["Accept"] = "application/json",
                     ["Content-Type"] = "application/json"
                 }))
        using (var response = await _http.SendAsync(request, ct))
        {
            profile = await ReadJsonAsync(response, "视频号预览接口");
        }

        using (profile)
        {
            var root = profile.RootElement;
            var errCode = root.TryGetProperty("errCode", out var ec) && ec.ValueKind == JsonValueKind.Number
                ? ec.GetInt32() : 0;
            if (!TryGet(root, "data", out var data) ||
                !TryGet(data, "feedInfo", out var feed))
            {
                throw new SphException("视频号预览失败，视频可能已删除、不可见或预览凭证失效");
            }

            // errMsg 里任意字段有值都视为预览失败（与原实现一致）
            if (TryGet(data, "errMsg", out var errMsg))
            {
                foreach (var prop in errMsg.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String &&
                        !string.IsNullOrEmpty(prop.Value.GetString()))
                    {
                        throw new SphException("视频号预览失败，视频可能已删除、不可见或预览凭证失效");
                    }
                }
            }

            if (errCode != 0 ||
                !feed.TryGetProperty("mediaType", out var mediaType) ||
                mediaType.ValueKind != JsonValueKind.Number || mediaType.GetInt32() != 4)
            {
                throw new SphException("链接不是可下载的普通视频，暂不支持图集或直播");
            }

            // 按编码偏好取直链：h264VideoInfo.videoUrl → feed.videoUrl → h265VideoInfo.videoUrl
            var fallback = quality == "h264" ? "h265" : "h264";
            var media = VideoUrl(feed, quality) ?? StringOrNull(feed, "videoUrl") ?? VideoUrl(feed, fallback);
            if (string.IsNullOrEmpty(media))
            {
                throw new SphException("视频详情缺少有效的媒体下载地址");
            }

            var title = StringOrNull(feed, "description")
                        ?? StringOrNull(parsedData, "desc")
                        ?? "video";
            var author = StringOrNull(data, "authorInfo", "nickname")
                         ?? StringOrNull(parsedData, "author")
                         ?? string.Empty;

            return new SphVideo(media, title, author);
        }
    }

    private static HttpRequestMessage NewRequest(
        string url, string body, Dictionary<string, string> headers)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        foreach (var (key, value) in headers)
        {
            if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
            {
                continue; // StringContent 已设置
            }

            request.Headers.TryAddWithoutValidation(key, value);
        }

        return request;
    }

    private static async Task<JsonDocument> ReadJsonAsync(HttpResponseMessage response, string stage)
    {
        var status = (int)response.StatusCode;
        if (status is >= 500 or 429)
        {
            throw new SphTransientException($"{stage}返回 HTTP {status}");
        }

        if (status is 401 or 403)
        {
            throw new SphException($"{stage}返回 HTTP {status}，请重新登录元宝");
        }

        if (status is < 200 or >= 300)
        {
            throw new SphException($"{stage}返回 HTTP {status}");
        }

        try
        {
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        }
        catch (JsonException)
        {
            throw new SphException($"{stage}返回非 JSON 数据");
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>();
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = part.Split('=', 2);
            if (kv.Length == 2)
            {
                result[Uri.UnescapeDataString(kv[0])] = Uri.UnescapeDataString(kv[1]);
            }
        }

        return result;
    }

    /// <summary>取子对象（值必须是 JSON 对象）。</summary>
    private static bool TryGet(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object &&
               parent.TryGetProperty(name, out value) &&
               value.ValueKind == JsonValueKind.Object;
    }

    /// <summary>取任意类型属性（不约束 JSON 类型，字符串/数字都能取）。</summary>
    private static bool TryGetValue(JsonElement parent, string name, out JsonElement value)
    {
        value = default;
        return parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(name, out value);
    }

    private static string? StringOrNull(JsonElement parent, string name)
    {
        return parent.ValueKind == JsonValueKind.Object &&
               parent.TryGetProperty(name, out var v) &&
               v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;
    }

    private static string? StringOrNull(JsonElement parent, string child, string name)
    {
        return TryGet(parent, child, out var c) ? StringOrNull(c, name) : null;
    }

    private static string? VideoUrl(JsonElement feed, string codec)
    {
        return TryGet(feed, codec + "VideoInfo", out var info) ? StringOrNull(info, "videoUrl") : null;
    }
}

/// <summary>解析结果快照：媒体直链、标题、作者。</summary>
public record SphVideo(string MediaUrl, string Title, string Author);

/// <summary>不可重试的业务错误（登录失效、链接无效、视频不可见等）。</summary>
public class SphException : Exception
{
    public SphException(string message) : base(message) { }
}

/// <summary>可重试的临时错误（网络失败、429/5xx）。</summary>
public class SphTransientException : Exception
{
    public SphTransientException(string message) : base(message) { }
}
