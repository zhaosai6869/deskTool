using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DeskApp.Models;

namespace DeskApp.Utils.ComicProviders;

/// <summary>
/// Provider 基类：按站点写死的选择器解析章节与图片。
/// 子类只需覆盖选择器与少量规则；需要调接口的站点自行重写方法。
/// </summary>
public abstract class ComicProviderBase : IComicProvider
{
    public abstract string Key { get; }
    public abstract string DisplayName { get; }
    public abstract string HomeUrl { get; }
    public abstract string[] Hosts { get; }

    public virtual string Usage => "粘贴本站漫画目录页网址后点「解析章节」。仅支持公开免费章节。";

    /// <summary>章节链接选择器，按顺序尝试，第一个命中的生效。</summary>
    protected abstract string[] ChapterSelectors { get; }

    /// <summary>图片选择器，按顺序尝试，第一个命中的生效。</summary>
    protected abstract string[] ImageSelectors { get; }

    /// <summary>章节链接必须包含的关键字（为空表示不限制）。</summary>
    protected virtual string[] ChapterHrefKeywords => [];

    /// <summary>章节链接需要排除的关键字。</summary>
    protected virtual string[] ChapterHrefExclude => [];

    public bool Matches(Uri uri) => Hosts.Any(h =>
        uri.Host.Equals(h, StringComparison.OrdinalIgnoreCase) ||
        uri.Host.EndsWith("." + h, StringComparison.OrdinalIgnoreCase));

    public virtual async Task<ComicParseResult> ParseChaptersAsync(string url, ComicConfig config,
        CancellationToken ct)
    {
        var html = await ComicClient.GetHtmlAsync(url, config, ct);
        var chapters = ComicHtml.Chapters(html, url, ChapterSelectors, HrefAllowed);
        var title = ComicHtml.Title(html) ?? DisplayName;

        var hint = chapters.Count > 0
            ? $"{DisplayName}：识别到 {chapters.Count} 个章节"
            : $"{DisplayName}：未识别到章节。{Usage}";

        return new ComicParseResult(chapters, title, hint);
    }

    /// <summary>是否必须先抓章节页 HTML（走接口的站点如 B 站可置 false）。</summary>
    protected virtual bool NeedsChapterHtml => true;

    public async Task<List<string>> FetchImagesAsync(string chapterUrl, ComicConfig config,
        CancellationToken ct)
    {
        if (!NeedsChapterHtml) return await ParseImagesAsync(chapterUrl, string.Empty, config, ct);

        var html = await ComicClient.GetHtmlAsync(chapterUrl, config, ct);
        return await ParseImagesAsync(chapterUrl, html, config, ct);
    }

    public virtual Task<List<string>> ParseImagesAsync(string chapterUrl, string html,
        ComicConfig config, CancellationToken ct)
        => Task.FromResult(ComicHtml.Images(html, chapterUrl, ImageSelectors));

    public virtual string RefererFor(string chapterUrl) => chapterUrl;

    /// <summary>章节链接过滤：先看必须包含的关键字，再看排除关键字。</summary>
    protected virtual bool HrefAllowed(string href)
    {
        if (ChapterHrefKeywords.Length > 0 &&
            !ChapterHrefKeywords.Any(k => href.Contains(k, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        return !ChapterHrefExclude.Any(k => href.Contains(k, StringComparison.OrdinalIgnoreCase));
    }
}
