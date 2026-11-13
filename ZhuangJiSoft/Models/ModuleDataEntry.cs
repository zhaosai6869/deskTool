namespace DeskApp.Models;

/// <summary>
/// 数据管理页中的一个模块条目：名称 + 是否勾选 + 清空该模块数据的动作。
/// </summary>
public class ModuleDataEntry
{
    public string Key { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public bool IsChecked { get; set; }

    /// <summary>清空该模块数据的逻辑（由各模块自行提供）</summary>
    public System.Action Clear { get; set; } = () => { };
}
