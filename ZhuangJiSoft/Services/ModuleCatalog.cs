using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using DeskApp.Models;

namespace DeskApp.Services;

/// <summary>
/// 模块目录：反射扫描本程序集里所有带 [ToolModule] 的 View 类型。
/// 这是「新增模块自动加入三处列表」的唯一数据源。
/// </summary>
public static class ModuleCatalog
{
    private static readonly Lazy<IReadOnlyList<ModuleInfo>> LazyAll = new(Scan);

    public static IReadOnlyList<ModuleInfo> All => LazyAll.Value;

    public static ModuleInfo? Find(string key) =>
        All.FirstOrDefault(m => string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));

    private static IReadOnlyList<ModuleInfo> Scan()
    {
        Type[] types;
        try
        {
            types = typeof(ModuleCatalog).Assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types.Where(t => t is not null).Cast<Type>().ToArray();
        }

        var list = new List<ModuleInfo>();
        foreach (var type in types)
        {
            ToolModuleAttribute? attr;
            try
            {
                attr = type.GetCustomAttribute<ToolModuleAttribute>(inherit: false);
            }
            catch
            {
                continue;
            }

            if (attr is null || !typeof(FrameworkElement).IsAssignableFrom(type))
            {
                continue;
            }

            list.Add(new ModuleInfo
            {
                Key = attr.Key,
                Name = attr.Name,
                Icon = attr.Icon,
                Description = attr.Description,
                Order = attr.Order,
                Tables = attr.Tables ?? Array.Empty<string>(),
                ViewType = type
            });
        }

        return list
            .OrderBy(m => m.Order)
            .ThenBy(m => m.Name, StringComparer.CurrentCulture)
            .ToArray();
    }
}
