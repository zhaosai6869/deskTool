using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskApp.Models;
using DeskApp.ViewModels;

namespace DeskApp;

/// <summary>
/// 网盘直链下载模块：登录网盘 → 解析分享链接 → 内置多线程下载。
/// 八大网盘 Provider 由 PanProviderFactory 提供；接口端点参考 LinkSwift 提取。
/// </summary>
[ToolModule("pan", "网盘直链下载", "☁️",
    "登录网盘解析分享链接，内置多线程断点续传下载", Order = 125)]
public partial class PanView : UserControl
{
    public PanView()
    {
        InitializeComponent();
        VM = new PanViewModel();
        DataContext = VM;
        VM.PropertyChanged += VM_PropertyChanged;
        Unloaded += (_, _) => VM.Cancel();
        Loaded += (_, _) => HighlightSite();
        UpdateButtons();
    }

    private PanViewModel VM { get; }

    private void VM_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(PanViewModel.Busy)) UpdateButtons();
    }

    private void UpdateButtons()
    {
        var enabled = !VM.Busy;
        UrlBox.IsEnabled = enabled;
        ParseBtn.IsEnabled = enabled;
        DownloadBtn.IsEnabled = enabled;
        BrowseBtn.IsEnabled = enabled;
        LoginBtn.IsEnabled = enabled;
        LogoutBtn.IsEnabled = enabled;
        CancelBtn.IsEnabled = VM.Busy;
    }

    private void Site_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not PanViewModel.PanSiteInfo info) return;
        VM.SelectedSite = info.Site;
        HighlightSite();
        VM.StatusText = $"已选择 {PanSites.NameOf(info.Site)}";
    }

    private void HighlightSite()
    {
        foreach (var child in FindVisualChildren<Button>(this))
        {
            if (child.Tag is PanViewModel.PanSiteInfo info)
            {
                child.BorderBrush = info.Site == VM.SelectedSite
                    ? new SolidColorBrush(Color.FromRgb(0x0F, 0x6C, 0xBD))
                    : new SolidColorBrush(Color.FromRgb(0xD8, 0xDE, 0xE6));
                child.Background = info.Site == VM.SelectedSite
                    ? new SolidColorBrush(Color.FromRgb(0xE8, 0xF2, 0xFD))
                    : Brushes.White;
            }
        }
    }

    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject parent) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if (child is T t) yield return t;
            foreach (var sub in FindVisualChildren<T>(child)) yield return sub;
        }
    }

    private void Login_Click(object sender, RoutedEventArgs e) => VM.Login(Window.GetWindow(this));
    private void Logout_Click(object sender, RoutedEventArgs e) => VM.Logout(Window.GetWindow(this));

    private async void Parse_Click(object sender, RoutedEventArgs e) => await VM.ParseAsync(Window.GetWindow(this));

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && !VM.Busy) _ = VM.ParseAsync(Window.GetWindow(this));
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => VM.Cancel();

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        foreach (var f in VM.Files) f.IsSelected = true;
    }

    private void Row_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (VM.Busy || sender is not DataGridRow row || row.Item is not PanFile file) return;
        file.IsSelected = !file.IsSelected;
    }

    private async void Download_Click(object sender, RoutedEventArgs e) => await VM.DownloadAllAsync(Window.GetWindow(this));

    private void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选择网盘下载保存目录",
            InitialDirectory = Directory.Exists(VM.DownloadDir) ? VM.DownloadDir : string.Empty
        };
        if (dlg.ShowDialog(Window.GetWindow(this)) == true)
        {
            VM.DownloadDir = dlg.FolderName;
            VM.Config.DownloadDir = dlg.FolderName;
            VM.Config.Save();
        }
    }
}
