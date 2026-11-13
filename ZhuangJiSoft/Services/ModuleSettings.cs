using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DeskApp.Models;

namespace DeskApp.Services;

/// <summary>
/// 模块启用状态与显示顺序，持久化到 data\modules.json。
/// 规则：首次运行把当时发现的所有模块设为「启用」（保持现有首页不变）；
/// 之后新出现的模块默认「未启用」，并排到顺序末尾，需在设置 → 模块管理里勾选才会上首页。
/// 用户用 ↑↓ 调整的顺序保存在 JSON 的 order 数组里，首页卡片按此顺序显示。
/// </summary>
public sealed class ModuleSettings
{
    private static readonly string FilePath =
        Path.Combine(AppContext.BaseDirectory, "data", "modules.json");

    public static ModuleSettings Instance { get; } = new();

    private readonly object _gate = new();
    private Dictionary<string, bool> _enabled = new(StringComparer.OrdinalIgnoreCase);
    private List<string> _order = new();
    private bool _fileExisted;
    private bool _orderLoaded;

    private ModuleSettings()
    {
        Load();
    }

    public bool IsEnabled(string key)
    {
        lock (_gate)
        {
            return _enabled.TryGetValue(key, out var value) && value;
        }
    }

    public void SetEnabled(string key, bool value)
    {
        lock (_gate)
        {
            _enabled[key] = value;
            Save();
        }
    }

    /// <summary>把目录里的模块同步进配置：老模块保留原状态/原顺序，新模块默认不启用并排到末尾。</summary>
    public void EnsureDefaults(IEnumerable<ModuleInfo> modules)
    {
        var all = modules.ToList();
        lock (_gate)
        {
            var firstRun = !_fileExisted && _enabled.Count == 0;
            var changed = false;

            foreach (var module in all)
            {
                if (_enabled.ContainsKey(module.Key))
                {
                    continue;
                }

                _enabled[module.Key] = firstRun;
                changed = true;
            }

            var valid = all.Select(m => m.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);

            // 已被移除的模块顺手清掉，避免配置无限膨胀
            foreach (var stale in _enabled.Keys.Where(k => !valid.Contains(k)).ToList())
            {
                _enabled.Remove(stale);
                changed = true;
            }

            foreach (var stale in _order.Where(k => !valid.Contains(k)).ToList())
            {
                _order.Remove(stale);
                changed = true;
            }

            if (!_orderLoaded)
            {
                // 老配置文件（只有 enabled）或首次运行：用特性里的默认顺序
                _order = all.Select(m => m.Key).ToList();
                _orderLoaded = true;
                changed = true;
            }
            else
            {
                foreach (var module in all)
                {
                    if (_order.Contains(module.Key, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    _order.Add(module.Key);
                    changed = true;
                }
            }

            if (changed || firstRun)
            {
                Save();
            }
        }
    }

    /// <summary>按保存的顺序返回模块目录；配置里缺失的模块按默认顺序补到末尾。</summary>
    public IReadOnlyList<ModuleInfo> GetOrderedModules()
    {
        var all = ModuleCatalog.All;
        List<string> order;
        lock (_gate)
        {
            order = _order.ToList();
        }

        var result = new List<ModuleInfo>();
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in order)
        {
            var module = all.FirstOrDefault(m =>
                string.Equals(m.Key, key, StringComparison.OrdinalIgnoreCase));
            if (module is not null && used.Add(module.Key))
            {
                result.Add(module);
            }
        }

        foreach (var module in all)
        {
            if (used.Add(module.Key))
            {
                result.Add(module);
            }
        }

        return result;
    }

    /// <summary>保存用户调整后的顺序</summary>
    public void SetOrder(IEnumerable<string> keys)
    {
        lock (_gate)
        {
            _order = keys
                .Where(k => !string.IsNullOrWhiteSpace(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
            _orderLoaded = true;
            Save();
        }
    }

    /// <summary>恢复 [ToolModule] 特性里声明的默认顺序</summary>
    public void ResetOrder() => SetOrder(ModuleCatalog.All.Select(m => m.Key));

    private void Load()
    {
        try
        {
            if (!File.Exists(FilePath))
            {
                return;
            }

            _fileExisted = true;
            var json = File.ReadAllText(FilePath);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("enabled", out var enabled) &&
                enabled.ValueKind == JsonValueKind.Object)
            {
                foreach (var item in enabled.EnumerateObject())
                {
                    _enabled[item.Name] = item.Value.ValueKind == JsonValueKind.True;
                }
            }

            if (doc.RootElement.TryGetProperty("order", out var order) &&
                order.ValueKind == JsonValueKind.Array)
            {
                _order.Clear();
                foreach (var item in order.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } key)
                    {
                        _order.Add(key);
                    }
                }

                _orderLoaded = true;
            }
        }
        catch
        {
            // 配置损毁时退化为「全部未启用 + 默认顺序」，用户重新勾选即可
            _enabled.Clear();
            _order.Clear();
            _orderLoaded = false;
        }
    }

    private void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(FilePath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(new
            {
                enabled = new Dictionary<string, bool>(_enabled, StringComparer.OrdinalIgnoreCase),
                order = _order.ToArray()
            }, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
            _fileExisted = true;
        }
        catch
        {
            // 写不进去不影响本次会话使用
        }
    }
}
