using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeskApp.Utils;

/// <summary>
/// 番茄小说解析（自研，思路参考 ying-ck/fanqienovel-downloader 与 h13637404183-hub/fanqie-api）：
/// ① 目录页 https://fanqienovel.com/page/{bookId} 内嵌 window.__INITIAL_STATE__.page，
///    含书名/作者/封面与 chapterListWithVolume（按卷分组的全量章节，实测 1928 章一次拿全），纯 HttpClient 直连、无验证；
/// ② 正文页 https://fanqienovel.com/reader/{itemId} 内嵌 reader.chapterData.content（HTML），
///    字符为私有区 PUA，经 FanqieCharset 解码还原汉字；
/// ③ 名称搜索走 /api/author/search/search_book/v1，会触发 Bdturing 滑块验证，纯 HttpClient 返回空，
///    故搜索由 FanqieSearchWindow（WebView2，用户过滑块）捕获响应完成。
/// 链接无效 / 章节无正文等业务失败抛 SphException；网络类失败抛 SphTransientException。
/// </summary>
public partial class FanqieClient
{
    /// <summary>桌面端 UA：目录/正文/下载共用。</summary>
    public const string DesktopUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/146.0.0.0 Safari/537.36";

    public const string Referer = "https://fanqienovel.com/";

    public const string PagePrefix = "https://fanqienovel.com/page/";

    public const string ReaderPrefix = "https://fanqienovel.com/reader/";

    public const string SearchPrefix = "https://fanqienovel.com/search/";

    private readonly HttpClient _http;

    public FanqieClient(int timeoutSeconds)
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

    /// <summary>从用户输入（纯书号 ID 或目录页链接）提取 bookId；不是有效输入返回 null。</summary>
    public static string? ParseBookId(string input)
    {
        var value = input?.Trim() ?? string.Empty;
        if (value.Length == 0)
        {
            return null;
        }

        // 纯数字书号（番茄 bookId 为长整型，至少 10 位）
        if (Regex.IsMatch(value, @"^\d{10,}$"))
        {
            return value;
        }

        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            return null;
        }

        if (!url.Host.Equals("fanqienovel.com", StringComparison.OrdinalIgnoreCase) &&
            !url.Host.EndsWith(".fanqienovel.com", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var match = Regex.Match(url.AbsolutePath, @"/page/(\d{10,})");
        return match.Success ? match.Groups[1].Value : null;
    }

    /// <summary>解析书籍：bookId → 目录页 → 书名/作者/封面 + 全量章节。</summary>
    public async Task<FanqieBook> ResolveBookAsync(string input, CancellationToken ct)
    {
        var bookId = ParseBookId(input) ??
            throw new SphException("请输入番茄小说目录页链接或小说 ID（如 fanqienovel.com/page/7276384138653862966）");

        var html = await GetHtmlAsync(PagePrefix + bookId, ct, PagePrefix + bookId);
        var page = ParseInitialState(html).GetProperty("page");

        var bookName = GetString(page, "bookName", "book_name");
        if (string.IsNullOrWhiteSpace(bookName))
        {
            throw new SphException("未能读取书名，链接可能无效或小说已下架");
        }

        var author = GetString(page, "author", "authorName");
        var cover = GetString(page, "thumbUrl", "thumbUri", "sourceUri");

        var chapters = new List<FanqieChapter>();
        var order = 0;
        if (page.TryGetProperty("chapterListWithVolume", out var volumes) &&
            volumes.ValueKind == JsonValueKind.Array)
        {
            foreach (var vol in volumes.EnumerateArray())
            {
                if (vol.ValueKind == JsonValueKind.Array)
                {
                    foreach (var ch in vol.EnumerateArray())
                    {
                        AddChapter(ch, chapters, ref order);
                    }
                }
                else if (vol.ValueKind == JsonValueKind.Object)
                {
                    AddChapter(vol, chapters, ref order);
                }
            }
        }

        // 兜底：部分书没有按卷分组，用平铺 chapterList
        if (chapters.Count == 0 && page.TryGetProperty("chapterList", out var flat) &&
            flat.ValueKind == JsonValueKind.Array)
        {
            foreach (var ch in flat.EnumerateArray())
            {
                AddChapter(ch, chapters, ref order);
            }
        }

        if (chapters.Count == 0)
        {
            throw new SphException("该书暂无可下载章节（可能是新书或仅试读）");
        }

        var canonical = PagePrefix + bookId;
        return new FanqieBook(bookId, canonical, bookName, author, cover, chapters);
    }

    /// <summary>抓取单章正文：正文页 → chapterData.content（HTML）→ 纯文本 → PUA 解码（MODE0/1 自动择优）。</summary>
    public async Task<string> FetchChapterTextAsync(string itemId, string bookId, CancellationToken ct)
    {
        var url = ReaderPrefix + itemId;
        var html = await GetHtmlAsync(url, ct, PagePrefix + bookId);
        var reader = ParseInitialState(html).GetProperty("reader");

        if (!reader.TryGetProperty("chapterData", out var data) || data.ValueKind != JsonValueKind.Object)
        {
            throw new SphException("章节数据为空（可能需登录或已加锁）");
        }

        var content = GetString(data, "content");
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new SphException("该章节无正文（可能需登录、已加锁或章节被删除）");
        }

        var text = HtmlToText(content);

        // MODE0 实测正确；同时算 MODE1，选私有区残留更少者，兼容番茄日后切换映射
        var decoded0 = FanqieCharset.Decode(text, 0);
        var decoded1 = FanqieCharset.Decode(text, 1);
        var remain0 = FanqieCharset.CountUndecodedPua(decoded0);
        var remain1 = FanqieCharset.CountUndecodedPua(decoded1);
        return remain0 <= remain1 ? decoded0 : decoded1;
    }

    /// <summary>GET HTML 页面：5xx/429 视为可重试，其余非 200 视为业务错误。</summary>
    private async Task<string> GetHtmlAsync(string url, CancellationToken ct, string referer)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", DesktopUserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "text/html,application/xhtml+xml");
        request.Headers.TryAddWithoutValidation("Accept-Language", "zh-CN,zh;q=0.9");
        request.Headers.TryAddWithoutValidation("Referer", referer);

        using var response = await _http.SendAsync(request, ct);
        var status = (int)response.StatusCode;
        if (status is >= 500 or 429)
        {
            throw new SphTransientException($"页面返回 HTTP {status}");
        }

        if (status != 200)
        {
            throw new SphException($"页面返回 HTTP {status}（链接可能无效）");
        }

        var html = await response.Content.ReadAsStringAsync(ct);
        if (!html.Contains("__INITIAL_STATE__", StringComparison.Ordinal))
        {
            throw new SphException("页面结构异常（未找到内嵌数据），番茄可能改版或临时拦截");
        }

        return html;
    }

    /// <summary>从 HTML 提取 window.__INITIAL_STATE__ 的 JSON 根对象（括号配平，避免正则误判嵌套）。</summary>
    private static JsonElement ParseInitialState(string html)
    {
        const string marker = "window.__INITIAL_STATE__=";
        var idx = html.IndexOf(marker, StringComparison.Ordinal);
        if (idx < 0)
        {
            throw new SphException("页面未找到内嵌数据");
        }

        var start = idx + marker.Length;
        while (start < html.Length && char.IsWhiteSpace(html[start]))
        {
            start++;
        }

        var json = ExtractBalancedObject(html, start);
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new SphException("页面内嵌数据解析失败，番茄可能改版");
        }
    }

    /// <summary>从 start 处的 '{' 开始，按大括号配平截出完整 JSON（正确跳过字符串内的括号与转义）。</summary>
    private static string ExtractBalancedObject(string text, int start)
    {
        if (start >= text.Length || text[start] != '{')
        {
            throw new SphException("内嵌数据格式异常");
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var i = start; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (c == '\\')
                {
                    escaped = true;
                }
                else if (c == '"')
                {
                    inString = false;
                }

                continue;
            }

            if (c == '"')
            {
                inString = true;
            }
            else if (c == '{')
            {
                depth++;
            }
            else if (c == '}')
            {
                depth--;
                if (depth == 0)
                {
                    return text[start..(i + 1)];
                }
            }
        }

        throw new SphException("内嵌数据不完整");
    }

    private static void AddChapter(JsonElement ch, List<FanqieChapter> chapters, ref int order)
    {
        if (ch.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var itemId = GetString(ch, "itemId", "item_id");
        if (string.IsNullOrWhiteSpace(itemId))
        {
            return;
        }

        var orderValue = order + 1;
        if (ch.TryGetProperty("realChapterOrder", out var ord) && ord.ValueKind == JsonValueKind.String &&
            int.TryParse(ord.GetString(), out var parsed) && parsed > 0)
        {
            orderValue = parsed;
        }
        else if (ch.TryGetProperty("order", out var ord2) && ord2.ValueKind == JsonValueKind.Number &&
                 ord2.TryGetInt32(out var parsed2) && parsed2 > 0)
        {
            orderValue = parsed2;
        }

        var needPay = ch.TryGetProperty("needPay", out var pay) &&
                      (pay.ValueKind == JsonValueKind.Number && pay.GetInt32() > 0 ||
                       pay.ValueKind == JsonValueKind.True);
        var locked = ch.TryGetProperty("isChapterLock", out var lk) && lk.ValueKind == JsonValueKind.True;
        var volume = GetString(ch, "volume_name", "volumeName");

        chapters.Add(new FanqieChapter(itemId, GetString(ch, "title"), orderValue, needPay || locked, volume));
        order = orderValue;
    }

    /// <summary>正文 HTML → 纯文本：按 &lt;p&gt; 分段，&lt;br&gt; 换行，去标签，还原 HTML 实体。PUA 解码在调用方做。</summary>
    public static string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return string.Empty;
        }

        var parts = new List<string>();
        var paragraphs = Regex.Matches(html, @"<p[^>]*>(.*?)</p>", RegexOptions.Singleline | RegexOptions.IgnoreCase);
        if (paragraphs.Count > 0)
        {
            foreach (Match match in paragraphs)
            {
                parts.Add(CleanFragment(match.Groups[1].Value));
            }
        }
        else
        {
            var withBreaks = Regex.Replace(html, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
            withBreaks = Regex.Replace(withBreaks, @"</(?:div|p|h[1-6])>", "\n\n", RegexOptions.IgnoreCase);
            parts.Add(CleanFragment(withBreaks));
        }

        return string.Join("\n\n", parts.Where(p => !string.IsNullOrWhiteSpace(p)));
    }

    private static string CleanFragment(string fragment)
    {
        var text = Regex.Replace(fragment, @"<br\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, @"<[^>]+>", string.Empty);
        return WebUtility.HtmlDecode(text).Replace("\r\n", "\n").TrimEnd();
    }

    /// <summary>解析 WebView2 捕获的搜索 API 响应 JSON → 结果列表。字段容错兼容 book_name/bookName。</summary>
    public static List<FanqieSearchResult> ParseSearchResponse(string json)
    {
        var list = new List<FanqieSearchResult>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return list;
            }

            if (root.TryGetProperty("code", out var code) && code.ValueKind == JsonValueKind.Number &&
                code.GetInt32() != 0)
            {
                return list;
            }

            if (!root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object)
            {
                return list;
            }

            if (!data.TryGetProperty("search_book_data_list", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return list;
            }

            foreach (var el in arr.EnumerateArray())
            {
                var id = GetString(el, "book_id", "bookId");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                list.Add(new FanqieSearchResult(
                    id,
                    GetString(el, "book_name", "bookName"),
                    GetString(el, "author", "authorName"),
                    GetString(el, "thumb_url", "thumbUrl", "cover"),
                    GetWordCount(el)));
            }
        }
        catch
        {
            // 响应非预期结构时返回空列表，由调用方提示
        }

        return list;
    }

    private static int GetWordCount(JsonElement el)
    {
        if (!el.TryGetProperty("word_count", out var wc) && !el.TryGetProperty("wordNumber", out wc))
        {
            return 0;
        }

        return wc.ValueKind switch
        {
            JsonValueKind.Number when wc.TryGetInt32(out var n) => n,
            JsonValueKind.String when int.TryParse(wc.GetString(), out var s) => s,
            _ => 0
        };
    }

    /// <summary>按顺序取第一个存在且为字符串的字段。</summary>
    private static string GetString(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var node) && node.ValueKind == JsonValueKind.String)
            {
                return node.GetString() ?? string.Empty;
            }
        }

        return string.Empty;
    }
}

/// <summary>单章：itemId 正文键、标题、序号、是否需登录/付费、所属卷。</summary>
public record FanqieChapter(string ItemId, string Title, int Order, bool Locked, string Volume);

/// <summary>书籍快照：书号、归一化目录链接、书名、作者、封面、全量章节。</summary>
public record FanqieBook(
    string BookId, string CanonicalUrl, string BookName, string Author, string CoverUrl,
    IReadOnlyList<FanqieChapter> Chapters);

/// <summary>搜索结果项：书号、书名、作者、封面、字数。</summary>
public record FanqieSearchResult(
    string BookId, string BookName, string Author, string CoverUrl, int WordCount)
{
    /// <summary>列表展示用字数文本。</summary>
    public string WordCountText => WordCount >= 10000
        ? $"{WordCount / 10000.0:F1}万字"
        : WordCount > 0 ? $"{WordCount}字" : string.Empty;
}
