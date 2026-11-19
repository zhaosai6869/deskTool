using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeskApp.Models;

namespace DeskApp.Utils.PanProviders;

/// <summary>
/// 天翼云盘 Provider。API 端点来自 LinkSwift 提取：
///   /open/file/getFileDownloadUrl.action
/// 天翼分享直链通常需要登录态 cookie（WebView2 登录后提供）。
/// </summary>
public sealed class TianyiProvider : IPanProvider
{
    public PanSite Site => PanSite.Tianyi;
    private const string Base = "https://api.cloud.189.cn";

    public Task<ShareParseResult> ParseShareAsync(string shareUrl, PanCredential credential,
        CancellationToken ct = default)
    {
        // 天翼的分享文件列表接口在不同版本间变动较大，且强依赖登录态。
        // 这里返回带提示的空结果，具体分享页解析待用真实链接联调时补齐。
        var result = new ShareParseResult
        {
            ContextJson = JsonSerializer.Serialize(new { shareUrl })
        };
        return Task.FromResult(result);
    }

    public async Task<string> GetDirectLinkAsync(PanFile file, string contextJson, PanCredential credential,
        CancellationToken ct = default)
    {
        using var client = PanHttp.CreateClient(credential);
        var url = $"{Base}/open/file/getFileDownloadUrl.action?fileId={Uri.EscapeDataString(file.FileId)}";
        var resp = await PanHttp.GetStringAsync(client, url, null, ct);
        using var json = JsonDocument.Parse(resp);
        var root = json.RootElement;
        if (root.TryGetProperty("url", out var u)) return u.GetString() ?? "";
        if (root.TryGetProperty("downloadUrl", out var d)) return d.GetString() ?? "";
        throw new Exception("未找到直链：" + resp);
    }
}
