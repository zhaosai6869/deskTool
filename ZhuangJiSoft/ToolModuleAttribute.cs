using System;

namespace DeskApp;

/// <summary>
/// 模块声明特性：标在模块的 View（UserControl）类上。
///
/// 以后新增模块只需两步：
///   1) 新建 XxxView.xaml / .xaml.cs（构造函数里自行设置 DataContext）；
///   2) 在 View 类上方加一行 [ToolModule(...)]。
/// 首页卡片、设置里的「模块管理」、「数据管理」会自动出现该模块，无需再改任何列表。
///
/// ViewModel 若有无参 public void Load()，每次进入页面时会被自动调用（可选）。
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class ToolModuleAttribute : Attribute
{
    /// <param name="key">唯一标识，如 "bilibili"</param>
    /// <param name="name">主界面/设置里显示的名称</param>
    /// <param name="icon">卡片图标（emoji 或文字）</param>
    /// <param name="description">卡片上的一句话简介</param>
    public ToolModuleAttribute(string key, string name, string icon, string description)
    {
        Key = key;
        Name = name;
        Icon = icon;
        Description = description;
    }

    public string Key { get; }
    public string Name { get; }
    public string Icon { get; }
    public string Description { get; }

    /// <summary>排序权重，小的在前；默认 100</summary>
    public int Order { get; set; } = 100;

    /// <summary>该模块占用的 SQLite 表名（与 AppDbContext 的 DbSet 属性名一致），供「数据管理」清空用</summary>
    public string[] Tables { get; set; } = Array.Empty<string>();
}
