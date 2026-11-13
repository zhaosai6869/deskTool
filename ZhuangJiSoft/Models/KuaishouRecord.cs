using System.Collections.Generic;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeskApp.Models;

/// <summary>
/// 快手下载记录。SourceUrl 存归一化后的作品链接（https://www.kuaishou.com/short-video/&lt;id&gt;），
/// 作为去重键；与已下载文件大小一致时跳过重复下载。
/// 状态机：排队中 → 解析中 → 下载中 → 已下载 / 已跳过 / 失败。
/// </summary>
public class KuaishouRecord : INotifyPropertyChanged
{
    private string _status = "排队中";
    private long _bytes;
    private string _title = string.Empty;

    public int Id { get; set; }

    /// <summary>归一化后的作品链接（或解析前的原始输入），作为去重键。</summary>
    public string SourceUrl { get; set; } = string.Empty;

    public string Title
    {
        get => _title;
        set
        {
            if (_title != value)
            {
                _title = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Title)));
            }
        }
    }

    public string Author { get; set; } = string.Empty;

    /// <summary>成品文件名（不含目录）。</summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>已下载字节数（完成时等于文件大小）。</summary>
    public long Bytes
    {
        get => _bytes;
        set
        {
            if (_bytes != value)
            {
                _bytes = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Bytes)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(BytesText)));
            }
        }
    }

    /// <summary>排队中 / 解析中 / 下载中 / 已下载 / 已跳过 / 失败。</summary>
    public string Status
    {
        get => _status;
        set
        {
            if (_status != value)
            {
                _status = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Status)));
            }
        }
    }

    public string Error { get; set; } = string.Empty;

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? FinishedAt { get; set; }

    [NotMapped]
    public string BytesText => Bytes <= 0
        ? string.Empty
        : Bytes < 1024 * 1024 ? $"{Bytes / 1024.0:F0} KB"
        : $"{Bytes / 1024.0 / 1024.0:F1} MB";

    [NotMapped]
    public string TimeText => FinishedAt?.ToString("MM-dd HH:mm") ?? CreatedAt.ToString("MM-dd HH:mm");

    public event PropertyChangedEventHandler? PropertyChanged;
}
