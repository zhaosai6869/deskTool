namespace DeskApp.Models;

/// <summary>
/// 首页上的一个工具入口。列表由 ModuleCatalog + ModuleSettings 自动生成，
/// 这里只是给卡片模板用的展示模型；末位的「＋」卡片也复用它。
/// </summary>
public class ToolEntry
{
    /// <summary>唯一标识，用于区分点击哪个工具</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>卡片显示名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>卡片图标（emoji 或文字）</summary>
    public string Icon { get; set; } = "🛠";

    /// <summary>卡片上的一句话简介</summary>
    public string Description { get; set; } = string.Empty;
}
