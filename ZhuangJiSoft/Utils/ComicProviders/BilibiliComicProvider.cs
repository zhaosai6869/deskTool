using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DeskApp.Models;

namespace DeskApp.Utils.ComicProviders;

/// <summary>哔哩哔哩漫画：走官网 twirp 接口（ComicDetail / ImageIndex），只取免费章节。</summary>
public sealed class BilibiliComicProvider : ComicProviderBase
{
    private const string ApiBase = "https://manga.bilibili.com/twirp/comic.v1.Comic/";

    public override string Key => "bilibili";
    public override string DisplayName => "哔哩哔哩漫画";
    public override string HomeUrl => "https://manga.bilibili.com";
    public override string[] Hosts => ["manga.bilibili.com"];

    public override string Usage =>
        "粘贴作品详情页（形如 /detail/mc26444）。只下载免费章节，付费/已下架章节自动跳过。";

    protected override string[] ChapterSelectors => [];
    protected override string[] ImageSelectors => [];

    /// <summary>B 站走 twirp 接口，不需要抓章节页 HTML。</summary>
    protected override bool NeedsChapterHtml => false;

    public override async Task<ComicParseResult> ParseChaptersAsync(string url, ComicConfig config,
        CancellationToken ct)
    {
        var match = Regex.Match(url, @"mc(?<id>\d+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            throw new InvalidOperationException("B 站漫画需要作品详情页网址，形如 /detail/mc26444");
        }

        var comicId = match.Groups["id"].Value;
        var json = await ComicClient.PostFormAsync(
            ApiBase + "ComicDetail?device=pc&platform=web",
            url,
            "comic_id=" + comicId,
            config,
            ct,
            ("Origin", "https://manga.bilibili.com"),
            ("X-Requested-With", "XMLHttpRequest"));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number
            ? codeEl.GetInt32()
            : -1;
        if (code != 0)
        {
            var msg = root.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() : null;
            throw new InvalidOperationException(
                $"B 站接口返回 code={code}{(string.IsNullOrWhiteSpace(msg) ? "" : "，" + msg)}，可能触发风控或需要登录");
        }

        var data = root.GetProperty("data");
        var title = data.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "B站漫画" : "B站漫画";

        var chapters = new List<ComicChapter>();
        if (data.TryGetProperty("ep_list", out var eps) && eps.ValueKind == JsonValueKind.Array)
        {
            foreach (var ep in eps.EnumerateArray())
            {
                if (!ep.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var epId)) continue;

                var locked = ep.TryGetProperty("is_locked", out var lockEl) && lockEl.ValueKind == JsonValueKind.True;
                var ordinal = ep.TryGetProperty("ordinal", out var ordEl) && ordEl.ValueKind == JsonValueKind.Number
                    ? ordEl.GetDouble()
                    : 0;
                var epTitle = ep.TryGetProperty("title", out var epTitleEl) ? epTitleEl.GetString() ?? "" : "";

                var name = ordinal > 0 ? $"第{ordinal:0.##}话 {epTitle}".Trim() : epTitle.Trim();
                chapters.Add(new ComicChapter
                {
                    Title = (locked ? "🔒 " : "") + (name.Length == 0 ? $"ep{epId}" : name),
                    Url = $"https://manga.bilibili.com/mc{comicId}/{epId}",
                    IsSelected = !locked
                });
            }
        }

        var hint = chapters.Count == 0
            ? $"B 站漫画：接口未返回章节。{Usage}"
            : $"B 站漫画：{chapters.Count} 章（付费章节已取消勾选）";

        return new ComicParseResult(chapters, title, hint);
    }

    public override async Task<List<string>> ParseImagesAsync(string chapterUrl, string html,
        ComicConfig config, CancellationToken ct)
    {
        var match = Regex.Match(chapterUrl, @"(?<ep>\d+)\s*$");
        if (!match.Success) return [];

        var json = await ComicClient.PostFormAsync(
            ApiBase + "ImageIndex?device=pc&platform=web",
            "https://manga.bilibili.com/",
            "ep_id=" + match.Groups["ep"].Value,
            config,
            ct,
            ("Origin", "https://manga.bilibili.com"),
            ("X-Requested-With", "XMLHttpRequest"));

        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        var code = root.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number
            ? codeEl.GetInt32()
            : -1;
        if (code != 0)
        {
            throw new InvalidOperationException($"B 站图片接口返回 code={code}，可能需登录或该话已下架");
        }

        var images = new List<string>();
        if (root.TryGetProperty("data", out var data) &&
            data.TryGetProperty("images", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            foreach (var img in arr.EnumerateArray())
            {
                if (!img.TryGetProperty("path", out var pathEl)) continue;
                var path = pathEl.GetString();
                if (string.IsNullOrWhiteSpace(path)) continue;
                images.Add("https://manga.hdslb.com/bfs/manga/" + path.TrimStart('/'));
            }
        }

        return images;
    }

    public override string RefererFor(string chapterUrl) => "https://manga.bilibili.com/";
}
