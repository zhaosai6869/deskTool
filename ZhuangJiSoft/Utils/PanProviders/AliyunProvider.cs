using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeskApp.Models;

namespace DeskApp.Utils.PanProviders;

/// <summary>
/// 阿里云盘 Provider。API 端点来自 LinkSwift 提取：
///   /v2/share_link/get_share_token
///   /v2/file/list_by_share
///   /v2/file/get_share_link_download_url
/// 需要登录态（x-share-token / Authorization），由 WebView2 登录后提供。
/// </summary>
public sealed class AliyunProvider : IPanProvider
{
    public PanSite Site => PanSite.Aliyun;
    private const string Base = "https://api.aliyundrive.com";

    public async Task<ShareParseResult> ParseShareAsync(string shareUrl, PanCredential credential,
        CancellationToken ct = default)
    {
        var result = new ShareParseResult();
        var shareId = ExtractShareId(shareUrl);
        if (string.IsNullOrWhiteSpace(shareId))
            throw new Exception("无法从链接识别分享 ID，请确认是阿里云盘分享链接");

        using var client = PanHttp.CreateClient(credential);

        // 1) 取 share token
        var tokenResp = await PanHttp.PostJsonAsync(client, Base + "/v2/share_link/get_share_token",
            JsonSerializer.Serialize(new { share_id = shareId }), null, ct);
        using var tokenJson = JsonDocument.Parse(tokenResp);
        var root = tokenJson.RootElement;
        if (!root.TryGetProperty("share_token", out var st))
            throw new Exception("获取 share_token 失败：" + tokenResp);
        var shareToken = st.GetString() ?? "";

        var shareHeaders = new Dictionary<string, string> { ["x-share-token"] = shareToken };

        // 2) 列出文件
        var listResp = await PanHttp.PostJsonAsync(client, Base + "/v2/file/list_by_share",
            JsonSerializer.Serialize(new { share_id = shareId, parent_file_id = "root", limit = 200 }),
            shareHeaders, ct);
        using var listJson = JsonDocument.Parse(listResp);
        var listRoot = listJson.RootElement;
        if (!listRoot.TryGetProperty("items", out var items))
            throw new Exception("解析文件列表失败：" + listResp);

        foreach (var item in items.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (type == "folder") continue;
            var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "未命名" : "未命名";
            var fid = item.TryGetProperty("file_id", out var f) ? f.GetString() ?? "" : "";
            var size = item.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
            result.Files.Add(new PanFile { Name = name, FileId = fid, Size = size });
        }

        result.ContextJson = JsonSerializer.Serialize(new { shareId, shareToken });
        return result;
    }

    public async Task<string> GetDirectLinkAsync(PanFile file, string contextJson, PanCredential credential,
        CancellationToken ct = default)
    {
        using var ctx = JsonDocument.Parse(contextJson);
        var shareId = ctx.RootElement.GetProperty("shareId").GetString() ?? "";
        var shareToken = ctx.RootElement.GetProperty("shareToken").GetString() ?? "";

        using var client = PanHttp.CreateClient(credential);
        var headers = new Dictionary<string, string> { ["x-share-token"] = shareToken };
        var body = JsonSerializer.Serialize(new
        {
            file_id = file.FileId,
            share_id = shareId
        });
        var resp = await PanHttp.PostJsonAsync(client,
            Base + "/v2/file/get_share_link_download_url", body, headers, ct);
        using var json = JsonDocument.Parse(resp);
        var root = json.RootElement;
        if (root.TryGetProperty("download_url", out var url))
            return url.GetString() ?? throw new Exception("直链为空");
        throw new Exception("未找到直链：" + resp);
    }

    private static string ExtractShareId(string url)
    {
        var m = System.Text.RegularExpressions.Regex.Match(url, @"/s/([A-Za-z0-9]+)");
        return m.Success ? m.Groups[1].Value : string.Empty;
    }
}
