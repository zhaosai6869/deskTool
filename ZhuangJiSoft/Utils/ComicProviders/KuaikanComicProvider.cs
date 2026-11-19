using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeskApp.Utils.ComicProviders;

/// <summary>快看漫画：主题页章节链接（/web/comic/数字）+ 阅读页图片。</summary>
public sealed class KuaikanComicProvider : ComicProviderBase
{
    public override string Key => "kuaikan";
    public override string DisplayName => "快看漫画";
    public override string HomeUrl => "https://www.kuaikanmanhua.com";
    public override string[] Hosts => ["kuaikanmanhua.com", "kkmh.com"];

    public override string Usage =>
        "粘贴作品主题页（/web/topic/数字）。快看对未登录访问限制较多，部分作品需要登录后才能看到完整图片。";

    protected override string[] ChapterSelectors =>
    [
        "a[href*='/web/comic/']",
        ".kukai-info a",
        ".chapter-list a",
        "#chapterList a"
    ];

    protected override string[] ImageSelectors =>
    [
        ".kukai-comic img",
        ".comic-img img",
        "#content img",
        "img[data-src]"
    ];

    public override Task<List<string>> ParseImagesAsync(string chapterUrl, string html,
        ComicConfig config, CancellationToken ct)
    {
        var images = ComicHtml.Images(html, chapterUrl, ImageSelectors);
        if (images.Count == 0)
        {
            images = ComicHtml.ImagesFromPattern(html, chapterUrl,
                "(?:https?:)?//[^\"'\\s]+?kkmh\\.com/[^\"'\\s]+?\\.(?:jpg|jpeg|png|webp|gif)");
        }

        return Task.FromResult(images);
    }
}
