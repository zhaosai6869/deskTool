using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DeskApp.Models;

namespace DeskApp.Utils.ComicProviders;

/// <summary>
/// 拷贝漫画：页面为前端渲染，章节与图片通过官方 api.mangacopy.com 接口获取。
/// 只取免费章节（付费章节返回空图片列表）。
/// </summary>
public sealed class CopymangaComicProvider : ComicProviderBase
{
    private const string ApiBase = "https://api.mangacopy.com/api/v3";

    public override string Key => "copymanga";
    public override string DisplayName => "拷贝漫画";
    public override string HomeUrl => "https://copymanga.com";
    public override string[] Hosts => ["copymanga.com", "mangacopy.com", "copymanga.site", "copymanga.tv"];

    public override string Usage =>
        "粘贴作品详情页（copymanga.com/comic/路径名）。接口只返回免费章节，付费章节会自动跳过。";

    protected override string[] ChapterSelectors =>
    [
        ".list-group a",
        ".chapter-list a",
        "a[href*='/comic/']"
    ];

    protected override string[] ImageSelectors =>
    [
        ".comic-contain img",
        "#content img",
        "img[data-src]"
    ];

    /// <summary>拷贝漫画走 api.mangacopy.com 接口，不需要抓章节页 HTML。</summary>
    protected override bool NeedsChapterHtml => false;

    public override async Task<ComicParseResult> ParseChaptersAsync(string url, ComicConfig config,
        CancellationToken ct)
    {
        var path = ExtractComicPath(url);
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException("拷贝漫画需要作品详情页网址，形如 copymanga.com/comic/xxxx");
        }

        var json = await ComicClient.GetStringAsync(
            $"{ApiBase}/comic2/group?id={Uri.EscapeDataString(path)}&limit=500&offset=0&platform=1",
            "https://copymanga.com/", config, ct,
            ("Accept", "application/json"),
            ("Source", "copyApp"),
            ("Version", "1.0.0"),
            ("Region", "1"));

        using var doc = JsonDocument.Parse(json);
        var code = doc.RootElement.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number
            ? codeEl.GetInt32()
            : -1;
        if (code != 200)
        {
            throw new InvalidOperationException($"拷贝漫画接口返回 code={code}，可能被风控或接口变更");
        }

        var chapters = new List<ComicChapter>();
        var title = path;

        if (doc.RootElement.TryGetProperty("results", out var results))
        {
            if (results.TryGetProperty("comic", out var comic) &&
                comic.TryGetProperty("name", out var nameEl))
            {
                title = nameEl.GetString() ?? path;
            }

            if (results.TryGetProperty("groups", out var groups) && groups.ValueKind == JsonValueKind.Object)
            {
                foreach (var group in groups.EnumerateObject())
                {
                    if (!group.Value.TryGetProperty("chapters", out var arr) ||
                        arr.ValueKind != JsonValueKind.Array) continue;

                    foreach (var ch in arr.EnumerateArray())
                    {
                        if (!ch.TryGetProperty("path_word", out var pw)) continue;
                        var chPath = pw.GetString();
                        if (string.IsNullOrWhiteSpace(chPath)) continue;

                        var name = ch.TryGetProperty("name", out var nEl) ? nEl.GetString() ?? chPath : chPath;
                        var isFree = !ch.TryGetProperty("is_free", out var fEl) ||
                                     fEl.ValueKind != JsonValueKind.False;

                        chapters.Add(new ComicChapter
                        {
                            Title = (isFree ? "" : "🔒 ") + name,
                            Url = $"https://copymanga.com/comic/{ExtractComicPath(url)}/chapter/{chPath}",
                            IsSelected = isFree
                        });
                    }
                }
            }
        }

        var hint = chapters.Count == 0
            ? $"拷贝漫画：接口未返回章节。{Usage}"
            : $"拷贝漫画：{chapters.Count} 章（付费章节已取消勾选）";

        return new ComicParseResult(chapters, title, hint);
    }

    public override async Task<List<string>> ParseImagesAsync(string chapterUrl, string html,
        ComicConfig config, CancellationToken ct)
    {
        var m = Regex.Match(chapterUrl, @"/comic/(?<comic>[^/]+)/chapter/(?<ch>[^/?#]+)",
            RegexOptions.IgnoreCase);
        if (!m.Success) return [];

        var comicPath = m.Groups["comic"].Value;
        var chapterPath = m.Groups["ch"].Value;
        var json = await ComicClient.GetStringAsync(
            $"{ApiBase}/comic/{Uri.EscapeDataString(comicPath)}/chapter2?chapter_uuid={Uri.EscapeDataString(chapterPath)}&platform=1",
            "https://copymanga.com/", config, ct,
            ("Accept", "application/json"),
            ("Source", "copyApp"),
            ("Version", "1.0.0"),
            ("Region", "1"));

        using var doc = JsonDocument.Parse(json);
        var images = new List<string>();
        if (!doc.RootElement.TryGetProperty("results", out var results)) return images;
        if (!results.TryGetProperty("chapter", out var chapter)) return images;
        if (!chapter.TryGetProperty("contents", out var contents) ||
            contents.ValueKind != JsonValueKind.Array) return images;

        foreach (var item in contents.EnumerateArray())
        {
            var url = item.TryGetProperty("url", out var uEl) ? uEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(url)) continue;
            images.Add(url);
        }

        return images;
    }

    public override string RefererFor(string chapterUrl) => "https://copymanga.com/";

    private static string? ExtractComicPath(string url)
    {
        var m = Regex.Match(url, @"/comic/(?<p>[^/?#]+)", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["p"].Value : null;
    }
}
