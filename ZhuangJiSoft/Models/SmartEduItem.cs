using CommunityToolkit.Mvvm.ComponentModel;

namespace DeskApp.Models;

/// <summary>
/// 智慧教育平台教材列表中的一本教材。纯界面模型：不入库，
/// 每次筛选按条件联网拉取教材清单后在本地按 tag 路径过滤。
/// </summary>
public partial class SmartEduItem : ObservableObject
{
    public string Id { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string Stage { get; init; } = string.Empty;

    public string Subject { get; init; } = string.Empty;

    public string Edition { get; init; } = string.Empty;

    public string Grade { get; init; } = string.Empty;

    public string Provider { get; init; } = string.Empty;

    /// <summary>平台 tag 路径（/ 分隔的 tag_id），仅用于本地筛选，不在界面显示。</summary>
    public string TagPath { get; init; } = string.Empty;

    /// <summary>详情页地址（浏览器打开核对用）。</summary>
    public string DetailUrl { get; init; } = string.Empty;

    [ObservableProperty] private bool _isSelected;

    [ObservableProperty] private string _status = string.Empty;

    [ObservableProperty] private string _error = string.Empty;

    [ObservableProperty] private string _fileName = string.Empty;
}
