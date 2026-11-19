using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeskApp.Utils.ComicProviders;

/// <summary>WEBTOON（繁体中文站）：list 页章节链接 + 阅读页 #_imageList 下的图片。</summary>
public sealed class WebtoonComicProvider : ComicProviderBase
{
    public override string Key => "webtoon";
    public override string DisplayName => "WEBTOON";
    public override string HomeUrl => "https://www.webtoons.com/zh-hant";
    public override string[] Hosts => ["webtoons.com"];

    public override string Usage =>
        "粘贴作品列表页（webtoons.com/zh-hant/类型/作品名/list?title_no=数字）。仅支持公开免费话。";

    protected override string[] ChapterSelectors =>
    [
        "#_listUl li a",
        "ul#_listUl a",
        "a[href*='viewer']"
    ];

    protected override string[] ImageSelectors =>
    [
        "#_imageList img",
        ".viewer_img img",
        "img[data-url]",
        "#content img"
    ];

    protected override string[] ChapterHrefKeywords => ["viewer", "episode_no"];

    public override Task<List<string>> ParseImagesAsync(string chapterUrl, string html,
        ComicConfig config, CancellationToken ct)
    {
        var images = ComicHtml.Images(html, chapterUrl, ImageSelectors);
        if (images.Count == 0)
        {
            images = ComicHtml.ImagesFromPattern(html, chapterUrl,
                "(?:https?:)?//[^\"'\\s]+?webtoons[^\"'\\s]*?\\.(?:jpg|jpeg|png|webp)");
        }

        return Task.FromResult(images);
    }

    public override string RefererFor(string chapterUrl) => "https://www.webtoons.com/";
}
