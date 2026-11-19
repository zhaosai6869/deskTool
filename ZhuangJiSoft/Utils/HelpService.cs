using System.IO;
using System.Reflection;
using System.Text;
using DeskApp.Models;
using DeskApp.Services;

namespace DeskApp.Utils;

/// <summary>
/// 使用说明服务：从程序集里嵌入的 Docs/Help/{模块key}.md 读取文档，
/// 并按 ModuleCatalog 的模块顺序组装列表。
///
/// 新增模块想带使用说明，只需两步：
///   1) 给 View 加 [ToolModule(...)]（原本就要做）；
///   2) 在 Docs/Help/ 下新增一个 &lt;模块key&gt;.md 文件（构建时自动嵌入）。
/// 列表、排序、缺省占位全部自动处理，无需改本类或设置窗口。
/// </summary>
public static class HelpService
{
    /// <summary>单篇文档：模块元信息 + Markdown 正文</summary>
    public sealed class Doc
    {
        public string Key { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public string Icon { get; init; } = string.Empty;
        public string Markdown { get; set; } = string.Empty;
        public bool HasDoc => !string.IsNullOrWhiteSpace(Markdown);
    }

    private const string ResourceDir = "Docs.Help.";
    private static IReadOnlyDictionary<string, string>? _cache;

    /// <summary>按模块顺序构建文档列表；缺文档的模块也会进列表，正文为空。</summary>
    public static IReadOnlyList<Doc> BuildList()
    {
        var docs = LoadEmbedded();
        return ModuleCatalog.All.Select(m =>
        {
            var key = m.Key;
            var text = docs.TryGetValue(key, out var md) ? md : string.Empty;
            return new Doc
            {
                Key = key,
                Name = m.Name,
                Icon = m.Icon,
                Markdown = text
            };
        }).ToArray();
    }

    /// <summary>读取程序集中所有 Docs.Help.*.md，按文件名（模块 key）建索引。</summary>
    private static IReadOnlyDictionary<string, string> LoadEmbedded()
    {
        if (_cache is not null)
        {
            return _cache;
        }

        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var asm = typeof(HelpService).Assembly;
            var marker = "." + ResourceDir;
            foreach (var name in asm.GetManifestResourceNames())
            {
                if (!name.Contains(marker, StringComparison.Ordinal) ||
                    !name.EndsWith(".md", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var idx = name.LastIndexOf(marker, StringComparison.Ordinal);
                var key = name.Substring(idx + marker.Length,
                    name.Length - idx - marker.Length - ".md".Length);

                using var stream = asm.GetManifestResourceStream(name);
                if (stream is null)
                {
                    continue;
                }
                using var reader = new StreamReader(stream, Encoding.UTF8,
                    detectEncodingFromByteOrderMarks: true);
                result[key] = reader.ReadToEnd();
            }
        }
        catch
        {
            // 读取失败时返回空，界面会显示「该模块暂无使用说明」，不影响程序启动
        }

        _cache = result;
        return result;
    }
}
