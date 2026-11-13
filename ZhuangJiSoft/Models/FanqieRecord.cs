using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeskApp.Models;

/// <summary>
/// 番茄小说下载记录。SourceUrl 存归一化的目录页链接（https://fanqienovel.com/page/&lt;bookId&gt;），作为去重键。
/// 状态机：排队中 → 解析中 → 下载中（N/总章）→ 已下载 / 已跳过 / 失败。
/// </summary>
public class FanqieRecord : INotifyPropertyChanged
{
    private string _status = "排队中";
    private long _bytes;
    private string _title = string.Empty;
    private int _chapterDone;
    private int _chapterTotal;

    public int Id { get; set; }

    /// <summary>归一化目录页链接（或解析前的原始输入），去重键。</summary>
    public string SourceUrl { get; set; } = string.Empty;

    /// <summary>书名。</summary>
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

    /// <summary>成品字节数（写盘完成后回填）。</summary>
    public long Bytes
    {
        get => _bytes;
        set
        {
            if (_bytes != value)
            {
                _bytes = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Bytes)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SizeText)));
            }
        }
    }

    /// <summary>已完成章节数。</summary>
    public int ChapterDone
    {
        get => _chapterDone;
        set
        {
            if (_chapterDone != value)
            {
                _chapterDone = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChapterDone)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressText)));
            }
        }
    }

    /// <summary>总章节数。</summary>
    public int ChapterTotal
    {
        get => _chapterTotal;
        set
        {
            if (_chapterTotal != value)
            {
                _chapterTotal = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ChapterTotal)));
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ProgressText)));
            }
        }
    }

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
    public string SizeText => Bytes <= 0
        ? string.Empty
        : Bytes < 1024 * 1024 ? $"{Bytes / 1024.0:F0} KB"
        : $"{Bytes / 1024.0 / 1024.0:F2} MB";

    [NotMapped]
    public string ProgressText => ChapterTotal <= 0
        ? Status
        : Status.StartsWith("下载中", StringComparison.Ordinal)
            ? $"{Status} {ChapterDone}/{ChapterTotal}"
            : $"{Status}（共{ChapterTotal}章）";

    [NotMapped]
    public string TimeText => FinishedAt?.ToString("MM-dd HH:mm") ?? CreatedAt.ToString("MM-dd HH:mm");

    public event PropertyChangedEventHandler? PropertyChanged;
}
