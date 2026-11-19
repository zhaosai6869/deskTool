using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;
using DeskApp.Models;

namespace DeskApp.Utils.ComicProviders;

/// <summary>
/// 各站点 Provider 内部使用的轻量 HTML 工具（标签扫描 + 简易选择器 + 图片属性提取）。
/// 选择器由各站点 Provider 自己写死，不再暴露给用户手动填写。
/// </summary>
internal static class ComicHtml
{
    private static readonly Regex TagRegex = new(
        @"<(?<close>/)?(?<tag>[a-zA-Z][\w:-]*)(?<attrs>(?:[^>""']|""[^""]*""|'[^']*')*?)(?<self>/)?>",
        RegexOptions.Compiled | RegexOptions.Singleline);

    private static readonly Regex AttrRegex = new(
        @"(?<name>[:\w-]+)\s*=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)'|(?<uq>[^\s>]+))",
        RegexOptions.Compiled);

    private static readonly Regex TitleSuffixRegex = new(
        @"[\s_|｜\-—~]+(?:在线漫画|免费漫画|漫画最新章节|最新章节|在线阅读|漫画|免費|免费).*$",
        RegexOptions.Compiled);

    private static readonly string[] ImageAttrs =
        ["data-original", "data-original-src", "data-src", "data-url", "data-echo", "data-lazy-src", "srcset", "src"];

    private static readonly HashSet<string> VoidTags = new(StringComparer.OrdinalIgnoreCase)
        { "img", "br", "hr", "input", "meta", "link", "source", "area", "base", "col", "embed", "param", "track", "wbr" };

    internal sealed class Element
    {
        public string Tag = string.Empty;
        public Dictionary<string, string> Attrs = new(StringComparer.OrdinalIgnoreCase);
        public List<Element> Ancestors = [];
        public string Text = string.Empty;

        public string Attr(string name) => Attrs.TryGetValue(name, out var v) ? v : string.Empty;
        public string Id => Attr("id");
        public string ClassName => Attr("class");
    }

    private sealed record SelPart(string? Tag, string? Id, List<string> Classes,
        string? AttrName, string? AttrOp, string? AttrValue);

    /// <summary>顺序扫描标签，保留祖先链，供选择器匹配使用。</summary>
    public static List<Element> ParseElements(string html)
    {
        var list = new List<Element>();
        var stack = new List<Element>();

        foreach (Match m in TagRegex.Matches(html))
        {
            var tag = m.Groups["tag"].Value.ToLowerInvariant();

            if (m.Groups["close"].Success)
            {
                for (var i = stack.Count - 1; i >= 0; i--)
                {
                    if (!stack[i].Tag.Equals(tag, StringComparison.OrdinalIgnoreCase)) continue;
                    stack.RemoveRange(i, stack.Count - i);
                    break;
                }
                continue;
            }

            var el = new Element
            {
                Tag = tag,
                Attrs = ParseAttrs(m.Groups["attrs"].Value),
                Ancestors = [.. stack]
            };

            if (tag == "a")
            {
                var closeIdx = html.IndexOf("</a", m.Index + m.Length, StringComparison.OrdinalIgnoreCase);
                if (closeIdx > m.Index)
                {
                    el.Text = Clean(html[(m.Index + m.Length)..closeIdx]);
                }
            }

            list.Add(el);
            if (!m.Groups["self"].Success && !VoidTags.Contains(tag)) stack.Add(el);
        }

        return list;
    }

    /// <summary>按站点自带的选择器收集章节链接；第一个命中的选择器生效。</summary>
    public static List<ComicChapter> Chapters(string html, string pageUrl, string[] selectors,
        Func<string, bool>? hrefFilter = null)
    {
        var baseUri = TryUri(pageUrl);
        var result = new List<ComicChapter>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var anchors = ParseElements(html).Where(e => e.Tag == "a").ToList();

        foreach (var selector in selectors)
        {
            var parts = ParseSelector(selector);
            if (parts.Count == 0) continue;

            var hits = anchors.Where(a => a.Attr("href").Length > 0 && Matches(a, parts)).ToList();
            if (hits.Count == 0) continue;

            foreach (var a in hits)
            {
                var href = a.Attr("href");
                if (hrefFilter is not null && !hrefFilter(href)) continue;
                var abs = Resolve(href, baseUri);
                if (abs is null || !seen.Add(abs)) continue;

                var title = a.Text;
                if (title.Length == 0) title = LastSegment(abs);
                if (title.Length > 80) title = title[..80];
                result.Add(new ComicChapter { Title = title, Url = abs });
            }

            if (result.Count > 0) return result;
        }

        return result;
    }

    /// <summary>按站点自带的选择器收集图片地址；第一个命中的选择器生效。</summary>
    public static List<string> Images(string html, string pageUrl, string[] selectors)
    {
        var baseUri = TryUri(pageUrl);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var elements = ParseElements(html).Where(e => e.Tag == "img").ToList();

        foreach (var selector in selectors)
        {
            var parts = ParseSelector(selector);
            if (parts.Count == 0) continue;

            foreach (var img in elements.Where(i => Matches(i, parts)))
            {
                foreach (var attr in ImageAttrs)
                {
                    var raw = img.Attr(attr);
                    if (raw.Length == 0) continue;

                    var candidate = attr.Equals("srcset", StringComparison.OrdinalIgnoreCase)
                        ? raw.Split(',')[^1].Trim().Split(' ')[0]
                        : raw;
                    candidate = candidate.Trim();
                    if (candidate.Length == 0) continue;
                    if (IsDecorative(candidate)) continue;

                    var abs = Resolve(candidate, baseUri);
                    if (abs is null || !seen.Add(abs)) continue;
                    result.Add(abs);
                    break;
                }
            }

            if (result.Count > 0) return result;
        }

        return result;
    }

    public static string? Title(string html)
    {
        var m = Regex.Match(html, @"<title[^>]*>(?<t>.*?)</title>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!m.Success) return null;

        var text = TitleSuffixRegex.Replace(Clean(m.Groups["t"].Value), string.Empty).Trim();
        return text.Length is > 0 and <= 80 ? text : null;
    }

    public static string Clean(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;
        var stripped = Regex.Replace(text, @"<[^>]+>", " ");
        var decoded = WebUtility.HtmlDecode(stripped);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    public static string? Resolve(string href, Uri? baseUri)
    {
        href = href.Trim();
        if (href.Length == 0) return null;
        if (href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)) return null;
        if (href.StartsWith("#", StringComparison.Ordinal)) return null;
        if (href.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;

        if (Uri.TryCreate(href, UriKind.Absolute, out var abs) &&
            (abs.Scheme == Uri.UriSchemeHttp || abs.Scheme == Uri.UriSchemeHttps))
        {
            return abs.AbsoluteUri;
        }

        if (baseUri is not null && Uri.TryCreate(baseUri, href, out var rel)) return rel.AbsoluteUri;
        return null;
    }

    private static Dictionary<string, string> ParseAttrs(string raw)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match a in AttrRegex.Matches(raw))
        {
            var name = a.Groups["name"].Value;
            var value = a.Groups["dq"].Success ? a.Groups["dq"].Value
                : a.Groups["sq"].Success ? a.Groups["sq"].Value
                : a.Groups["uq"].Value;
            map[name] = WebUtility.HtmlDecode(value);
        }
        return map;
    }

    private static List<SelPart> ParseSelector(string selector)
    {
        var parts = new List<SelPart>();
        foreach (var rawToken in selector.Split([' ', '>'], StringSplitOptions.RemoveEmptyEntries))
        {
            var text = rawToken.Trim();
            string? attrName = null, attrOp = null, attrValue = null;

            var bracket = Regex.Match(text,
                @"^(?<base>[^\[]*)\[(?<name>[\w:-]+)(?<op>[*^$]?=)(?<val>[^\]]*)\]$");
            if (bracket.Success)
            {
                text = bracket.Groups["base"].Value;
                attrName = bracket.Groups["name"].Value;
                attrOp = bracket.Groups["op"].Value;
                attrValue = bracket.Groups["val"].Value.Trim('\'', '"');
            }

            string? tag = null, id = null;
            var classes = new List<string>();

            var head = Regex.Match(text, @"^(?<tag>[a-zA-Z][\w-]*)");
            if (head.Success) tag = head.Groups["tag"].Value.ToLowerInvariant();

            var idMatch = Regex.Match(text, @"#(?<id>[\w-]+)");
            if (idMatch.Success) id = idMatch.Groups["id"].Value;

            foreach (Match c in Regex.Matches(text, @"\.(?<cls>[\w-]+)"))
            {
                classes.Add(c.Groups["cls"].Value);
            }

            if (tag is null && id is null && classes.Count == 0 && attrName is null) continue;
            parts.Add(new SelPart(tag, id, classes, attrName, attrOp, attrValue));
        }
        return parts;
    }

    private static bool Matches(Element el, List<SelPart> parts)
    {
        if (parts.Count == 0) return false;
        if (!MatchPart(el, parts[^1])) return false;

        var idx = parts.Count - 2;
        for (var i = el.Ancestors.Count - 1; i >= 0 && idx >= 0; i--)
        {
            if (MatchPart(el.Ancestors[i], parts[idx])) idx--;
        }
        return idx < 0;
    }

    private static bool MatchPart(Element el, SelPart part)
    {
        if (part.Tag is not null && !el.Tag.Equals(part.Tag, StringComparison.OrdinalIgnoreCase)) return false;
        if (part.Id is not null && !el.Id.Equals(part.Id, StringComparison.OrdinalIgnoreCase)) return false;

        if (part.Classes.Count > 0)
        {
            var have = el.ClassName.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (!part.Classes.All(c => have.Any(h => h.Equals(c, StringComparison.OrdinalIgnoreCase)))) return false;
        }

        if (part.AttrName is null) return true;
        if (!el.Attrs.TryGetValue(part.AttrName, out var value)) return false;

        var target = part.AttrValue ?? string.Empty;
        return part.AttrOp switch
        {
            "*=" => value.Contains(target, StringComparison.OrdinalIgnoreCase),
            "^=" => value.StartsWith(target, StringComparison.OrdinalIgnoreCase),
            "$=" => value.EndsWith(target, StringComparison.OrdinalIgnoreCase),
            _ => value.Equals(target, StringComparison.OrdinalIgnoreCase)
        };
    }

    private static bool IsDecorative(string url) =>
        Regex.IsMatch(url, @"(logo|icon|avatar|favicon|blank|placeholder|loading|spacer|advert|qrcode)",
            RegexOptions.IgnoreCase);

    /// <summary>从脚本/JSON 片段里按正则抓图（站点专用兜底）。正则需含命名分组 u。</summary>
    public static List<string> ImagesFromPattern(string html, string pageUrl, string pattern)
    {
        var baseUri = TryUri(pageUrl);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (Match m in Regex.Matches(html, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline))
        {
            var raw = m.Groups["u"].Success ? m.Groups["u"].Value : m.Groups[1].Value;
            if (string.IsNullOrWhiteSpace(raw)) continue;
            raw = raw.Replace("\\/", "/").Replace("\\u0026", "&").Trim();
            if (IsDecorative(raw)) continue;
            var abs = Resolve(raw, baseUri);
            if (abs is null || !seen.Add(abs)) continue;
            result.Add(abs);
        }

        return result;
    }

    private static Uri? TryUri(string? s) =>
        !string.IsNullOrWhiteSpace(s) && Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : null;

    private static string LastSegment(string url)
    {
        try
        {
            var path = new Uri(url).AbsolutePath.TrimEnd('/');
            var idx = path.LastIndexOf('/');
            return idx >= 0 ? path[(idx + 1)..] : path;
        }
        catch
        {
            return "章节";
        }
    }
}
