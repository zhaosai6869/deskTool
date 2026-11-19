using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeskApp.Models;

namespace DeskApp.Utils.PanProviders;

/// <summary>
/// 夸克 / UC 网盘 Provider。两家 API 同构（clouddrive），共用实现，仅 base/UA/pr 不同。
/// API 端点来自 LinkSwift 提取：drive-pc.quark.cn / pc-api.uc.cn 的 1/clouddrive/... 系列。
/// </summary>
public sealed class QuarkProvider : IPanProvider
{
    private readonly bool _isUc;
    private readonly string _base;
    private readonly string _pr;

    public QuarkProvider(bool isUc = false)
    {
        _isUc = isUc;
        _base = isUc ? "https://pc-api.uc.cn" : "https://drive-pc.quark.cn";
        _pr = isUc ? "UCBrowser" : "ucpro";
    }

    public PanSite Site => _isUc ? PanSite.UC : PanSite.Quark;

    private const string Ua = "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
                              "quark-cloud-drive/3.20.0 Chrome/112.0.5615.165 Electron/24.1.3.8 Safari/537.36 " +
                              "Channel/pckk_other_ch";

    public async Task<ShareParseResult> ParseShareAsync(string shareUrl, PanCredential credential,
        CancellationToken ct = default)
    {
        var result = new ShareParseResult();
        // 从分享链接提取 share token：形如 /s/xxxxxx 或 ?pwd=
        var (shareId, pwd) = ExtractShare(shareUrl);
        if (string.IsNullOrWhiteSpace(shareId))
            throw new Exception("无法从链接中识别分享 ID，请确认是夸克/UC 的分享链接");

        using var client = PanHttp.CreateClient(credential, Ua);

        // 1) 取 stoken
        var tokenUrl = $"{_base}/1/clouddrive/share/sharepage/token?pr={_pr}&fr=pc";
        var tokenBody = JsonSerializer.Serialize(new { pwd, share_id = shareId, fr = "pc" });
        var tokenResp = await PanHttp.PostJsonAsync(client, tokenUrl, tokenBody,
            new Dictionary<string, string> { ["Content-Type"] = "application/json" }, ct);
        using var tokenJson = JsonDocument.Parse(tokenResp);
        var tokenRoot = tokenJson.RootElement;
        if (!tokenRoot.TryGetProperty("data", out var data) ||
            !data.TryGetProperty("stoken", out var stokenEl))
            throw new Exception("获取分享 stoken 失败：" + tokenResp);
        var stoken = stokenEl.GetString() ?? string.Empty;

        // 2) 取文件列表
        var listUrl = $"{_base}/1/clouddrive/share/sharepage/detail?pr={_pr}&fr=pc&stoken={Uri.EscapeDataString(stoken)}" +
                      $"&share_id={Uri.EscapeDataString(shareId)}&fid=0&page=1&size=1000&asc=1";
        var listResp = await PanHttp.GetStringAsync(client, listUrl, null, ct);
        using var listJson = JsonDocument.Parse(listResp);
        var listRoot = listJson.RootElement;
        if (!listRoot.TryGetProperty("data", out var d) ||
            !d.TryGetProperty("items", out var items))
            throw new Exception("解析文件列表失败：" + listResp);

        foreach (var item in items.EnumerateArray())
        {
            var kind = item.TryGetProperty("kind", out var k) ? k.GetString() : null;
            if (kind == "folder") continue; // 暂不递归文件夹
            var name = item.TryGetProperty("name", out var n) ? n.GetString() ?? "未命名" : "未命名";
            var fid = item.TryGetProperty("fid", out var f) ? f.GetString() ?? "" : "";
            var size = item.TryGetProperty("size", out var s) && s.TryGetInt64(out var sv) ? sv : 0;
            result.Files.Add(new PanFile { Name = name, FileId = fid, Size = size });
        }

        result.ContextJson = JsonSerializer.Serialize(new { shareId, stoken });
        return result;
    }

    public async Task<string> GetDirectLinkAsync(PanFile file, string contextJson, PanCredential credential,
        CancellationToken ct = default)
    {
        using var ctx = JsonDocument.Parse(contextJson);
        var shareId = ctx.RootElement.GetProperty("shareId").GetString() ?? "";
        var stoken = ctx.RootElement.GetProperty("stoken").GetString() ?? "";

        using var client = PanHttp.CreateClient(credential, Ua);
        var url = $"{_base}/1/clouddrive/file/download?entry=ft&fr=pc&pr={_pr}";
        var body = JsonSerializer.Serialize(new
        {
            fids = new[] { file.FileId },
            share_id = shareId,
            stoken = stoken
        });
        var resp = await PanHttp.PostJsonAsync(client, url, body,
            new Dictionary<string, string> { ["Content-Type"] = "application/json" }, ct);
        using var json = JsonDocument.Parse(resp);
        var root = json.RootElement;
        if (!root.TryGetProperty("data", out var data)) throw new Exception("获取直链失败：" + resp);
        // 夸克/UC 返回 data.download_url 数组
        if (data.TryGetProperty("download_url", out var arr) && arr.GetArrayLength() > 0)
            return arr[0].GetString() ?? throw new Exception("直链为空");
        if (data.TryGetProperty("download_urls", out var arr2) && arr2.GetArrayLength() > 0)
        {
            var first = arr2[0];
            if (first.TryGetProperty("url", out var u)) return u.GetString() ?? "";
        }
        throw new Exception("未找到直链字段：" + resp);
    }

    private static (string shareId, string pwd) ExtractShare(string url)
    {
        // 形如 https://pan.quark.cn/s/abcdef123 或 /s/abcdef123?pwd=xxxx
        var m = System.Text.RegularExpressions.Regex.Match(url, @"/s/([A-Za-z0-9]+)");
        var id = m.Success ? m.Groups[1].Value : string.Empty;
        var pm = System.Text.RegularExpressions.Regex.Match(url, @"[?&]pwd=([^&]+)");
        var pwd = pm.Success ? Uri.UnescapeDataString(pm.Groups[1].Value) : string.Empty;
        return (id, pwd);
    }
}
