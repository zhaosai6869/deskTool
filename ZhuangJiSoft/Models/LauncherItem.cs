namespace DeskApp.Models;

/// <summary>
/// 模式内的一条启动项：程序显示名、目标（exe 或 .lnk 路径）与可选启动参数。
/// </summary>
public class LauncherItem
{
    public int Id { get; set; }

    /// <summary>所属模式 Id。</summary>
    public int ModeId { get; set; }

    /// <summary>程序显示名（下拉里选的名称或文件名去扩展名）。</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>启动目标：exe / .lnk 完整路径。</summary>
    public string Target { get; set; } = string.Empty;

    /// <summary>可选启动参数（快捷方式里带的参数）。</summary>
    public string Arguments { get; set; } = string.Empty;
}
