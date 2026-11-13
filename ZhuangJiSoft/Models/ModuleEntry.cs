using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DeskApp.Models;

/// <summary>
/// 设置页里的可勾选模块条目（模块管理 / 数据管理共用），勾选状态可双向绑定到 UI。
/// </summary>
public class ModuleEntry : INotifyPropertyChanged
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = "🛠";
    public string Description { get; set; } = string.Empty;
    public string[] Tables { get; set; } = Array.Empty<string>();

    private bool _isChecked;

    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
