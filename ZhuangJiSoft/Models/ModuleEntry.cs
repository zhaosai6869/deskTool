using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DeskApp.Models;

/// <summary>
/// 设置页里的可勾选模块条目（模块管理 / 数据管理共用），勾选状态可双向绑定到 UI。
/// CanMoveUp / CanMoveDown 控制模块管理里 ↑↓ 按钮的可用状态。
/// </summary>
public class ModuleEntry : INotifyPropertyChanged
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = "🛠";
    public string Description { get; set; } = string.Empty;
    public string[] Tables { get; set; } = Array.Empty<string>();

    private bool _isChecked;
    private bool _canMoveUp;
    private bool _canMoveDown;

    public bool IsChecked
    {
        get => _isChecked;
        set => Set(ref _isChecked, value);
    }

    public bool CanMoveUp
    {
        get => _canMoveUp;
        set => Set(ref _canMoveUp, value);
    }

    public bool CanMoveDown
    {
        get => _canMoveDown;
        set => Set(ref _canMoveDown, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value)
        {
            return;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
