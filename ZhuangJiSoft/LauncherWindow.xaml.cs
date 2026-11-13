using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskApp.Models;
using DeskApp.Services;
using Wpf.Ui.Controls;

namespace DeskApp;

/// <summary>
/// 桌面工具外壳：FluentWindow + 自定义标题栏（标题/返回/设置/最小化/关闭）+ 各模块二级页面。
///
/// 页面不再逐个手写导航方法：NavigateModule(key) 按 ModuleCatalog 里的 ViewType
/// 反射创建并缓存页面，ViewModel 若有无参 Load() 会自动调用。
/// 新增模块只要写 View + [ToolModule]，这里零改动。
/// </summary>
public partial class LauncherWindow : FluentWindow
{
    private readonly HomeView _home = new();
    private readonly Dictionary<string, UserControl> _pages = new(StringComparer.OrdinalIgnoreCase);

    public LauncherWindow()
    {
        InitializeComponent();
        // 跟随系统主题切换（浅色/深色）
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        NavigateHome();
    }

    public void NavigateHome()
    {
        PageHost.Content = _home;
        BackButton.Visibility = Visibility.Collapsed;
    }

    /// <summary>按模块 Key 打开对应页面（自动创建 + 缓存）</summary>
    public void NavigateModule(string key)
    {
        var module = ModuleCatalog.Find(key);
        if (module is null)
        {
            PromptDialog.Show(this, "该模块不存在或已被移除。", "提示",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Information);
            return;
        }

        if (!_pages.TryGetValue(key, out var page))
        {
            try
            {
                page = (UserControl)Activator.CreateInstance(module.ViewType)!;
                _pages[key] = page;
            }
            catch (Exception ex)
            {
                PromptDialog.Show(this, $"打开「{module.Name}」失败：{ex.Message}", "错误",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }
        }

        TryLoad(page);
        PageHost.Content = page;
        BackButton.Visibility = Visibility.Visible;
    }

    /// <summary>约定：ViewModel 有无参 public void Load() 时，每次进入页面刷新数据</summary>
    private static void TryLoad(UserControl page)
    {
        var vm = page.DataContext;
        if (vm is null)
        {
            return;
        }

        try
        {
            var method = vm.GetType().GetMethod("Load", BindingFlags.Public | BindingFlags.Instance,
                binder: null, types: Type.EmptyTypes, modifiers: null);
            if (method is not null && method.ReturnType == typeof(void))
            {
                method.Invoke(vm, null);
            }
        }
        catch
        {
            // 加载失败不阻断页面展示，模块内部通常有自己的异常提示
        }
    }

    /// <summary>打开设置，可指定初始面板；关闭后立刻刷新首页卡片</summary>
    public void OpenSettings(int panel = SettingsWindow.ModulesPanel)
    {
        var dlg = new SettingsWindow
        {
            Owner = this,
            InitialPanel = panel,
            WindowStartupLocation = WindowStartupLocation.Manual
        };
        dlg.Left = Left + (Width - dlg.Width) / 2;
        dlg.Top = Top + (Height - dlg.Height) / 2;
        dlg.ShowDialog();
        _home.RefreshModules();
    }

    private void Back_Click(object sender, RoutedEventArgs e) => NavigateHome();

    private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettings();

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    /// <summary>标题栏空白处拖动窗口；点到按钮时不拖动，避免吞掉点击</summary>
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject d)
        {
            var cur = d;
            while (cur is not null)
            {
                if (cur is System.Windows.Controls.Button or Wpf.Ui.Controls.Button)
                {
                    return;
                }
                cur = VisualTreeHelper.GetParent(cur);
            }
        }

        DragMove();
    }
}
