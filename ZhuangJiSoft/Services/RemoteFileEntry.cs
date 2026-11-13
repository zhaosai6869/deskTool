namespace DeskApp.Services;

/// <summary>远端文件条目（SFTP / FTP 统一表示）。</summary>
public sealed class RemoteFileEntry
{
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public bool IsDirectory { get; init; }
    public long Size { get; init; }
    public DateTime Modified { get; init; }
    public string Permissions { get; init; } = string.Empty;

    public string SizeText => IsDirectory ? "文件夹" : FormatSize(Size);

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return bytes + " B";
        if (bytes < 1024 * 1024) return (bytes / 1024.0).ToString("F1") + " KB";
        if (bytes < 1024L * 1024 * 1024) return (bytes / 1024.0 / 1024).ToString("F1") + " MB";
        return (bytes / 1024.0 / 1024 / 1024).ToString("F1") + " GB";
    }
}
