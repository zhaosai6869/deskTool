using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeskApp.Models;

namespace DeskApp.Utils.PanProviders;

/// <summary>
/// 百度网盘 Provider。
/// LinkSwift 1.1.1.6 起废弃 BDUSS Cookie，转向 AccessToken。
/// 分享文件列表走 /share/rest/ 系列，直链走 /api/... + access_token。
/// 百度风控最严，此处为基础骨架，待真实链接联调补齐签名细节。
/// </summary>
public sealed class BaiduProvider : IPanProvider
{
    public PanSite Site => PanSite.Baidu;

    public Task<ShareParseResult> ParseShareAsync(string shareUrl, PanCredential credential,
        CancellationToken ct = default)
    {
        var result = new ShareParseResult { ContextJson = JsonSerializer.Serialize(new { shareUrl }) };
        return Task.FromResult(result);
    }

    public async Task<string> GetDirectLinkAsync(PanFile file, string contextJson, PanCredential credential,
        CancellationToken ct = default)
    {
        var token = ExtractAccessToken(credential);
        if (string.IsNullOrWhiteSpace(token))
            throw new Exception("百度网盘需要 AccessToken，请先扫码登录并确保凭据包含 token");
        using var client = PanHttp.CreateClient(credential);
        var url = $"https://pan.baidu.com/rest/2.0/xpan/multimedia?method=filemetas&access_token=" +
                  $"{Uri.EscapeDataString(token)}&fsids=[{file.FileId}]&dlink=1";
        var resp = await PanHttp.GetStringAsync(client, url, null, ct);
        using var json = JsonDocument.Parse(resp);
        var root = json.RootElement;
        if (root.TryGetProperty("list", out var list) && list.GetArrayLength() > 0)
        {
            var first = list[0];
            if (first.TryGetProperty("dlink", out var d)) return d.GetString() ?? "";
        }
        throw new Exception("未找到直链：" + resp);
    }

    private static string ExtractAccessToken(PanCredential cred)
    {
        try
        {
            using var json = JsonDocument.Parse(cred.AccessToken);
            foreach (var p in json.RootElement.EnumerateObject())
            {
                if (p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() is string v &&
                    v.Length > 20) return v;
            }
        }
        catch { /* AccessToken 可能就是裸 token */ }
        return string.Empty;
    }
}

/// <summary>
/// 迅雷云盘 Provider。API 端点来自 LinkSwift 提取：api-pan.xunlei.com/drive/v1/files/
/// </summary>
public sealed class XunleiProvider : IPanProvider
{
    public PanSite Site => PanSite.Xunlei;

    public Task<ShareParseResult> ParseShareAsync(string shareUrl, PanCredential credential,
        CancellationToken ct = default)
    {
        return Task.FromResult(new ShareParseResult
        {
            ContextJson = JsonSerializer.Serialize(new { shareUrl })
        });
    }

    public async Task<string> GetDirectLinkAsync(PanFile file, string contextJson, PanCredential credential,
        CancellationToken ct = default)
    {
        using var client = PanHttp.CreateClient(credential);
        var url = $"https://api-pan.xunlei.com/drive/v1/files/{Uri.EscapeDataString(file.FileId)}?op=download";
        var resp = await PanHttp.GetStringAsync(client, url, null, ct);
        using var json = JsonDocument.Parse(resp);
        var root = json.RootElement;
        if (root.TryGetProperty("download_url", out var u)) return u.GetString() ?? "";
        if (root.TryGetProperty("url", out var u2)) return u2.GetString() ?? "";
        throw new Exception("未找到直链：" + resp);
    }
}

/// <summary>
/// 中国移动云盘 Provider。API 域名来自 LinkSwift 提取：yun.139.com / personal-kd-njs.yun.139.com
/// </summary>
public sealed class MobileProvider : IPanProvider
{
    public PanSite Site => PanSite.Mobile;

    public Task<ShareParseResult> ParseShareAsync(string shareUrl, PanCredential credential,
        CancellationToken ct = default)
    {
        return Task.FromResult(new ShareParseResult
        {
            ContextJson = JsonSerializer.Serialize(new { shareUrl })
        });
    }

    public async Task<string> GetDirectLinkAsync(PanFile file, string contextJson, PanCredential credential,
        CancellationToken ct = default)
    {
        using var client = PanHttp.CreateClient(credential);
        var url = $"https://personal-kd-njs.yun.139.com/orb/caas/download?fileId={Uri.EscapeDataString(file.FileId)}";
        var resp = await PanHttp.GetStringAsync(client, url, null, ct);
        using var json = JsonDocument.Parse(resp);
        var root = json.RootElement;
        if (root.TryGetProperty("downloadUrl", out var u)) return u.GetString() ?? "";
        if (root.TryGetProperty("url", out var u2)) return u2.GetString() ?? "";
        throw new Exception("未找到直链：" + resp);
    }
}
