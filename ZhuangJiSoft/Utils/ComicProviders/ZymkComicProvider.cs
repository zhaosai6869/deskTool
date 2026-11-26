using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace DeskApp.Utils.ComicProviders;

/// <summary>
/// 知音漫客：作品页章节列表 + 阅读页图片。
/// 站点会把 zymk.cn 章节页跳到 kanman.com；正文图片不写在 img 标签里，
/// 而在 window.comicInfo.current_chapter.chapter_img_list 数组中。
/// 旧版用通用 img 选择器/图片正则，抓到的是推荐位封面和占位图，导致导出文件里图片不对。
/// </summary>
public sealed class ZymkComicProvider : ComicProviderBase
{
    private static readonly Regex ChapterImgListRegex = new(
        @"chapter_img_list\s*:\s*\[(?<items>[^\]]*)\]", RegexOptions.Compiled);

    private static readonly Regex QuotedUrlRegex = new(
        @"""([^""]+)""", RegexOptions.Compiled);

    public override string Key => "zymk";
    public override string DisplayName => "知音漫客";
    public override string HomeUrl => "https://www.zymk.cn";
    public override string[] Hosts => ["zymk.cn"];

    public override string Usage =>
        "粘贴作品详情页网址（www.zymk.cn/数字/）。图片取章节页内嵌的 chapter_img_list，" +
        "站点常年不稳定，打不开时可换站。";

    protected override string[] ChapterSelectors =>
    [
        ".list ul a",
        "#chapterList a",
        ".chapter-list a",
        ".zj_list a",
        "a[href*='.html']"
    ];

    /// <summary>正文图不在 img 标签里，禁用选择器兜底，避免误抓推荐封面。</summary>
    protected override string[] ImageSelectors => [];

    public override Task<List<string>> ParseImagesAsync(string chapterUrl, string html,
        ComicConfig config, CancellationToken ct)
    {
        var match = ChapterImgListRegex.Match(html);
        if (!match.Success) return Task.FromResult(new List<string>());

        var images = QuotedUrlRegex.Matches(match.Groups["items"].Value)
            .Select(m => m.Groups[1].Value.Replace("\\/", "/"))
            .Where(u => u.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                        u.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                        u.StartsWith("//", StringComparison.Ordinal))
            .Distinct()
            .ToList();

        return Task.FromResult(images);
    }

    public override string RefererFor(string chapterUrl) =>
        Regex.IsMatch(chapterUrl, @"zymk\.cn", RegexOptions.IgnoreCase)
            ? "https://www.zymk.cn/"
            : chapterUrl;
}
