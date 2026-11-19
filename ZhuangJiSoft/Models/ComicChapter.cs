using CommunityToolkit.Mvvm.ComponentModel;

namespace DeskApp.Models;

/// <summary>漫画的一个章节（话）。Url 为章节详情页地址。</summary>
public sealed partial class ComicChapter : ObservableObject
{
    public string Title { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;

    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string _status = string.Empty;
    [ObservableProperty] private int _imageCount;

    public override string ToString() => Title;
}
