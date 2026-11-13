using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DeskApp.Models;

namespace DeskApp.Services;

/// <summary>
/// 模块启用状态，持久化到 data\modules.json。
/// 规则：首次运行把当时发现的所有模块设为「启用」（保持现有首页不变）；
/// 之后新出现的模块默认「未启用」，需在设置 → 模块管理里勾选才会上首页。
/// </summary>
public sealed class ModuleSettings
{
    private static readonly string FilePath =
        Path.Combine(AppContext.BaseDirectory, "data", "modules.json");

    public static ModuleSettings Instance { get; } = new();

    private readonly object _gate = new();
    private Dictionary<string, bool> _enabled = new(StringComparer.OrdinalIgnoreCase);
    private bool _fileExisted;

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

    /// <summary>把目录里的模块同步进配置：老模块保留原状态，新模块默认不启用。</summary>
    public void EnsureDefaults(IEnumerable<ModuleInfo> modules)
    {
        lock (_gate)
        {
            var firstRun = !_fileExisted && _enabled.Count == 0;
            var changed = false;

            foreach (var module in modules)
            {
                if (_enabled.ContainsKey(module.Key))
                {
                    continue;
                }

                _enabled[module.Key] = firstRun;
                changed = true;
            }

            // 已被移除的模块顺手清掉，避免配置无限膨胀
            var valid = modules.Select(m => m.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var stale in _enabled.Keys.Where(k => !valid.Contains(k)).ToList())
            {
                _enabled.Remove(stale);
                changed = true;
            }

            if (changed || firstRun)
            {
                Save();
            }
        }
    }

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
        }
        catch
        {
            // 配置损毁时退化为「全部未启用」，用户重新勾选即可
            _enabled.Clear();
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

            var payload = new Dictionary<string, bool>(_enabled, StringComparer.OrdinalIgnoreCase);
            var json = JsonSerializer.Serialize(new { enabled = payload },
                new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(FilePath, json);
            _fileExisted = true;
        }
        catch
        {
            // 写不进去不影响本次会话使用
        }
    }
}
