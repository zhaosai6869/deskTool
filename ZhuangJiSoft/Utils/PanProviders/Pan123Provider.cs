using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeskApp.Models;

namespace DeskApp.Utils.PanProviders;

/// <summary>
/// 123 云盘 Provider。API 端点来自 LinkSwift 提取：
///   /api/share/detail  - 分享详情/文件列表
///   /api/share/download/info - 取直链
/// </summary>
public sealed class Pan123Provider : IPanProvider
{
    public PanSite Site => PanSite.Pan123;
    private const string Base = "https://www.123pan.com";

    public async Task<ShareParseResult> ParseShareAsync(string shareUrl, PanCredential credential,
        CancellationToken ct = default)
    {
        var result = new ShareParseResult();
        var m = System.Text.RegularExpressions.Regex.Match(shareUrl, @"/s/([A-Za-z0-9]+)");
        if (!m.Success) throw new Exception("无法从链接识别 123 分享 ID");
        var shareId = m.Groups[1].Value;
        var pm = System.Text.RegularExpressions.Regex.Match(shareUrl, @"[?&]pwd=([^&]+)");
        var pwd = pm.Success ? Uri.UnescapeDataString(pm.Groups[1].Value) : string.Empty;

        using var client = PanHttp.CreateClient(credential);
        var body = JsonSerializer.Serialize(new { shareKey = shareId, accessCode = pwd });
        var resp = await PanHttp.PostJsonAsync(client, Base + "/api/share/detail", body,
            new Dictionary<string, string> { ["Content-Type"] = "application/json" }, ct);
        using var json = JsonDocument.Parse(resp);
        var root = json.RootElement;
        if (!root.TryGetProperty("data", out var data))
            throw new Exception("解析分享失败：" + resp);

        if (data.TryGetProperty("fileList", out var files))
        {
            foreach (var item in files.EnumerateArray())
            {
                var isDir = item.TryGetProperty("type", out var tp) && tp.GetInt32() == 1;
                if (isDir) continue;
                var name = item.TryGetProperty("filename", out var n) ? n.GetString() ?? "未命名" : "未命名";
                var fid = item.TryGetProperty("fileId", out var f) && f.TryGetInt64(out var fv)
                    ? fv.ToString() : "";
                var size = item.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
                result.Files.Add(new PanFile { Name = name, FileId = fid, Size = size });
            }
        }

        // s3key 等上下文
        var s3key = data.TryGetProperty("s3key", out var sk) ? sk.GetString() ?? "" : "";
        result.ContextJson = JsonSerializer.Serialize(new { shareId, pwd, s3key });
        return result;
    }

    public async Task<string> GetDirectLinkAsync(PanFile file, string contextJson, PanCredential credential,
        CancellationToken ct = default)
    {
        using var ctx = JsonDocument.Parse(contextJson);
        var shareId = ctx.RootElement.GetProperty("shareId").GetString() ?? "";
        var pwd = ctx.RootElement.GetProperty("pwd").GetString() ?? "";
        var s3key = ctx.RootElement.GetProperty("s3key").GetString() ?? "";

        using var client = PanHttp.CreateClient(credential);
        var url = $"{Base}/api/share/download/info?fileId={file.FileId}&shareKey={Uri.EscapeDataString(shareId)}" +
                  $"&accessCode={Uri.EscapeDataString(pwd)}&s3key={Uri.EscapeDataString(s3key)}";
        var resp = await PanHttp.GetStringAsync(client, url, null, ct);
        using var json = JsonDocument.Parse(resp);
        var root = json.RootElement;
        if (!root.TryGetProperty("data", out var data)) throw new Exception("获取直链失败：" + resp);
        if (data.TryGetProperty("downloadUrl", out var dl)) return dl.GetString() ?? "";
        throw new Exception("未找到直链：" + resp);
    }
}
