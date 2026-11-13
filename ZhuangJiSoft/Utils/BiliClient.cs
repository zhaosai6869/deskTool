using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeskApp.Utils;

/// <summary>
/// B站视频链接解析（参考 BBDown（MIT，已归档）与 bilibili-API-collect 社区文档的思路，自研实现）：
/// ① b23.tv 短链 / bilibili.com 完整链接跟随重定向，从落地 URL 提取 BV 号（或 av 号）与 ?p= 分P序号；
/// ② GET https://api.bilibili.com/x/web-interface/view → videoData（aid/cid/标题/UP主/分P 列表），无需签名；
/// ③ WBI 签名后 GET https://api.bilibili.com/x/player/wbi/playurl?fnval=0&qn=80
///    → data.durl[0].url 单文件 MP4 直链（免登录最高 720P，qn 请求 1080P 由服务器自动回落）。
/// WBI 签名：nav 接口取 wbi_img.img_url/sub_url 文件名 → 64 字符原钥 → 固定 64 位乱序表重排取前 32
/// → mixin_key；参数含 wts 按键排序、值过滤 !'()* 、URL 编码拼 query → w_rid = MD5(query + mixin_key)。
/// CDN 下载需 Referer https://www.bilibili.com/。多段 durl（超长视频）暂不支持。
/// </summary>
public partial class BiliClient
{
    /// <summary>桌面端 UA：短链展开、API 与下载共用。</summary>
    public const string DesktopUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36";

    /// <summary>下载媒体的 Referer（upos CDN 防盗链要求）。</summary>
    public const string Referer = "https://www.bilibili.com/";

    /// <summary>WBI 混淆索引表（社区公开的客户端固定表）。</summary>
    private static readonly int[] MixinKeyEncTab =
    {
        46, 47, 18, 2, 53, 8, 23, 32, 15, 50, 10, 31, 58, 3, 45, 35, 27, 43, 5, 49,
        33, 9, 42, 19, 29, 28, 14, 39, 12, 38, 41, 13, 37, 48, 7, 16, 24, 55, 40, 61,
        26, 17, 0, 1, 60, 51, 30, 4, 22, 25, 54, 21, 56, 59, 6, 63, 57, 62, 11, 36,
        20, 34, 44, 52
    };

    /// <summary>mixin_key 进程级缓存（B站每日轮换 img/sub key，缓存 6 小时）。</summary>
    private static string? _cachedMixinKey;
    private static DateTime _mixinKeyAt;

    private readonly HttpClient _http;

    public BiliClient(int timeoutSeconds)
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
    /// 解析视频链接。输入可以是 b23.tv 短链、www.bilibili.com/video/BVxxx（含 ?p=N）或 /av数字 完整链接。
    /// 返回归一化视频链接、MP4 直链、标题（多分P 带分P名）、UP 主昵称。网络类失败抛 SphTransientException，
    /// 业务失败抛 SphException。
    /// </summary>
    public async Task<BiliVideo> ResolveAsync(string sourceUrl, CancellationToken ct)
    {
        var input = sourceUrl.Trim();

        // ① 短链/完整链接跟随重定向，从落地 URL 提取 BV/av 号与 p 序号
        string finalUrl;
        using (var request = new HttpRequestMessage(HttpMethod.Get, input))
        {
            request.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);
            request.Headers.TryAddWithoutValidation("Referer", Referer);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            finalUrl = response.RequestMessage?.RequestUri?.ToString() ?? input;
        }

        var bvid = BvidRegex().Match(finalUrl).Groups[1].Value;
        if (string.IsNullOrEmpty(bvid))
        {
            var avMatch = AvIdRegex().Match(finalUrl);
            if (!avMatch.Success)
            {
                throw new SphException("链接无效：请输入B站视频链接（www.bilibili.com/video/BV… 或 b23.tv 短链）");
            }

            bvid = "av" + avMatch.Groups[1].Value;
        }

        var page = ParsePageParam(finalUrl);

        // ② view API 取视频数据（无需签名）
        var view = await GetApiJsonAsync(
            bvid.StartsWith("av", StringComparison.Ordinal)
                ? $"https://api.bilibili.com/x/web-interface/view?aid={bvid[2..]}"
                : $"https://api.bilibili.com/x/web-interface/view?bvid={Uri.EscapeDataString(bvid)}",
            ct);
        var data = view.GetProperty("data");

        // 分P：p 序号越界直接报错；p>1 取对应分P的 cid 与分P名
        JsonElement[] pages = Array.Empty<JsonElement>();
        if (data.TryGetProperty("pages", out var pagesNode) && pagesNode.ValueKind == JsonValueKind.Array)
        {
            pages = pagesNode.EnumerateArray().ToArray();
        }

        if (page > pages.Length)
        {
            throw new SphException($"分P序号超出范围：该视频共 {pages.Length} 个分P（p=1~{pages.Length}）");
        }

        // 顶层 cid 是 p=1 的；分P必须取 pages[p-1].cid
        var cid = data.GetProperty("cid").GetInt64();
        var aid = data.GetProperty("aid").GetInt64();
        if (page > 1 && pages.Length > 0)
        {
            cid = pages[page - 1].GetProperty("cid").GetInt64();
        }
        var title = data.GetProperty("title").GetString() ?? "bilibili";
        if (page > 1 && pages.Length > 0)
        {
            // 分P名与主标题不同才拼接，避免「标题-标题」
            var part = pages[page - 1].TryGetProperty("part", out var partNode)
                ? partNode.GetString() : null;
            if (!string.IsNullOrWhiteSpace(part) && part != title)
            {
                title = $"{title}-{part}";
            }
        }

        var author = data.TryGetProperty("owner", out var owner) &&
                     owner.TryGetProperty("name", out var nameNode) &&
                     nameNode.ValueKind == JsonValueKind.String
            ? nameNode.GetString() ?? string.Empty
            : string.Empty;

        var bvidCanon = data.GetProperty("bvid").GetString();
        var canonical = $"https://www.bilibili.com/video/{bvidCanon}" + (page > 1 ? $"?p={page}" : string.Empty);

        // ③ WBI 签名 playurl（fnval=0 → durl 单文件 MP4）
        var mixinKey = await GetMixinKeyAsync(ct);
        var signed = WbiSign(new Dictionary<string, string>
        {
            ["avid"] = aid.ToString(),
            ["cid"] = cid.ToString(),
            ["fnval"] = "0",
            ["fnver"] = "0",
            ["fourk"] = "1",
            ["qn"] = "80"
        }, mixinKey);
        var play = await GetApiJsonAsync("https://api.bilibili.com/x/player/wbi/playurl?" + signed, ct);
        var playData = play.GetProperty("data");

        if (!playData.TryGetProperty("durl", out var durl) ||
            durl.ValueKind != JsonValueKind.Array || durl.GetArrayLength() == 0)
        {
            throw new SphException("接口没有返回可下载的视频地址（视频可能是番剧/课程/付费内容，暂只支持普通投稿视频）");
        }

        if (durl.GetArrayLength() > 1)
        {
            throw new SphException($"视频被服务器分为 {durl.GetArrayLength()} 段（超长视频），暂不支持分段下载");
        }

        var mediaUrl = durl[0].GetProperty("url").GetString();
        if (string.IsNullOrEmpty(mediaUrl))
        {
            throw new SphException("接口没有返回可下载的视频地址");
        }

        return new BiliVideo(canonical, mediaUrl!, title, author, $"{bvidCanon}-{page}");
    }

    /// <summary>取 WBI mixin key：nav → wbi_img.img_url/sub_url 文件名 → 乱序表重排取前 32。进程内缓存 6 小时。</summary>
    private async Task<string> GetMixinKeyAsync(CancellationToken ct)
    {
        if (_cachedMixinKey is not null && DateTime.UtcNow - _mixinKeyAt < TimeSpan.FromHours(6))
        {
            return _cachedMixinKey;
        }

        // nav 对未登录用户合法返回 code=-101（data 里仍带 wbi_img），不能走统一的业务码校验
        var nav = await GetApiJsonAsync("https://api.bilibili.com/x/web-interface/nav", ct, allowAnonymous: true);
        var wbi = nav.GetProperty("data").GetProperty("wbi_img");
        var imgKey = KeyFromUrl(wbi.GetProperty("img_url").GetString()!);
        var subKey = KeyFromUrl(wbi.GetProperty("sub_url").GetString()!);
        var raw = imgKey + subKey;
        if (raw.Length < 64)
        {
            throw new SphException("WBI 签名密钥异常（img/sub key 长度不足）");
        }

        var builder = new StringBuilder();
        foreach (var index in MixinKeyEncTab)
        {
            builder.Append(raw[index]);
        }

        _cachedMixinKey = builder.ToString()[..32];
        _mixinKeyAt = DateTime.UtcNow;
        return _cachedMixinKey;
    }

    /// <summary>WBI 签名：加 wts → 键排序 → 值过滤 !'()* → URL 编码 → w_rid = MD5(query + mixinKey)。</summary>
    private static string WbiSign(Dictionary<string, string> parameters, string mixinKey)
    {
        var filtered = new Dictionary<string, string>();
        foreach (var (key, value) in parameters)
        {
            filtered[key] = new string(value.Where(c => c is not ('!' or '\'' or '(' or ')' or '*')).ToArray());
        }

        filtered["wts"] = ((DateTimeOffset)DateTime.UtcNow).ToUnixTimeSeconds().ToString();
        var query = string.Join("&", filtered.OrderBy(kv => kv.Key, StringComparer.Ordinal)
            .Select(kv => $"{Uri.EscapeDataString(kv.Key)}={Uri.EscapeDataString(kv.Value)}"));
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(query + mixinKey));
        return query + "&w_rid=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    /// <summary>GET JSON 接口：5xx/429 抛 SphTransientException（可重试），业务 code != 0 抛 SphException。返回 data 的根对象。</summary>
    private async Task<JsonElement> GetApiJsonAsync(string url, CancellationToken ct, bool allowAnonymous = false)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("Referer", Referer);
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
            if (doc.RootElement.TryGetProperty("code", out var codeNode) && codeNode.ValueKind == JsonValueKind.Number)
            {
                var code = codeNode.GetInt32();
                // -101 = 账号未登录：免登录模块里 nav 接口匿名时的正常返回
                if (code != 0 && !(code == -101 && allowAnonymous))
                {
                    var message = doc.RootElement.TryGetProperty("message", out var messageNode) &&
                                   messageNode.ValueKind == JsonValueKind.String
                        ? messageNode.GetString()
                        : null;
                    throw new SphException(code == -404
                        ? "视频不存在、已删除或仅UP主自己可见"
                        : $"接口返回错误（{code}）：{message ?? "未知错误"}");
                }
            }

            return doc.RootElement.Clone();
        }
    }

    /// <summary>取 URL 文件名主干（去扩展名）作为 key 材料。</summary>
    private static string KeyFromUrl(string url)
    {
        var name = url.Split('/')[^1];
        var dot = name.IndexOf('.');
        return dot > 0 ? name[..dot] : name;
    }

    /// <summary>取 URL 查询参数 p（分P序号，从 1 起；无参数按 1）。</summary>
    private static int ParsePageParam(string url)
    {
        var idx = url.IndexOf('?', StringComparison.Ordinal);
        if (idx < 0)
        {
            return 1;
        }

        foreach (var pair in url[(idx + 1)..].Split('&'))
        {
            var eq = pair.IndexOf('=');
            if (eq > 0 && pair[..eq] == "p" && int.TryParse(pair[(eq + 1)..], out var page) && page >= 1)
            {
                return page;
            }
        }

        return 1;
    }

    [GeneratedRegex(@"(?:^|[/=])(BV[A-Za-z0-9]{10})(?![A-Za-z0-9])")]
    private static partial Regex BvidRegex();

    [GeneratedRegex(@"(?:^|/)av(\d+)(?![0-9])")]
    private static partial Regex AvIdRegex();
}

/// <summary>解析结果快照：归一化视频链接（去重键）、MP4 直链、标题（含分P名）、UP 主昵称、作品标识（BV-p）。</summary>
public record BiliVideo(string CanonicalUrl, string MediaUrl, string Title, string Author, string VideoId);
