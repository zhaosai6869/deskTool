using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeskApp.Utils.ComicProviders;

/// <summary>知音漫客：作品页章节列表 + 阅读页图片。</summary>
public sealed class ZymkComicProvider : ComicProviderBase
{
    public override string Key => "zymk";
    public override string DisplayName => "知音漫客";
    public override string HomeUrl => "https://www.zymk.cn";
    public override string[] Hosts => ["zymk.cn"];

    public override string Usage =>
        "粘贴作品详情页网址（www.zymk.cn/数字/）。站点常年不稳定，打不开时可换站。";

    protected override string[] ChapterSelectors =>
    [
        ".list ul a",
        "#chapterList a",
        ".chapter-list a",
        ".zj_list a",
        "a[href*='.html']"
    ];

    protected override string[] ImageSelectors =>
    [
        "#content img",
        ".comic_img img",
        "#images img",
        "img[data-original]",
        "img[data-src]"
    ];

    public override Task<List<string>> ParseImagesAsync(string chapterUrl, string html,
        ComicConfig config, CancellationToken ct)
    {
        var images = ComicHtml.Images(html, chapterUrl, ImageSelectors);
        if (images.Count == 0)
        {
            images = ComicHtml.ImagesFromPattern(html, chapterUrl,
                "(?:https?:)?//[^\"'\\s]+?zymk[^\"'\\s]*?\\.(?:jpg|jpeg|png|webp|gif)");
        }

        return Task.FromResult(images);
    }

    public override string RefererFor(string chapterUrl) =>
        Regex.IsMatch(chapterUrl, @"zymk\.cn", RegexOptions.IgnoreCase)
            ? "https://www.zymk.cn/"
            : chapterUrl;
}
