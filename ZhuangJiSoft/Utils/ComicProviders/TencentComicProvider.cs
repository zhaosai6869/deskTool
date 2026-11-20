using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DeskApp.Utils;

namespace DeskApp.Utils.ComicProviders;

/// <summary>腾讯动漫：作品页章节列表 + 阅读页图片，按站点写死选择器。</summary>
public sealed class TencentComicProvider : ComicProviderBase
{
    public override string Key => "qq";
    public override string DisplayName => "腾讯动漫";
    public override string HomeUrl => "https://ac.qq.com";
    public override string[] Hosts => ["ac.qq.com"];

    public override string Usage =>
        "粘贴作品详情页（ac.qq.com/Comic/ComicInfo/id/数字）。付费章节图片受版权保护，通常只能拿到试读页。";

    protected override string[] ChapterSelectors =>
    [
        "#chapterList a",
        "ul#chapterList li a",
        ".works-chapter-item a",
        ".chapter-item a",
        ".chapter-list a"
    ];

    protected override string[] ImageSelectors =>
    [
        "#comicContain img",
        ".comic-contain img",
        "#content img",
        "img[data-src]"
    ];

    protected override string[] ChapterHrefKeywords => ["/ComicView/"];

    public override Task<List<string>> ParseImagesAsync(string chapterUrl, string html,
        ComicConfig config, CancellationToken ct)
    {
        // 腾讯现在把图片列表加密在 var DATA + nonce 里，静态 img 标签只剩第一张预加载图。
        // 先走 DATA 解密；失败再回退旧的 img/正则方案（兼容个别老页面）。
        var images = TencentComicSession.ParseImages(html);
        if (images.Count == 0)
        {
            images = ComicHtml.Images(html, chapterUrl, ImageSelectors);
            if (images.Count == 0)
            {
                images = ComicHtml.ImagesFromPattern(html, chapterUrl,
                    "\"url\"\\s*:\\s*\"(?<u>[^\"]+?\\.(?:jpg|jpeg|png|webp|gif))\"");
            }
        }

        return Task.FromResult(images);
    }
}
