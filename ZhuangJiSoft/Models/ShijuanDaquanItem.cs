using CommunityToolkit.Mvvm.ComponentModel;

namespace DeskApp.Models;

/// <summary>
/// 试卷大全列表中的一条试卷。纯界面模型：不入库、不做全站扫描，
/// 数据随筛选/翻页实时从站点抓取。
/// </summary>
public partial class ShijuanDaquanItem : ObservableObject
{
    public int Id { get; init; }

    public string Title { get; init; } = string.Empty;

    public string Version { get; init; } = string.Empty;

    public string Year { get; init; } = string.Empty;

    public string PaperType { get; init; } = string.Empty;

    public string Grade { get; init; } = string.Empty;

    public string Rating { get; init; } = string.Empty;

    public string DetailUrl { get; init; } = string.Empty;

    public string DownloadUrl { get; init; } = string.Empty;

    [ObservableProperty] private bool _isSelected;

    [ObservableProperty] private string _status = string.Empty;

    [ObservableProperty] private string _error = string.Empty;

    [ObservableProperty] private string _fileName = string.Empty;
}
