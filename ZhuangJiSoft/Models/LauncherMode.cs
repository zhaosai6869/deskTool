namespace DeskApp.Models;

/// <summary>
/// 一键启动的模式（如「上班模式」「游戏模式」）。一个模式包含一组可一键启动的程序。
/// </summary>
public class LauncherMode
{
    public int Id { get; set; }

    /// <summary>模式名（用户自定义，如 上班模式）。</summary>
    public string Name { get; set; } = string.Empty;
}
