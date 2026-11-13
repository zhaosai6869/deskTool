namespace DeskApp.Models;

/// <summary>
/// 九宫格中的一个工具入口。后续新增工具只需往 LauncherWindow 的 Tools 列表加一项。
/// </summary>
public class ToolEntry
{
    /// <summary>唯一标识，用于区分点击哪个工具</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>格子显示名称</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>占位图标（emoji 或文字）</summary>
    public string Icon { get; set; } = "🛠";
}
