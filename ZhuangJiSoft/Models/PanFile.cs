using CommunityToolkit.Mvvm.ComponentModel;

namespace DeskApp.Models;

/// <summary>网盘中的一个文件（解析分享链接后得到）。</summary>
public sealed partial class PanFile : ObservableObject
{
    public string Name { get; init; } = string.Empty;
    public string FileId { get; init; } = string.Empty;

    /// <summary>文件大小（字节），未知为 0。</summary>
    public long Size { get; init; }

    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string _status = "待下载";
    [ObservableProperty] private double _progress;

    public string SizeText => Size <= 0
        ? "—"
        : Size >= 1024L * 1024 * 1024
            ? $"{Size / 1024.0 / 1024 / 1024:F2} GB"
            : Size >= 1024L * 1024
                ? $"{Size / 1024.0 / 1024:F1} MB"
                : $"{Size / 1024.0:F0} KB";
}

/// <summary>单个下载任务（UI 层进度模型）。</summary>
public sealed partial class PanDownloadItem : ObservableObject
{
    public string FileName { get; init; } = string.Empty;
    [ObservableProperty] private string _status = "排队中";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _speed = string.Empty;
}
