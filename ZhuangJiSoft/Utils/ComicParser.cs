using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using DeskApp.Models;

namespace DeskApp.Utils;

/// <summary>
/// 通用 HTML 解析：从漫画目录页识别章节链接，从章节页识别图片地址。
/// 不绑定任何站点，采用多套启发式规则；识别不准时可用用户提供的 CSS 选择器兜底。
/// 纯正则/字符串实现，零第三方依赖。
/// </summary>
public static class ComicParser
{
    private static readonly Regex TagRegex = new(@"<(?<tag>[a-zA-Z][\w-]*)\b(?<attrs>[^>]*?)(?:/)?>",
        RegexOptions.Compiled | RegexOptions.Singleline);
    // 带内容的 <a ...>文本</a> 片段，用于提取链接文字
    private static readonly Regex AnchorWithTextRegex = new(
        @"<a\b(?<attrs>[^>]*)>(?<text>.*?)</a\s*>",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Singleline);
    private static readonly Regex AttrRegex = new(@"(?<name>[\w-]+)\s*=\s*(?:""(?<dq>[^""]*)""|'(?<sq>[^']*)'|(?<uq>[^\s>]+))",
        RegexOptions.Compiled);
    private static readonly Regex EntityRegex = new(@"&(?:amp|lt|gt|quot|#39|nbsp);|&#\d+;|&#x[0-9a-fA-F]+;",
        RegexOptions.Compiled);

    // 章节标题常见关键词：第X话/第X章/第X卷/Chapter/Episode/话/集，以及纯数字
    private static readonly Regex ChapterTitleHint = new(
        @"(第\s*\d+|[章节话集卷]\s*\d|\d+\s*[章节话集卷]|^Chapter\b|^Episode\b|^Vol\b|更新|话\s*$)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // 图片地址常见属性：懒加载会用 data-src / data-original / data-url / srcset
    private static readonly string[] ImageAttrs =
        ["data-original", "data-src", "data-url", "data-echo", "data-original-src", "src", "srcset"];

    /// <summary>解析结果：章节列表 + 用于诊断的提示文本。</summary>
    public sealed record ParseResult(List<ComicChapter> Chapters, string Hint);

    /// <summary>从目录页 HTML 提取章节链接。</summary>
    public static ParseResult ParseChapters(string html, string pageUrl, string? manualSelector = null)
    {
        var baseUri = TryUri(pageUrl);
        var candidates = new List<ComicChapter>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 优先用手动选择器：形如 "a.chapter-item" / "#chapter-list a" / ".list a"
        if (!string.IsNullOrWhiteSpace(manualSelector))
        {
            foreach (var el in SelectBySimpleCss(html, manualSelector))
            {
                var href = GetAttr(el, "href");
                if (string.IsNullOrWhiteSpace(href)) continue;
                var abs = Resolve(href, baseUri);
                if (abs is null || !seen.Add(abs)) continue;
                candidates.Add(new ComicChapter
                {
                    Title = CleanText(StripTags(el)),
                    Url = abs
                });
            }
            if (candidates.Count > 0)
            {
                return new ParseResult(candidates, $"手动选择器命中 {candidates.Count} 个章节");
            }
        }

        // 提取 <a...>文本</a> 完整片段（含链接文字）
        var anchors = new List<(string Href, string Text)>();
        foreach (Match m in AnchorWithTextRegex.Matches(html))
        {
            var attrs = m.Groups["attrs"].Value;
            var hrefMatch = Regex.Match(attrs, @"href\s*=\s*(?:""(?<d>[^""]*)""|'(?<s>[^']*)'|(?<u>[^\s>]+))",
                RegexOptions.IgnoreCase);
            var href = hrefMatch.Success
                ? (hrefMatch.Groups["d"].Success ? hrefMatch.Groups["d"].Value
                   : hrefMatch.Groups["s"].Success ? hrefMatch.Groups["s"].Value
                   : hrefMatch.Groups["u"].Value)
                : string.Empty;
            if (href.Length == 0) continue;
            anchors.Add((DecodeEntities(href), CleanText(m.Groups["text"].Value)));
        }

        // 自动策略：带章节标题关键词的链接
        foreach (var (href, text) in anchors)
        {
            if (text.Length == 0 || text.Length > 80) continue;
            if (!ChapterTitleHint.IsMatch(text)) continue;
            var abs = Resolve(href, baseUri);
            if (abs is null || !seen.Add(abs)) continue;
            candidates.Add(new ComicChapter { Title = text, Url = abs });
        }
        if (candidates.Count > 0)
        {
            return new ParseResult(candidates, $"自动识别 {candidates.Count} 个章节（按标题关键词+链接）");
        }

        // 兜底策略：href 含详情页特征（数字ID / chapter / view 等）
        foreach (var (href, text) in anchors)
        {
            if (!Regex.IsMatch(href, @"(chapter|view|manga|comic|book|/\d+|_\d+)", RegexOptions.IgnoreCase)) continue;
            if (text.Length == 0 || text.Length > 80) continue;
            var abs = Resolve(href, baseUri);
            if (abs is null || !seen.Add(abs)) continue;
            candidates.Add(new ComicChapter { Title = text, Url = abs });
        }

        return candidates.Count > 0
            ? new ParseResult(candidates, $"宽松模式识别 {candidates.Count} 个章节，建议核对")
            : new ParseResult([], "未识别到章节，请在「手动校正」里填章节链接的 CSS 选择器（如 a.chapter）");
    }

    /// <summary>从章节页 HTML 提取图片地址。</summary>
    public static List<string> ParseImages(string html, string pageUrl, string? manualSelector = null)
    {
        var baseUri = TryUri(pageUrl);
        var result = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? url)
        {
            if (string.IsNullOrWhiteSpace(url)) return;
            // 过滤掉常见的图标/广告/占位图
            if (Regex.IsMatch(url, @"(logo|icon|avatar|ad[/_.-]|blank|placeholder|loading|spacer)", RegexOptions.IgnoreCase)) return;
            var abs = Resolve(url, baseUri);
            if (abs is null || !seen.Add(abs)) return;
            result.Add(abs);
        }

        // 手动选择器：提取命中元素的图片属性
        if (!string.IsNullOrWhiteSpace(manualSelector))
        {
            foreach (var el in SelectBySimpleCss(html, manualSelector))
            {
                Add(GetImageUrl(el));
            }
            if (result.Count > 0) return result;
        }

        // 自动策略：遍历 img 标签，按懒加载属性优先级取图
        foreach (var el in ExtractTags(html, "img"))
        {
            Add(GetImageUrl(el));
        }

        // 补充策略：从 JSON / CSS background 里找图片（部分站点用脚本渲染图片地址）
        if (result.Count == 0)
        {
            foreach (Match m in Regex.Matches(html,
                         @"(?:url\(|[""'])((?:https?:)?//[^\s""')]+?\.(?:jpg|jpeg|png|webp|gif)(?:\?[^\s""')]*)?)[""')]",
                         RegexOptions.Compiled | RegexOptions.IgnoreCase))
            {
                Add(DecodeEntities(m.Groups[1].Value));
            }
        }

        return result;
    }

    /// <summary>从单个标签的字符串里按优先级取图片 URL。</summary>
    private static string? GetImageUrl(string tag)
    {
        foreach (var attr in ImageAttrs)
        {
            var val = GetAttr(tag, attr);
            if (string.IsNullOrWhiteSpace(val)) continue;
            if (attr.Equals("srcset", StringComparison.OrdinalIgnoreCase))
            {
                // srcset 取最后一项（通常最大尺寸）的 URL
                var last = val.Split(',')[^1].Trim().Split(' ')[0];
                return DecodeEntities(last);
            }
            return DecodeEntities(val);
        }
        return null;
    }

    /// <summary>简易 CSS 选择器支持：tag.class / #id / .class / tag，可带一层后代（如 "#list a"）。</summary>
    private static List<string> SelectBySimpleCss(string html, string selector)
    {
        var parts = selector.Trim().Split([' ', '>'], StringSplitOptions.RemoveEmptyEntries);
        var final = parts.Length > 0 ? parts[^1] : selector;

        // 解析最后一段为 tag + class + id
        string? tag = null, cls = null, id = null;
        var m = Regex.Match(final, @"^([a-zA-Z][\w-]*)?(?:\.([\w-]+))?(?:#([\w-]+))?$");
        if (m.Success)
        {
            tag = m.Groups[1].Value;
            cls = m.Groups[2].Value;
            id = m.Groups[3].Value;
        }
        else
        {
            id = final.StartsWith('#') ? final[1..] : null;
            cls = final.StartsWith('.') ? final[1..] : null;
        }

        var all = ExtractTags(html, tag ?? "[a-zA-Z][\\w-]*");
        var matched = all.Where(el =>
        {
            var attrs = GetAttrs(el);
            if (!string.IsNullOrEmpty(id) &&
                !id.Equals(attrs.TryGetValue("id", out var idVal) ? idVal : string.Empty,
                    StringComparison.OrdinalIgnoreCase)) return false;
            if (!string.IsNullOrEmpty(cls))
            {
                var elCls = attrs.TryGetValue("class", out var clsVal) ? clsVal : string.Empty;
                if (string.IsNullOrEmpty(elCls) || !elCls.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Any(c => c.Equals(cls, StringComparison.OrdinalIgnoreCase))) return false;
            }
            return true;
        }).ToList();

        return matched;
    }

    private static List<string> ExtractTags(string html, string tag)
    {
        var result = new List<string>();
        foreach (Match m in TagRegex.Matches(html))
        {
            if (m.Groups["tag"].Value.Equals(tag, StringComparison.OrdinalIgnoreCase) ||
                (tag.Contains('\\') || tag.Contains('[')) &&
                Regex.IsMatch(m.Groups["tag"].Value, "^" + tag + "$", RegexOptions.IgnoreCase))
            {
                result.Add(m.Value);
            }
        }
        return result;
    }

    private static Dictionary<string, string> GetAttrs(string tag)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var m = Regex.Match(tag, @"<[a-zA-Z][\w-]*\b(?<attrs>[^>]*?)>");
        if (!m.Success) return map;
        foreach (Match a in AttrRegex.Matches(m.Groups["attrs"].Value))
        {
            var name = a.Groups["name"].Value;
            var val = a.Groups["dq"].Success ? a.Groups["dq"].Value
                    : a.Groups["sq"].Success ? a.Groups["sq"].Value
                    : a.Groups["uq"].Value;
            map[name] = DecodeEntities(val);
        }
        return map;
    }

    private static string GetAttr(string tag, string name)
    {
        var attrs = GetAttrs(tag);
        return attrs.TryGetValue(name, out var v) ? v : string.Empty;
    }

    private static string StripTags(string html) => Regex.Replace(html, @"<[^>]+>", " ");

    private static string CleanText(string text)
    {
        var decoded = DecodeEntities(text);
        return Regex.Replace(decoded, @"\s+", " ").Trim();
    }

    private static string DecodeEntities(string s) => EntityRegex.Replace(s, m =>
    {
        var v = m.Value;
        return v switch
        {
            "&amp;" => "&", "&lt;" => "<", "&gt;" => ">", "&quot;" => "\"",
            "&apos;" or "&#39;" => "'", "&nbsp;" => " ",
            _ when v.StartsWith("&#x", StringComparison.OrdinalIgnoreCase) =>
                char.TryParse(((char)Convert.ToInt32(v[3..^1], 16)).ToString(), out var c1) ? c1.ToString() : v,
            _ when v.StartsWith("&#") => int.TryParse(v[2..^1], out var n) && n <= 0x10FFFF
                ? char.ConvertFromUtf32(n) : v,
            _ => v
        };
    });

    private static Uri? TryUri(string s) => Uri.TryCreate(s, UriKind.Absolute, out var u) ? u : null;

    private static string? Resolve(string href, Uri? baseUri)
    {
        href = href.Trim();
        if (href.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) ||
            href.StartsWith("#")) return null;
        if (Uri.TryCreate(href, UriKind.Absolute, out var abs)) return abs.AbsoluteUri;
        if (baseUri is not null && Uri.TryCreate(baseUri, href, out var rel)) return rel.AbsoluteUri;
        return null;
    }
}
