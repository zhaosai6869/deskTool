using System.IO;
using SharpCompress.Archives;
using SharpCompress.Readers;

namespace DeskApp.Utils;

/// <summary>压缩包中提取 Office 文件的结果。</summary>
public sealed class FirstPaperExtractResult
{
    public List<string> Files { get; } = new();

    public long TotalBytes { get; set; }

    public string? Error { get; set; }

    public bool Success => Error is null && Files.Count > 0;
}

/// <summary>
/// 第一试卷下载后的自动解压：从 rar/zip/7z 中只提取 Office 文件（doc/docx/xls/xlsx/ppt/pptx）。
/// 只做解压，不删源文件；是否删除压缩包由调用方在确认成功后决定，避免解压失败时误删。
/// </summary>
public static class FirstPaperExtractor
{
    private static readonly HashSet<string> ArchiveExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".rar", ".zip", ".7z"
    };

    private static readonly HashSet<string> OfficeExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".doc", ".docx", ".xls", ".xlsx", ".ppt", ".pptx"
    };

    public static bool IsArchive(string path)
    {
        var ext = Path.GetExtension(path ?? string.Empty);
        return ArchiveExtensions.Contains(ext);
    }

    public static async Task<FirstPaperExtractResult> ExtractOfficeFilesAsync(
        string archivePath, string downloadDir, string cacheDir,
        Action<string>? progress, CancellationToken ct)
    {
        var result = new FirstPaperExtractResult();
        var workDir = Path.Combine(cacheDir, $"extract-{Guid.NewGuid():N}");
        Directory.CreateDirectory(workDir);

        try
        {
            using var archive = ArchiveFactory.OpenArchive(archivePath, new ReaderOptions());
            var officeEntries = archive.Entries
                .Where(e => !e.IsDirectory && OfficeExtensions.Contains(Path.GetExtension(e.Key ?? string.Empty)))
                .ToList();

            for (var i = 0; i < officeEntries.Count; i++)
            {
                var entry = officeEntries[i];
                progress?.Invoke($"解压中 {i + 1}/{officeEntries.Count}");

                var safeName = SafeEntryName(entry.Key ?? string.Empty);
                var target = UniqueTarget(downloadDir, safeName);
                var temp = Path.Combine(workDir, $"{Guid.NewGuid():N}{Path.GetExtension(safeName)}");

                await using (var input = entry.OpenEntryStream())
                await using (var output = File.Create(temp))
                {
                    await input.CopyToAsync(output, 128 * 1024, ct);
                }

                var info = new FileInfo(temp);
                if (info.Length == 0)
                {
                    throw new IOException($"压缩包内文件为空：{safeName}");
                }

                File.Move(temp, target);
                result.Files.Add(target);
                result.TotalBytes += info.Length;
            }

            if (result.Files.Count == 0)
            {
                result.Error = "压缩包内未找到 Office 文件（doc/docx/xls/xlsx/ppt/pptx）";
            }

            return result;
        }
        catch (Exception ex)
        {
            result.Error = "解压失败：" + ex.Message;
            return result;
        }
        finally
        {
            try { Directory.Delete(workDir, recursive: true); } catch { }
        }
    }

    /// <summary>取条目文件名并清理 Windows 非法字符，防止压缩包内路径穿越。</summary>
    private static string SafeEntryName(string key)
    {
        var normalized = key.Replace('\\', '/');
        var name = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        name = Path.GetFileName(name);
        foreach (var c in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(c, '_');
        }

        name = name.Trim(' ', '.');
        if (string.IsNullOrWhiteSpace(name))
        {
            name = "paper.docx";
        }

        if (name.Length > 160)
        {
            var ext = Path.GetExtension(name);
            name = name[..(160 - ext.Length)].Trim('_', '.', ' ') + ext;
        }

        return name;
    }

    private static string UniqueTarget(string dir, string fileName)
    {
        var target = Path.Combine(dir, fileName);
        if (!File.Exists(target))
        {
            return target;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }
}
