using System;

namespace DeskApp.Models;

/// <summary>由 [ToolModule] 特性反射得到的模块元数据（只读）。</summary>
public sealed class ModuleInfo
{
    public string Key { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Icon { get; init; } = "🛠";
    public string Description { get; init; } = string.Empty;
    public int Order { get; init; }
    public string[] Tables { get; init; } = Array.Empty<string>();
    public Type ViewType { get; init; } = typeof(object);
}
