using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DeskApp.Models;

namespace DeskApp.Utils.ComicProviders;

/// <summary>单站解析结果：章节列表 + 书名 + 给用户看的提示。</summary>
public sealed record ComicParseResult(List<ComicChapter> Chapters, string BookTitle, string Hint);

/// <summary>
/// 漫画站点解析器。每个支持的站点实现一套自己的解析逻辑，
/// 不再有通用启发式识别，也不再让用户手填 CSS 选择器。
/// </summary>
public interface IComicProvider
{
    /// <summary>与 ComicSite.Key 对应。</summary>
    string Key { get; }

    string DisplayName { get; }

    string HomeUrl { get; }

    /// <summary>可匹配的主机名（含子域）。</summary>
    string[] Hosts { get; }

    /// <summary>站点能力/限制说明，显示在界面上。</summary>
    string Usage { get; }

    bool Matches(Uri uri);

    Task<ComicParseResult> ParseChaptersAsync(string url, ComicConfig config, CancellationToken ct);

    /// <summary>抓取该话的全部图片地址（内部按站点各自逻辑实现）。</summary>
    Task<List<string>> FetchImagesAsync(string chapterUrl, ComicConfig config, CancellationToken ct);

    /// <summary>该站图片下载时应带的 Referer。</summary>
    string RefererFor(string chapterUrl);
}
