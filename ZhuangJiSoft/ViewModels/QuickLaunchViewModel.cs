using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskApp.Data;
using DeskApp.Models;
using DeskApp.Utils;
using Microsoft.EntityFrameworkCore;

namespace DeskApp.ViewModels;

/// <summary>
/// 一键启动模块：多模式（如 上班模式/游戏模式），每模式配置一组系统应用，
/// 单击「一键启动」批量拉起。应用清单从注册表卸载信息 + 开始菜单快捷方式枚举。
/// </summary>
public partial class QuickLaunchViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<LauncherMode> _modes = new();

    [ObservableProperty]
    private LauncherMode? _selectedMode;

    [ObservableProperty]
    private ObservableCollection<LauncherItem> _items = new();

    [ObservableProperty]
    private ObservableCollection<AppEntry> _appOptions = new();

    [ObservableProperty]
    private AppEntry? _selectedApp;

    [ObservableProperty]
    private bool _busy;

    /// <summary>选中模式变化时重载该模式的启动项。</summary>
    partial void OnSelectedModeChanged(LauncherMode? value)
    {
        LoadItems();
    }

    public void Load()
    {
        using var db = new AppDbContext();
        Modes = new ObservableCollection<LauncherMode>(
            db.LauncherModes.OrderBy(m => m.Id).ToList());
        SelectedMode = Modes.FirstOrDefault();
        LoadItems();

        // 后台枚举系统应用，避免阻塞 UI
        Busy = true;
        _ = Task.Run(() =>
        {
            var apps = AppDiscovery.Discover();
            Application.Current?.Dispatcher.Invoke(() =>
            {
                AppOptions = new ObservableCollection<AppEntry>(apps);
                Busy = false;
            });
        });
    }

    /// <summary>重载当前模式的启动项列表。</summary>
    private void LoadItems()
    {
        using var db = new AppDbContext();
        Items = SelectedMode is null
            ? new ObservableCollection<LauncherItem>()
            : new ObservableCollection<LauncherItem>(
                db.LauncherItems.Where(i => i.ModeId == SelectedMode.Id).OrderBy(i => i.Id).ToList());
    }

    /// <summary>新建模式（弹输入框），成功后选中新模式。</summary>
    public LauncherMode? AddMode(Window? owner)
    {
        var name = PromptInput(owner, "新建启动模式", "模式名称（如：上班模式）", string.Empty);
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var mode = new LauncherMode { Name = name.Trim() };
        using (var db = new AppDbContext())
        {
            db.LauncherModes.Add(mode);
            db.SaveChanges();
        }

        Modes.Add(mode);
        SelectedMode = mode;
        return mode;
    }

    /// <summary>重命名当前模式。</summary>
    public void RenameMode(Window? owner)
    {
        if (SelectedMode is null)
        {
            PromptDialog.Show(owner, "请先选择一个模式（或新建）。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = PromptInput(owner, "重命名模式", "模式名称", SelectedMode.Name);
        if (string.IsNullOrWhiteSpace(name) || name == SelectedMode.Name)
        {
            return;
        }

        SelectedMode.Name = name.Trim();
        using var db = new AppDbContext();
        var entity = db.LauncherModes.Find(SelectedMode.Id);
        if (entity is not null)
        {
            entity.Name = SelectedMode.Name;
            db.SaveChanges();
        }

        // 触发 ComboBox 显示刷新
        var current = SelectedMode;
        Modes = new ObservableCollection<LauncherMode>(Modes);
        SelectedMode = current;
    }

    /// <summary>删除当前模式（含其启动项）。</summary>
    public void DeleteMode(Window? owner)
    {
        if (SelectedMode is null)
        {
            PromptDialog.Show(owner, "请先选择一个模式。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (PromptDialog.Show(owner, $"删除模式「{SelectedMode.Name}」及其全部启动项？",
                "确认删除", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
        {
            return;
        }

        var id = SelectedMode.Id;
        using (var db = new AppDbContext())
        {
            db.LauncherItems.RemoveRange(db.LauncherItems.Where(i => i.ModeId == id));
            var mode = db.LauncherModes.Find(id);
            if (mode is not null)
            {
                db.LauncherModes.Remove(mode);
            }

            db.SaveChanges();
        }

        Modes.Remove(SelectedMode);
        SelectedMode = Modes.FirstOrDefault();
    }

    /// <summary>从系统应用下拉里把选中应用加入当前模式。</summary>
    public void AddSelectedApp(Window? owner)
    {
        if (SelectedMode is null)
        {
            PromptDialog.Show(owner, "请先选择或新建一个模式。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (SelectedApp is null)
        {
            PromptDialog.Show(owner, "请先在下拉框里选择一个程序（或点「浏览」手动添加）。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        AddItem(SelectedMode, SelectedApp.Name, SelectedApp.Target, SelectedApp.Arguments);
    }

    /// <summary>浏览手动添加（绿色软件等枚举不到的程序）。</summary>
    public void AddBrowsedApp(Window? owner, string path)
    {
        if (SelectedMode is null)
        {
            PromptDialog.Show(owner, "请先选择或新建一个模式。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is not ".exe" and not ".lnk")
        {
            PromptDialog.Show(owner, "只支持 .exe 或 .lnk 快捷方式。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var name = Path.GetFileNameWithoutExtension(path);
        var target = path;
        var arguments = string.Empty;
        if (ext == ".lnk" && ShortcutResolver.Resolve(path) is { } resolved)
        {
            target = resolved.Target;
            arguments = resolved.Arguments;
        }

        AddItem(SelectedMode, name, target, arguments);
    }

    private void AddItem(LauncherMode mode, string name, string target, string arguments)
    {
        // 同模式同目标不重复添加
        if (Items.Any(i => string.Equals(i.Target, target, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var item = new LauncherItem
        {
            ModeId = mode.Id,
            Name = name,
            Target = target,
            Arguments = arguments
        };
        using var db = new AppDbContext();
        db.LauncherItems.Add(item);
        db.SaveChanges();
        Items.Add(item);
    }

    /// <summary>移除一条启动项。</summary>
    public void RemoveItem(LauncherItem? item)
    {
        if (item is null)
        {
            return;
        }

        using var db = new AppDbContext();
        var entity = db.LauncherItems.Find(item.Id);
        if (entity is not null)
        {
            db.LauncherItems.Remove(entity);
            db.SaveChanges();
        }

        Items.Remove(item);
    }

    /// <summary>启动当前模式下的全部程序。返回 (成功数, 失败数, 失败名称列表)。</summary>
    public (int Ok, int Failed, string[] FailedNames) LaunchAll(Window? owner)
    {
        if (SelectedMode is null || Items.Count == 0)
        {
            PromptDialog.Show(owner, "当前模式为空：请先从下拉框添加程序。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return (0, 0, Array.Empty<string>());
        }

        var ok = 0;
        var failed = new System.Collections.Generic.List<string>();
        foreach (var item in Items)
        {
            if (LaunchOne(item))
            {
                ok++;
            }
            else
            {
                failed.Add(item.Name);
            }
        }

        return (ok, failed.Count, failed.ToArray());
    }

    /// <summary>启动单个程序。返回是否成功。</summary>
    public bool LaunchOne(LauncherItem item)
    {
        try
        {
            // UseShellExecute 走系统关联启动（.exe / .lnk 均可），不直接拉起 explorer.exe
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = item.Target,
                Arguments = item.Arguments,
                UseShellExecute = true
            });
            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    private static string? PromptInput(Window? owner, string title, string label, string defaultValue)
    {
        var dialog = new TextInputDialog(title, label, defaultValue)
        {
            Owner = owner
        };
        return dialog.ShowDialog() == true ? dialog.Value : null;
    }
}
