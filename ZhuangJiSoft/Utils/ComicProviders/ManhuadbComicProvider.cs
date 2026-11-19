using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace DeskApp.Utils.ComicProviders;

/// <summary>漫画DB：作品页 /comic/数字，章节在 .links-of-books 或 #chapter-list 下。</summary>
public sealed class ManhuadbComicProvider : ComicProviderBase
{
    public override string Key => "manhuadb";
    public override string DisplayName => "漫画DB";
    public override string HomeUrl => "https://www.manhuadb.com";
    public override string[] Hosts => ["manhuadb.com"];

    public override string Usage =>
        "粘贴作品详情页（www.manhuadb.com/comic/数字）。该站语种版本较多，请选简体中文版本再解析。";

    protected override string[] ChapterSelectors =>
    [
        ".links-of-books a",
        "#chapter-list a",
        ".comic-chapters a",
        "a[href*='/comic/']"
    ];

    protected override string[] ImageSelectors =>
    [
        "#iimg",
        ".comic-page img",
        "#content img",
        "img.data-original",
        "img[data-src]"
    ];

    protected override string[] ChapterHrefKeywords => ["/comic/", "/manhua/"];

    public override Task<List<string>> ParseImagesAsync(string chapterUrl, string html,
        ComicConfig config, CancellationToken ct)
    {
        var images = ComicHtml.Images(html, chapterUrl, ImageSelectors);
        if (images.Count == 0)
        {
            images = ComicHtml.ImagesFromPattern(html, chapterUrl,
                "(?:https?:)?//[^\"'\\s]+?manhuadb[^\"'\\s]*?\\.(?:jpg|jpeg|png|webp|gif)");
        }

        return Task.FromResult(images);
    }

    public override string RefererFor(string chapterUrl) => "https://www.manhuadb.com/";
}
