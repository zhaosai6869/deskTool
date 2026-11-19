namespace DeskApp.Models;

/// <summary>
/// 漫画站点入口（名称 + 首页网址 + Provider 标识）。
/// 列表由内置 Provider 固定生成，不提供增删改。
/// </summary>
public sealed class ComicSite
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;

    public override string ToString() => Name;
}
