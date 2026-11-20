using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DeskApp.Models;

namespace DeskApp.Utils.ComicProviders;

/// <summary>
/// 哔哩哔哩漫画：新版网页给 twirp 接口加了 x-bili-data-sn / ultra_sign 风控签名，
/// 原图（mangaup.hdslb.com + cpx）还叠加了 ECDH + Go wasm 的 DRM，纯 HTTP 无法复现。
/// 改为在隐藏 WebView2 里打开官方页面，让页面自己完成签名、鉴权与解密：
/// 章节读 seasonData.episodeList；图片逐页跳转后用页面 wasm 解密出的 canvas 导出 JPEG
/// （见 BiliWebSession）。导出的是已解密的本地 JPEG，下载层直接落盘。
/// </summary>
public sealed class BilibiliComicProvider : ComicProviderBase
{
    private const string Origin = "https://manga.bilibili.com";

    public override string Key => "bilibili";
    public override string DisplayName => "哔哩哔哩漫画";
    public override string HomeUrl => "https://manga.bilibili.com";
    public override string[] Hosts => ["manga.bilibili.com"];

    public override string Usage =>
        "先在「B漫 Cookie」右侧点「登录」扫码登录一次（登录态保存在本机，约 30 天）。" +
        "粘贴作品详情页网址（形如 /detail/mc26444），可下载免费章节与你已解锁的章节。";

    protected override string[] ChapterSelectors => [];
    protected override string[] ImageSelectors => [];

    /// <summary>B 站走内嵌网页采集，不需要抓章节页 HTML。</summary>
    protected override bool NeedsChapterHtml => false;

    // ───────────────────────── 章节解析 ─────────────────────────

    public override async Task<ComicParseResult> ParseChaptersAsync(string url, ComicConfig config,
        CancellationToken ct)
    {
        var match = Regex.Match(url, @"mc(?<id>\d+)", RegexOptions.IgnoreCase);
        if (!match.Success)
        {
            throw new InvalidOperationException("B 站漫画需要作品详情页网址，形如 /detail/mc26444");
        }

        var comicId = match.Groups["id"].Value;
        List<(string EpId, string Title)> chapters;
        try
        {
            chapters = await BiliWebSession.CollectChaptersAsync(comicId, ct);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                "B 漫章节读取失败：" + ex.Message + "。请确认已登录、能正常打开漫画详情页。", ex);
        }

        var result = new List<ComicChapter>();
        foreach (var (epId, rawTitle) in chapters)
        {
            var shortTitle = rawTitle.Trim();
            var name = shortTitle.Length > 0 ? shortTitle : $"ep{epId}";
            result.Add(new ComicChapter
            {
                Title = name,
                Url = $"https://manga.bilibili.com/mc{comicId}/{epId}",
                IsSelected = true
            });
        }

        var hint = result.Count == 0
            ? "B 站漫画：未识别到章节。请确认作品详情页网址正确、已扫码登录。"
            : $"B 站漫画：已读取 {result.Count} 章（含付费/锁定章节；能否下载取决于账号权益，逐章下载时才解密）";

        return new ComicParseResult(result, "B站漫画", hint);
    }

    // ───────────────────────── 图片地址 ─────────────────────────

    public override async Task<List<string>> ParseImagesAsync(string chapterUrl, string html,
        ComicConfig config, CancellationToken ct)
    {
        var m = Regex.Match(chapterUrl, @"mc(?<comic>\d+)/(?<ep>\d+)", RegexOptions.IgnoreCase);
        if (!m.Success) return [];

        var comicId = m.Groups["comic"].Value;
        var epId = m.Groups["ep"].Value;
        if (string.IsNullOrEmpty(comicId) || string.IsNullOrEmpty(epId)) return [];

        try
        {
            var urls = await BiliWebSession.CollectImagesAsync(comicId, epId, ct);
            return urls;
        }
        catch (Exception)
        {
            return [];
        }
    }

    public override string RefererFor(string chapterUrl) => Origin + "/";
}
