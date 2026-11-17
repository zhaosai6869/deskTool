using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;

namespace DeskApp.Models;

/// <summary>
/// 第一试卷题库条目。一条记录对应站点上的一个试卷详情页；
/// 题库扫描只抓列表信息，下载时才请求详情页解析真实文件地址（避免一次抓几千个详情页）。
/// </summary>
public class FirstPaperRecord : INotifyPropertyChanged
{
    private bool _isSelected;
    private string _status = string.Empty;
    private string _error = string.Empty;
    private long _bytes;

    public int Id { get; set; }

    /// <summary>学科 Key，如 sjyw。</summary>
    public string SubjectKey { get; set; } = string.Empty;

    /// <summary>年级 Key，如 1 / 7 / zk / g1 / gk。</summary>
    public string GradeKey { get; set; } = string.Empty;

    /// <summary>详情页地址，作为题库去重键。</summary>
    public string DetailUrl { get; set; } = string.Empty;

    public string Title { get; set; } = string.Empty;

    public string Version { get; set; } = string.Empty;

    public string FileType { get; set; } = string.Empty;

    public string SizeText { get; set; } = string.Empty;

    public string UploadDate { get; set; } = string.Empty;

    /// <summary>解析后的真实下载地址；为空表示尚未解析或曾解析失败。</summary>
    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>站点已下架的条目保留在本地但不在题库列表展示。</summary>
    public bool Available { get; set; } = true;

    public string FileName { get; set; } = string.Empty;

    public long Bytes
    {
        get => _bytes;
        set
        {
            if (_bytes != value)
            {
                _bytes = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Bytes)));
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

    public string Error
    {
        get => _error;
        set
        {
            if (_error != value)
            {
                _error = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Error)));
            }
        }
    }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public DateTime? FinishedAt { get; set; }

    [NotMapped]
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            }
        }
    }

    [NotMapped]
    public string TimeText => string.IsNullOrWhiteSpace(UploadDate)
        ? string.Empty
        : UploadDate;

    public event PropertyChangedEventHandler? PropertyChanged;
}
