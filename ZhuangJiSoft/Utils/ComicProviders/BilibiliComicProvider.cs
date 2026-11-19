using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DeskApp.Models;

namespace DeskApp.Utils.ComicProviders;

/// <summary>
/// 哔哩哔哩漫画：走官网 twirp 接口，必须携带登录 Cookie（SESSDATA）。
/// 链路：ComicDetail 取章节 → GetImageIndex 取索引 → .index 去头 9 字节 + XOR 解密 + 解 zip
/// → pics 路径 → ImageToken 换带 token 的图片地址 → 下载。
/// </summary>
public sealed class BilibiliComicProvider : ComicProviderBase
{
    private const string ApiBase = "https://manga.bilibili.com/twirp/comic.v1.Comic/";
    private const string Origin = "https://manga.bilibili.com";
    private const string DefaultImageHost = "https://manga.hdslb.com";

    public override string Key => "bilibili";
    public override string DisplayName => "哔哩哔哩漫画";
    public override string HomeUrl => "https://manga.bilibili.com";
    public override string[] Hosts => ["manga.bilibili.com"];

    public override string Usage =>
        "需要登录 Cookie：浏览器登录 manga.bilibili.com，F12 → 应用 → Cookie → 复制 SESSDATA 粘贴到上方「B漫 Cookie」框。" +
        "可下载免费章节与你自己已解锁的章节。";

    protected override string[] ChapterSelectors => [];
    protected override string[] ImageSelectors => [];

    /// <summary>B 站走 twirp 接口，不需要抓章节页 HTML。</summary>
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

        var cookie = RequireCookie(config);
        var comicId = match.Groups["id"].Value;

        using var doc = await CallAsync("ComicDetail?device=pc&platform=web",
            JsonSerializer.Serialize(new Dictionary<string, string> { ["comic_id"] = comicId }),
            url, cookie, config, ct);

        var data = doc.RootElement.GetProperty("data");
        var title = data.TryGetProperty("title", out var titleEl) ? titleEl.GetString() ?? "B站漫画" : "B站漫画";

        var chapters = new List<ComicChapter>();
        if (data.TryGetProperty("ep_list", out var eps) && eps.ValueKind == JsonValueKind.Array)
        {
            foreach (var ep in eps.EnumerateArray())
            {
                if (!ep.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var epId)) continue;

                var locked = ep.TryGetProperty("is_locked", out var lockEl) && lockEl.ValueKind == JsonValueKind.True;
                var ordinal = ep.TryGetProperty("ordinal", out var ordEl) && ordEl.ValueKind == JsonValueKind.Number
                    ? ordEl.GetDouble()
                    : 0;
                var shortTitle = ep.TryGetProperty("short_title", out var stEl) ? stEl.GetString() ?? "" : "";
                var epTitle = ep.TryGetProperty("title", out var epTitleEl) ? epTitleEl.GetString() ?? "" : "";

                var name = shortTitle.Trim();
                if (name.Length == 0)
                {
                    name = ordinal > 0 ? $"第{ordinal:0.##}话" : $"ep{epId}";
                }
                if (epTitle.Trim().Length > 0 && !epTitle.Trim().Equals(name, StringComparison.Ordinal))
                {
                    name = (name + " " + epTitle.Trim()).Trim();
                }

                chapters.Add(new ComicChapter
                {
                    Title = (locked ? "🔒 " : "") + name,
                    Url = $"https://manga.bilibili.com/mc{comicId}/{epId}",
                    IsSelected = !locked
                });
            }
        }

        var hint = chapters.Count == 0
            ? $"B 站漫画：接口未返回章节。{Usage}"
            : $"B 站漫画：{chapters.Count} 章（付费章节已取消勾选）";

        return new ComicParseResult(chapters, title, hint);
    }

    // ───────────────────────── 图片地址 ─────────────────────────

    public override async Task<List<string>> ParseImagesAsync(string chapterUrl, string html,
        ComicConfig config, CancellationToken ct)
    {
        var m = Regex.Match(chapterUrl, @"mc(?<comic>\d+)/(?<ep>\d+)", RegexOptions.IgnoreCase);
        long comicId = 0, epId = 0;

        if (m.Success)
        {
            long.TryParse(m.Groups["comic"].Value, out comicId);
            long.TryParse(m.Groups["ep"].Value, out epId);
        }
        else
        {
            var m2 = Regex.Match(chapterUrl, @"(?<ep>\d+)\s*$");
            if (!m2.Success) return [];
            long.TryParse(m2.Groups["ep"].Value, out epId);
        }

        if (epId <= 0) return [];

        var cookie = RequireCookie(config);
        var pics = new List<string>();
        var host = DefaultImageHost;

        using (var doc = await CallAsync("GetImageIndex?device=pc&platform=web",
                   JsonSerializer.Serialize(new Dictionary<string, string> { ["ep_id"] = epId.ToString() }),
                   chapterUrl, cookie, config, ct))
        {
            var data = doc.RootElement.GetProperty("data");

            if (data.TryGetProperty("host", out var hostEl) && hostEl.ValueKind == JsonValueKind.String)
            {
                var h = hostEl.GetString();
                if (!string.IsNullOrWhiteSpace(h)) host = h!.TrimEnd('/');
            }

            // 新版本接口可能直接给 images 列表
            if (data.TryGetProperty("images", out var direct) && direct.ValueKind == JsonValueKind.Array)
            {
                foreach (var img in direct.EnumerateArray())
                {
                    var s = img.ValueKind == JsonValueKind.Object
                        ? (img.TryGetProperty("path", out var p) ? p.GetString() : null)
                        : img.ValueKind == JsonValueKind.String ? img.GetString() : null;
                    if (!string.IsNullOrWhiteSpace(s)) pics.Add(s!);
                }
            }

            // 老版本：下载 .index 索引文件并解密
            if (pics.Count == 0 && data.TryGetProperty("path", out var pathEl))
            {
                var indexPath = pathEl.GetString();
                if (!string.IsNullOrWhiteSpace(indexPath))
                {
                    var indexUrl = indexPath!.StartsWith("http", StringComparison.OrdinalIgnoreCase)
                        ? indexPath
                        : host + (indexPath.StartsWith("/") ? indexPath : "/" + indexPath);

                    var raw = await ComicClient.DownloadBytesAsync(indexUrl, Origin + "/", config, ct);
                    if (raw is not null) pics = DecodeIndex(raw, comicId, epId);
                }
            }
        }

        if (pics.Count == 0) return [];

        // 换 token：不带 token 的图片地址会被 403
        var urlsJson = JsonSerializer.Serialize(pics);
        using (var doc = await CallAsync("ImageToken?device=pc&platform=web",
                   JsonSerializer.Serialize(new Dictionary<string, string> { ["urls"] = urlsJson }),
                   chapterUrl, cookie, config, ct))
        {
            var data = doc.RootElement.GetProperty("data");
            var result = new List<string>();

            if (data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;

                    var u = item.TryGetProperty("url", out var uEl) ? uEl.GetString() : null;
                    var token = item.TryGetProperty("token", out var tEl) ? tEl.GetString() : null;
                    if (string.IsNullOrWhiteSpace(u)) continue;

                    result.Add(string.IsNullOrWhiteSpace(token)
                        ? u!
                        : $"{u}?token={token}");
                }
            }

            if (result.Count > 0) return result;
        }

        return [];
    }

    public override string RefererFor(string chapterUrl) => Origin + "/";

    // ───────────────────────── 内部实现 ─────────────────────────

    private static string RequireCookie(ComicConfig config)
    {
        var raw = (config.BiliSessData ?? string.Empty).Trim();
        if (raw.Length == 0)
        {
            throw new InvalidOperationException(
                "B 站漫画需要登录 Cookie：请用浏览器登录 manga.bilibili.com，按 F12 → 应用 → Cookies，" +
                "复制 SESSDATA 的值，粘贴到界面上的「B漫 Cookie」输入框。");
        }

        return raw.Contains("SESSDATA=", StringComparison.OrdinalIgnoreCase) ? raw : "SESSDATA=" + raw;
    }

    private static async Task<JsonDocument> CallAsync(string endpoint, string body, string referer,
        string cookie, ComicConfig config, CancellationToken ct)
    {
        var text = await ComicClient.PostJsonAsync(ApiBase + endpoint, referer, body, config, ct,
            ("Cookie", cookie),
            ("Origin", Origin),
            ("X-Requested-With", "XMLHttpRequest"));

        var doc = JsonDocument.Parse(text);
        var code = doc.RootElement.TryGetProperty("code", out var codeEl) && codeEl.ValueKind == JsonValueKind.Number
            ? codeEl.GetInt32()
            : -1;

        if (code != 0)
        {
            var msg = doc.RootElement.TryGetProperty("msg", out var msgEl) ? msgEl.GetString() : null;
            doc.Dispose();

            if (code == 99)
            {
                throw new InvalidOperationException(
                    "B 站接口返回 code=99：登录 Cookie 缺失或已过期。请重新登录 manga.bilibili.com，" +
                    "复制 SESSDATA 后填入「B漫 Cookie」框" +
                    (string.IsNullOrWhiteSpace(msg) ? "" : "（接口提示：" + msg + "）"));
            }

            throw new InvalidOperationException(
                $"B 站接口返回 code={code}{(string.IsNullOrWhiteSpace(msg) ? "" : "，" + msg)}，可能接口变更或该作品已下架");
        }

        return doc;
    }

    /// <summary>解密 .index：去前 9 字节 → XOR（key = ep_id 4 字节 + comic_id 4 字节，小端）→ 解 zip → index.dat 的 pics。</summary>
    private static List<string> DecodeIndex(byte[] raw, long comicId, long epId)
    {
        var pics = new List<string>();
        if (raw.Length <= 9) return pics;

        var data = new byte[raw.Length - 9];
        Buffer.BlockCopy(raw, 9, data, 0, data.Length);

        var key = new byte[8];
        BitConverter.GetBytes((uint)epId).CopyTo(key, 0);
        BitConverter.GetBytes((uint)comicId).CopyTo(key, 4);

        for (var i = 0; i < data.Length; i++)
        {
            data[i] ^= key[i % 8];
        }

        try
        {
            using var ms = new MemoryStream(data);
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            var entry = zip.GetEntry("index.dat") ?? zip.Entries.FirstOrDefault();
            if (entry is null) return pics;

            using var sr = new StreamReader(entry.Open(), Encoding.UTF8);
            using var doc = JsonDocument.Parse(sr.ReadToEnd());
            if (!doc.RootElement.TryGetProperty("pics", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return pics;
            }

            foreach (var item in arr.EnumerateArray())
            {
                var s = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (!string.IsNullOrWhiteSpace(s)) pics.Add(s!);
            }
        }
        catch
        {
            // 索引格式变更时返回空，由上层给出「未找到图片」提示
        }

        return pics;
    }
}
