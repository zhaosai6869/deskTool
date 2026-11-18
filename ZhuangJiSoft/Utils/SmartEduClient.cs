using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DeskApp.Models;

namespace DeskApp.Utils;

/// <summary>
/// 智慧教育平台客户端：拉取标签树与教材清单，按 tag 路径筛选，
/// 从详情 JSON 解析 PDF 直链，按 UC SDK 规则生成 X-ND-AUTH 签名后下载。
/// 参考 happycola233/tchMaterial-parser 与 lss53/sed-dl 的公开实现。
/// </summary>
public static class SmartEduClient
{
    private const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36";

    private const string NdrPrivate = "https://r1-ndr-private.ykt.cbern.com.cn";
    private static readonly string[] NdrMirrors =
    [
        "https://r1-ndr-private.ykt.cbern.com.cn",
        "https://r2-ndr-private.ykt.cbern.com.cn",
        "https://r3-ndr-private.ykt.cbern.com.cn"
    ];
    private static readonly string[] NonceAlphabet =
        "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ".Select(c => c.ToString()).ToArray();

    private static readonly HttpClient Http = new(new HttpClientHandler
    {
        AllowAutoRedirect = true,
        AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan
    };

    /// <summary>一次拉取到的全量教材清单（含 tag 路径）与标签树。</summary>
    public sealed class Catalog
    {
        public SmartEduCatalog.TagTree Tree { get; init; } = new();
        public List<SmartEduItem> Items { get; init; } = [];

        /// <summary>
        /// 从教材 tag_paths 直接构建的四级联动表：key 为上级 tag_id，value 为下一级选项。
        /// 比 tag 树嵌套结构更可靠——每本教材自带完整 tag_path，平台改版也不易失效。
        /// </summary>
        public Dictionary<string, List<SmartEduOption>> SubjectsByStage { get; } = new();
        public Dictionary<string, List<SmartEduOption>> EditionsBySubject { get; } = new();
        public Dictionary<string, List<SmartEduOption>> GradesByEdition { get; } = new();

        private static void AddOption(Dictionary<string, List<SmartEduOption>> map,
            string parentId, SmartEduOption option)
        {
            if (string.IsNullOrEmpty(parentId))
            {
                return;
            }
            if (!map.TryGetValue(parentId, out var list))
            {
                list = [];
                map[parentId] = list;
            }
            if (!list.Any(x => x.Id == option.Id))
            {
                list.Add(option);
            }
        }

        public void AddBookTags(string[] ids,
            Dictionary<string, (string Name, string Dimension)> names)
        {
            string? stage = null, subject = null, edition = null;
            foreach (var id in ids)
            {
                if (!names.TryGetValue(id, out var pair))
                {
                    continue;
                }
                switch (pair.Dimension)
                {
                    case SmartEduCatalog.DimStage:
                        stage = id;
                        break;
                    case SmartEduCatalog.DimSubject:
                    case SmartEduCatalog.DimSpecialType:
                        subject = id;
                        break;
                    case SmartEduCatalog.DimEdition:
                        edition = id;
                        break;
                }
            }
            if (stage is null)
            {
                return;
            }
            if (subject is not null)
            {
                AddOption(SubjectsByStage, stage,
                    new SmartEduOption(names[subject].Name, subject));
            }
            if (edition is not null && subject is not null)
            {
                // 学科 tag_id 跨学段重复，版本需按 学段|学科 组合键归属
                var editionKey = $"{stage}|{subject}";
                AddOption(EditionsBySubject, editionKey,
                    new SmartEduOption(names[edition].Name, edition));
            }
            // 版本 tag_id 跨学段重复（如“统编版”小学/初中/高中同 ID），
            // 年级必须按 学段|学科|版本 组合键归属，否则会把小学年级混进初中
            var grade = ids.FirstOrDefault(id =>
                names.TryGetValue(id, out var p) && p.Dimension == SmartEduCatalog.DimGrade);
            if (grade is not null && edition is not null && subject is not null)
            {
                var gradeKey = $"{stage}|{subject}|{edition}";
                AddOption(GradesByEdition, gradeKey,
                    new SmartEduOption(names[grade].Name, grade));
            }
        }

        public string RefreshedAt { get; init; } = string.Empty;
    }

    public sealed record PdfLink(string Url, string FileName);

    /// <summary>联网拉取标签树 + 全部分片，得到完整教材目录。</summary>
    public static async Task<Catalog> LoadCatalogAsync(int timeoutSeconds, CancellationToken ct)
    {
        var tagsJson = await GetStringAsync(SmartEduCatalog.TagDimensionsUrl, timeoutSeconds, ct);
        using var tagsDoc = JsonDocument.Parse(tagsJson);
        var tree = SmartEduCatalog.ParseTagTree(tagsDoc.RootElement);

        var versionJson = await GetStringAsync(SmartEduCatalog.VersionUrl, timeoutSeconds, ct);
        using var versionDoc = JsonDocument.Parse(versionJson);
        var urlsText = versionDoc.RootElement.TryGetProperty("urls", out var u)
            ? u.GetString() ?? string.Empty
            : string.Empty;
        var partUrls = urlsText.Split(',', System.StringSplitOptions.RemoveEmptyEntries | System.StringSplitOptions.TrimEntries);
        if (partUrls.Length == 0)
        {
            throw new InvalidOperationException("平台未返回教材清单分片地址");
        }

        // 菜单维度信息用于把 tag_id 还原成中文名
        var names = CollectTagNames(tagsDoc.RootElement);
        var items = new List<SmartEduItem>();
        var catalog = new Catalog
        {
            Tree = tree,
            RefreshedAt = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm")
        };
        foreach (var partUrl in partUrls)
        {
            var partJson = await GetWithRetryAsync(partUrl, timeoutSeconds, ct);
            using var partDoc = JsonDocument.Parse(partJson);
            if (partDoc.RootElement.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var book in partDoc.RootElement.EnumerateArray())
            {
                if (book.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                var id = GetString(book, "id");
                if (id.Length == 0)
                {
                    continue;
                }

                var paths = GetStringArray(book, "tag_paths");
                var primaryPath = paths.FirstOrDefault() ?? string.Empty;
                var ids = primaryPath.Split('/', System.StringSplitOptions.RemoveEmptyEntries);
                if (ids.Length == 0)
                {
                    continue;
                }

                // 用教材自带 tag 路径构建四级联动表，不依赖 tag 树嵌套结构
                catalog.AddBookTags(ids, names);

                var title = GetString(book, "title");
                if (title.Length == 0 && book.TryGetProperty("global_title", out var gt))
                {
                    title = gt.ValueKind == JsonValueKind.String
                        ? gt.GetString() ?? string.Empty
                        : ReadLocalizedTitle(gt);
                }

                items.Add(new SmartEduItem
                {
                    Id = id,
                    Title = title.Length > 0 ? title : id,
                    Stage = Lookup(ids, SmartEduCatalog.DimStage),
                    Subject = Lookup(ids, SmartEduCatalog.DimSubject, SmartEduCatalog.DimSpecialType),
                    Edition = Lookup(ids, SmartEduCatalog.DimEdition),
                    Grade = Lookup(ids, SmartEduCatalog.DimGrade),
                    Provider = GetProvider(book),
                    TagPath = primaryPath,
                    DetailUrl = $"{SmartEduCatalog.BaseUrl}/tchMaterial/detail?contentType=assets_document&contentId={id}&catalogType=tchMaterial&subCatalog=tchMaterial"
                });

                string Lookup(string[] tagIds, params string[] dimensions)
                {
                    foreach (var tagId in tagIds)
                    {
                        if (names.TryGetValue(tagId, out var pair) && dimensions.Contains(pair.Dimension))
                        {
                            return pair.Name;
                        }
                    }
                    return string.Empty;
                }
            }
        }

        catalog.Items.AddRange(items);
        return catalog;
    }

    /// <summary>按用户选择的四级 tag_id 与关键词过滤教材。</summary>
    public static List<SmartEduItem> Filter(
        Catalog catalog, SmartEduQuery q, int timeoutSeconds, CancellationToken ct)
    {
        var keyword = q.Keyword.Trim();
        return catalog.Items.Where(item =>
        {
            if (!string.IsNullOrEmpty(q.StageId) && !HasTag(item, q.StageId)) return false;
            if (!string.IsNullOrEmpty(q.SubjectId) && !HasTag(item, q.SubjectId)) return false;
            if (!string.IsNullOrEmpty(q.EditionId) && !HasTag(item, q.EditionId)) return false;
            if (!string.IsNullOrEmpty(q.GradeId) && !HasTag(item, q.GradeId)) return false;
            if (keyword.Length > 0 &&
                !item.Title.Contains(keyword, System.StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
            return true;
        }).ToList();
    }

    private static bool HasTag(SmartEduItem item, string tagId) =>
        item.TagPath.Split('/').Contains(tagId, System.StringComparer.OrdinalIgnoreCase);

    /// <summary>从详情 JSON 解析 PDF 直链（优先源文件，失败时退回其它镜像）。</summary>
    public static async Task<PdfLink> ResolvePdfAsync(
        string id, SmartEduConfig config, int timeoutSeconds, CancellationToken ct)
    {
        var url = string.Format(SmartEduCatalog.DetailUrlTemplate, id);
        var json = await GetStringAsync(url, timeoutSeconds, ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        var title = root.TryGetProperty("title", out var t) && t.ValueKind == JsonValueKind.String
            ? t.GetString() ?? id
            : id;

        if (!root.TryGetProperty("ti_items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("详情中没有可下载文件");
        }

        var candidates = items.EnumerateArray()
            .Where(i => i.ValueKind == JsonValueKind.Object)
            .Where(i => !IsFolder(i))
            .OrderByDescending(i => IsSource(i))
            .ThenBy(i => Format(i) == "pdf" ? 0 : 1)
            .ToList();

        foreach (var item in candidates)
        {
            var raw = GetString(item, "ti_storage");
            var path = raw.Contains("cs_path:${ref-path}", System.StringComparison.Ordinal)
                ? raw.Substring(raw.IndexOf("cs_path:${ref-path}", System.StringComparison.Ordinal) +
                                "cs_path:${ref-path}".Length)
                : string.Empty;
            if (path.Length == 0)
            {
                var storages = GetStringArray(item, "ti_storages");
                var first = storages.FirstOrDefault() ?? string.Empty;
                var idx = first.IndexOf("ykt.cbern.com.cn/", System.StringComparison.Ordinal);
                path = idx >= 0 ? first[(idx + "ykt.cbern.com.cn".Length)..] : string.Empty;
            }
            if (path.Length == 0)
            {
                continue;
            }

            return new PdfLink(NdrPrivate + path, title);
        }

        throw new InvalidOperationException("未找到 PDF 源文件地址");
    }

    /// <summary>下载 PDF：对当前 URL 现算 X-ND-AUTH 签名，校验文件头，失败时换镜像重试。</summary>
    public static async Task<(bool Ok, string Message, long Bytes)> DownloadAsync(
        string pdfUrl, string targetPath, SmartEduConfig config,
        int timeoutSeconds, CancellationToken ct)
    {
        if (!config.HasToken)
        {
            return (false, "请先设置登录凭据（Token）", 0);
        }

        var errors = new List<string>();
        foreach (var mirror in NdrMirrors)
        {
            var url = ReplaceHost(pdfUrl, mirror);
            var escaped = EscapeUrl(url);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, escaped);
                request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
                request.Headers.TryAddWithoutValidation("Authorization", "Bearer " + config.AccessToken);
                request.Headers.TryAddWithoutValidation("Origin", SmartEduCatalog.BaseUrl);
                request.Headers.TryAddWithoutValidation("Referer", SmartEduCatalog.BaseUrl + "/");
                request.Headers.TryAddWithoutValidation("X-ND-AUTH", BuildNdAuth(url, config));

                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(System.TimeSpan.FromSeconds(timeoutSeconds));
                using var response = await Http.SendAsync(request,
                    HttpCompletionOption.ResponseHeadersRead, linked.Token);

                if (!response.IsSuccessStatusCode)
                {
                    errors.Add($"{new System.Uri(mirror).Host}: HTTP {(int)response.StatusCode}");
                    continue;
                }

                var temp = targetPath + ".part";
                try
                {
                    await using (var src = await response.Content.ReadAsStreamAsync(linked.Token))
                    await using (var dst = new FileStream(temp, FileMode.Create, FileAccess.Write,
                                     FileShare.None, 81920, useAsync: true))
                    {
                        await src.CopyToAsync(dst, linked.Token);
                    }

                    var head = new byte[5];
                    await using (var check = new FileStream(temp, FileMode.Open, FileAccess.Read,
                                     FileShare.Read, 8, useAsync: true))
                    {
                        _ = await check.ReadAsync(head, linked.Token);
                    }

                    if (System.Text.Encoding.ASCII.GetString(head) != "%PDF-")
                    {
                        File.Delete(temp);
                        errors.Add($"{new System.Uri(mirror).Host}: 返回的不是 PDF");
                        continue;
                    }

                    File.Move(temp, targetPath, overwrite: true);
                    return (true, string.Empty, new FileInfo(targetPath).Length);
                }
                finally
                {
                    if (File.Exists(temp))
                    {
                        try { File.Delete(temp); } catch { /* 清理失败不影响主流程 */ }
                    }
                }
            }
            catch (System.OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                errors.Add($"{new System.Uri(mirror).Host}: 超时");
            }
            catch (System.Exception ex)
            {
                errors.Add($"{new System.Uri(mirror).Host}: {ex.Message}");
            }
        }

        return (false, string.Join("；", errors.Take(2)), 0);
    }

    /// <summary>按 UC SDK 规则为当前 URL 生成 X-ND-AUTH（每次请求现算）。</summary>
    public static string BuildNdAuth(string url, SmartEduConfig config)
    {
        var token = string.IsNullOrWhiteSpace(config.AccessToken) ? "0" : config.AccessToken;
        if (string.IsNullOrWhiteSpace(config.MacKey))
        {
            return $"MAC id=\"{token}\",nonce=\"0\",mac=\"0\"";
        }

        var nonceSuffix = string.Concat(Enumerable.Range(0, 8)
            .Select(_ => NonceAlphabet[System.Random.Shared.Next(1, NonceAlphabet.Length)]));
        var nonce = $"{System.DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + config.TokenDiff}:{nonceSuffix}";

        var uri = new System.Uri(url);
        var relative = System.Uri.UnescapeDataString(uri.AbsolutePath) +
                       (uri.Query.Length > 0 ? uri.Query : string.Empty);
        var text = $"{nonce}\nGET\n{relative}\n{uri.Host}\n";

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(config.MacKey));
        var mac = System.Convert.ToBase64String(hmac.ComputeHash(Encoding.UTF8.GetBytes(text)));
        return $"MAC id=\"{token}\",nonce=\"{nonce}\",mac=\"{mac}\"";
    }

    private static string ReplaceHost(string url, string host)
    {
        var uri = new System.Uri(url);
        return host + uri.PathAndQuery;
    }

    private static string EscapeUrl(string url)
    {
        var uri = new System.Uri(url);
        var escapedPath = string.Join("/", uri.AbsolutePath.Split('/').Select(Uri.EscapeDataString));
        return uri.GetLeftPart(System.UriPartial.Authority) + escapedPath +
               (uri.Query.Length > 0 ? uri.Query : string.Empty);
    }

    private static async Task<string> GetStringAsync(
        string url, int timeoutSeconds, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        linked.CancelAfter(System.TimeSpan.FromSeconds(timeoutSeconds));
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);
        request.Headers.TryAddWithoutValidation("X-ND-AUTH", "MAC id=\"0\",nonce=\"0\",mac=\"0\"");
        using var response = await Http.SendAsync(request, linked.Token);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(linked.Token);
    }

    private static async Task<string> GetWithRetryAsync(
        string url, int timeoutSeconds, CancellationToken ct)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return await GetStringAsync(url, timeoutSeconds, ct);
            }
            catch (HttpRequestException) when (attempt < 2)
            {
                await Task.Delay(600 * (attempt + 1), ct);
            }
            catch (System.IO.IOException) when (attempt < 2)
            {
                await Task.Delay(600 * (attempt + 1), ct);
            }
        }
    }

    private static Dictionary<string, (string Name, string Dimension)> CollectTagNames(
        JsonElement root)
    {
        var result = new Dictionary<string, (string, string)>(System.StringComparer.OrdinalIgnoreCase);
        Walk(root, (id, name, dim) =>
        {
            if (id.Length > 0 && name.Length > 0)
            {
                result[id] = (name, dim);
            }
        });
        return result;
    }

    private static void Walk(JsonElement node, Action<string, string, string> visit)
    {
        if (node.ValueKind == JsonValueKind.Array)
        {
            foreach (var child in node.EnumerateArray())
            {
                Walk(child, visit);
            }
            return;
        }
        if (node.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        if (node.TryGetProperty("tag_id", out var idEl))
        {
            visit(idEl.GetString() ?? string.Empty,
                node.TryGetProperty("tag_name", out var nEl) ? nEl.GetString() ?? string.Empty : string.Empty,
                node.TryGetProperty("tag_dimension_id", out var dEl) ? dEl.GetString() ?? string.Empty : string.Empty);
        }

        foreach (var key in new[] { "hierarchies", "children" })
        {
            if (node.TryGetProperty(key, out var child))
            {
                Walk(child, visit);
            }
        }
    }

    private static string GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString() ?? string.Empty
            : string.Empty;

    private static string[] GetStringArray(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array)
        {
            return [];
        }
        return v.EnumerateArray()
            .Where(x => x.ValueKind == JsonValueKind.String)
            .Select(x => x.GetString() ?? string.Empty)
            .Where(x => x.Length > 0)
            .ToArray();
    }

    private static string ReadLocalizedTitle(JsonElement value)
    {
        foreach (var key in new[] { "zh-CN", "en" })
        {
            if (value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String)
            {
                return v.GetString() ?? string.Empty;
            }
        }
        return string.Empty;
    }

    private static string GetProvider(JsonElement book)
    {
        if (book.TryGetProperty("provider_list", out var list) && list.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in list.EnumerateArray())
            {
                var name = GetString(p, "provider_name");
                if (name.Length > 0)
                {
                    return name;
                }
            }
        }
        return GetString(book, "provider");
    }

    private static bool IsFolder(JsonElement item) => Format(item) == "folder";

    private static bool IsSource(JsonElement item) =>
        GetString(item, "ti_file_flag") == "source" ||
        (item.TryGetProperty("ti_is_source_file", out var v) &&
         v.ValueKind == JsonValueKind.True);

    private static string Format(JsonElement item) => GetString(item, "ti_format");
}
