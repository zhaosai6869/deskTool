using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DeskApp.Models;
using Jint;

namespace DeskApp.Utils.ComicProviders;

/// <summary>
/// 快看漫画：目录不渲染成 HTML 链接，而是压缩在服务端页面的 window.__NUXT__ 里；
/// 图片走官方 api.kkmh.com/v2/comic/{id} 接口。方案参考 GitHub 上 spiderComics_kuaikan 与
/// ComicBook 的 kuaikan 模块（执行 __NUXT__ → data[0].comics / comicInfo.comicImages）。
/// 这里用 Jint 在 .NET 内执行这段 JS，避免引入浏览器或硬编码前端变量名。
/// </summary>
public sealed class KuaikanComicProvider : ComicProviderBase
{
    private const string SiteBase = "https://www.kuaikanmanhua.com";
    private const string ImageApi = "https://api.kkmh.com/v2/comic";

    private static readonly Regex NuxtScriptRegex = new(
        @"<script[^>]*>([\s\S]*?)</script>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex TopicIdRegex = new(
        @"/(?:web/topic|mobile|topic)/(\d+)", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private static readonly Regex ChapterIdRegex = new(
        @"/(?:web/comic|webs/comic-next|mobile/comics)/(\d+)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public override string Key => "kuaikan";
    public override string DisplayName => "快看漫画";
    public override string HomeUrl => SiteBase;
    public override string[] Hosts => ["kuaikanmanhua.com", "kkmh.com"];

    public override string Usage =>
        "粘贴作品主题页（/web/topic/数字）；也支持手机列表页 /mobile/数字/list 或章节页。" +
        "目录从页面内 __NUXT__ 数据解析，图片走官方接口；付费/未解锁章节自动取消勾选。";

    protected override string[] ChapterSelectors =>
    [
        "a[href*='/web/comic/']",
        ".kukai-info a",
        ".chapter-list a"
    ];

    protected override string[] ImageSelectors =>
    [
        ".kukai-comic img",
        ".comic-img img",
        "img[data-src]"
    ];

    /// <summary>图片从接口取，不需要抓章节页 HTML。</summary>
    protected override bool NeedsChapterHtml => false;

    public override async Task<ComicParseResult> ParseChaptersAsync(string url, ComicConfig config,
        CancellationToken ct)
    {
        var topicId = MatchId(TopicIdRegex, url);
        if (string.IsNullOrEmpty(topicId))
        {
            // 用户粘了章节页：先用图片接口反查所属作品 id
            var chapterId = MatchId(ChapterIdRegex, url);
            if (!string.IsNullOrEmpty(chapterId)) topicId = await ResolveTopicIdAsync(chapterId, config, ct);
        }
        if (string.IsNullOrEmpty(topicId))
        {
            throw new InvalidOperationException("快看漫画需要作品主题页网址，形如 " +
                "https://www.kuaikanmanhua.com/web/topic/16222/");
        }

        var html = await ComicClient.GetHtmlAsync($"{SiteBase}/web/topic/{topicId}/", config, ct);
        using var doc = ParseNuxtData(html);
        var data = doc.RootElement;

        var title = GetString(data, "topicInfo", "title") ?? DisplayName;

        JsonElement list = default;
        if (data.TryGetProperty("comics", out var comics) && comics.ValueKind == JsonValueKind.Array)
        {
            list = comics; // PC 主题页
        }
        else if (data.TryGetProperty("comicList", out var comicList) &&
                 comicList.ValueKind == JsonValueKind.Array)
        {
            list = comicList; // 手机列表页字段
        }

        var chapters = new List<ComicChapter>();
        var lockedCount = 0;
        if (list.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in list.EnumerateArray())
            {
                var id = GetId(item);
                if (string.IsNullOrEmpty(id)) continue;

                var name = GetString(item, "title") ?? $"第 {chapters.Count + 1} 话";
                var locked = IsLocked(item);
                if (locked) lockedCount++;

                chapters.Add(new ComicChapter
                {
                    Title = locked ? "🔒 " + name : name,
                    Url = $"{SiteBase}/webs/comic-next/{id}",
                    IsSelected = !locked
                });
            }
        }

        var hint = chapters.Count == 0
            ? $"快看漫画：未识别到章节，页面结构可能已变更。{Usage}"
            : $"快看漫画：识别到 {chapters.Count} 章" +
              (lockedCount > 0 ? $"（{lockedCount} 个付费/未解锁章节已取消勾选）" : string.Empty);

        return new ComicParseResult(chapters, title, hint);
    }

    public override async Task<List<string>> ParseImagesAsync(string chapterUrl, string html,
        ComicConfig config, CancellationToken ct)
    {
        var chapterId = MatchId(ChapterIdRegex, chapterUrl);
        if (string.IsNullOrEmpty(chapterId)) return [];

        var json = await ComicClient.GetStringAsync(
            $"{ImageApi}/{chapterId}?is_preview=1", RefererFor(chapterUrl), config, ct,
            ("Accept", "application/json"));

        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("data", out var data) ||
            data.ValueKind != JsonValueKind.Object ||
            !data.TryGetProperty("images", out var images) ||
            images.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<string>();
        foreach (var image in images.EnumerateArray())
        {
            var url = image.ValueKind == JsonValueKind.String
                ? image.GetString()
                : GetString(image, "url");
            if (!string.IsNullOrWhiteSpace(url) && !result.Contains(url)) result.Add(url);
        }
        return result;
    }

    public override string RefererFor(string chapterUrl) => SiteBase + "/";

    /// <summary>执行页面里的 window.__NUXT__，返回 data[0] 的 JSON。</summary>
    private static JsonDocument ParseNuxtData(string html)
    {
        var script = NuxtScriptRegex.Matches(html)
            .Select(m => m.Groups[1].Value)
            .FirstOrDefault(s => s.Contains("window.__NUXT__=", StringComparison.Ordinal));
        if (string.IsNullOrEmpty(script))
        {
            throw new InvalidOperationException("快看页面未找到 __NUXT__ 数据，站点可能已改版");
        }

        var engine = new Engine();
        engine.Execute("var window = {};");
        engine.Execute(script);
        var json = engine.Evaluate("JSON.stringify(window.__NUXT__.data[0])").AsString();
        return JsonDocument.Parse(json);
    }

    private static async Task<string?> ResolveTopicIdAsync(string chapterId, ComicConfig config,
        CancellationToken ct)
    {
        var json = await ComicClient.GetStringAsync(
            $"{ImageApi}/{chapterId}?is_preview=1", SiteBase + "/", config, ct,
            ("Accept", "application/json"));
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.TryGetProperty("data", out var data) &&
               data.TryGetProperty("topic", out var topic)
            ? GetId(topic)
            : null;
    }

    private static string? MatchId(Regex regex, string url)
    {
        var m = regex.Match(url);
        return m.Success ? m.Groups[1].Value : null;
    }

    private static string? GetId(JsonElement el)
    {
        if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty("id", out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null
        };
    }

    private static string? GetString(JsonElement el, params string[] path)
    {
        foreach (var name in path)
        {
            if (el.ValueKind != JsonValueKind.Object || !el.TryGetProperty(name, out el)) return null;
        }
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }

    private static bool IsLocked(JsonElement item)
    {
        foreach (var key in new[] { "is_free", "can_view" })
        {
            if (item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.False) return true;
        }
        return item.TryGetProperty("vip_exclusive", out var vip) && vip.ValueKind == JsonValueKind.True;
    }
}
