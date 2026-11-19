using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using DeskApp.Models;

namespace DeskApp.Utils.PanProviders;

/// <summary>分享链接解析结果：文件列表 + 供后续取直链用的上下文。</summary>
public sealed class ShareParseResult
{
    public List<PanFile> Files { get; set; } = new();

    /// <summary>各网盘取直链所需的额外上下文（share_id / share_token / stoken 等），JSON 字符串。</summary>
    public string ContextJson { get; set; } = "{}";
}

/// <summary>网盘 Provider 抽象：分享链接解析 + 直链获取。</summary>
public interface IPanProvider
{
    PanSite Site { get; }

    /// <summary>解析分享链接，返回可下载文件列表。</summary>
    Task<ShareParseResult> ParseShareAsync(string shareUrl, PanCredential credential,
        CancellationToken ct = default);

    /// <summary>取单个文件的下载直链。</summary>
    Task<string> GetDirectLinkAsync(PanFile file, string contextJson, PanCredential credential,
        CancellationToken ct = default);
}
