using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DeskApp.Models;

namespace DeskApp.Utils.ComicProviders;

/// <summary>动漫之家：作品页 /info/数字.htm，章节链接在 #chapterlist 或 ul.list 下。</summary>
public sealed class DmzjComicProvider : ComicProviderBase
{
    public override string Key => "dmzj";
    public override string DisplayName => "动漫之家";
    public override string HomeUrl => "https://www.dmzj.com";
    public override string[] Hosts => ["dmzj.com", "m.dmzj.com"];

    public override string Usage =>
        "粘贴作品详情页（www.dmzj.com/info/数字.htm）。手机版页面 m.dmzj.com 亦可。";

    protected override string[] ChapterSelectors =>
    [
        "#chapterlist a",
        "#chapterlistwrap a",
        "ul.list a",
        ".cartoon_online_border a",
        "a[href*='.shtml']"
    ];

    protected override string[] ImageSelectors =>
    [
        "#images img",
        ".comic_wraCon img",
        "#content img",
        "img[data-original]",
        "img[data-src]"
    ];

    public override Task<List<string>> ParseImagesAsync(string chapterUrl, string html,
        ComicConfig config, CancellationToken ct)
    {
        var images = ComicHtml.Images(html, chapterUrl, ImageSelectors);
        if (images.Count == 0)
        {
            // 动漫之家常把整话图片压在一个 JS 数组里
            images = ComicHtml.ImagesFromPattern(html, chapterUrl,
                "(?:https?:)?//[^\"'\\s]+?dmzj[^\"'\\s]*?\\.(?:jpg|jpeg|png|webp|gif)");
        }
        if (images.Count == 0)
        {
            images = ComicHtml.ImagesFromPattern(html, chapterUrl,
                "\"path\"\\s*:\\s*\"(?<u>[^\"]+?\\.(?:jpg|jpeg|png|webp))\"");
        }

        return Task.FromResult(images);
    }

    public override string RefererFor(string chapterUrl) =>
        Regex.IsMatch(chapterUrl, @"m\.dmzj\.com", RegexOptions.IgnoreCase)
            ? "https://m.dmzj.com/"
            : "https://www.dmzj.com/";
}
