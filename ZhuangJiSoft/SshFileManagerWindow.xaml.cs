using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using DeskApp.Models;
using DeskApp.Services;
using Wpf.Ui.Controls;

namespace DeskApp;

/// <summary>
/// 双栏文件管理（对齐 electerm 的本地/远端双栏 SFTP 面板）。
/// 底层由 IRemoteFileService 抽象，SSH 记录走 SftpFileService，FTP 记录走 FtpFileService。
/// </summary>
public partial class SshFileManagerWindow : FluentWindow
{
    private readonly SshConnectionInfo _info;
    private readonly IRemoteFileService _service;
    private bool _connected;

    public ObservableCollection<FileRow> LocalItems { get; } = new();
    public ObservableCollection<RemoteFileEntry> RemoteItems { get; } = new();

    private string _localPath;
    private string _remotePath;

    public SshFileManagerWindow(SshConnectionInfo info)
    {
        InitializeComponent();
        _info = info;
        _service = info.IsFtp ? new FtpFileService(info) : new SftpFileService(info);
        _localPath = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _remotePath = _service.HomePath;

        var proto = info.IsFtp ? "FTP" : "SFTP";
        Title = $"文件管理（{proto}）— {info.Name}";
        TitleText.Text = $"文件管理（{proto}）— {info.Name}";

        DataContext = this;
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        Loaded += Window_Loaded;
        Closed += Window_Closed;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Status($"正在连接 {_info.Host}:{_info.Port} …");
            await _service.ConnectAsync();
            _connected = true;
            Status("已连接", error: false);
            LoadLocal();
            await LoadRemoteAsync();
        }
        catch (Exception ex)
        {
            Status("连接失败：" + ex.Message, error: true);
        }
    }

    private void Status(string text, bool error = false)
    {
        StatusText.Text = text;
        StatusText.Foreground = new SolidColorBrush(error
            ? Color.FromRgb(0xD1, 0x34, 0x38)
            : Color.FromRgb(0x5A, 0x64, 0x72));
    }

    private void LoadLocal()
    {
        LocalItems.Clear();
        var parent = Directory.GetParent(_localPath);
        if (parent is not null)
        {
            LocalItems.Add(new FileRow { Name = "..", FullPath = parent.FullName, IsDirectory = true, Size = 0 });
        }

        try
        {
            foreach (var dir in Directory.EnumerateDirectories(_localPath).OrderBy(x => x))
            {
                LocalItems.Add(new FileRow
                {
                    Name = Path.GetFileName(dir), FullPath = dir, IsDirectory = true, Size = 0
                });
            }

            foreach (var file in Directory.EnumerateFiles(_localPath).OrderBy(x => x))
            {
                var fi = new FileInfo(file);
                LocalItems.Add(new FileRow
                {
                    Name = fi.Name, FullPath = fi.FullName, IsDirectory = false, Size = fi.Length
                });
            }
        }
        catch (Exception ex)
        {
            Status("本地目录读取失败：" + ex.Message, error: true);
        }

        LocalPathText.Text = "本地：" + _localPath;
    }

    private async Task LoadRemoteAsync()
    {
        if (!_connected) return;
        try
        {
            var entries = await _service.ListAsync(_remotePath);
            RemoteItems.Clear();
            if (_remotePath != "/")
            {
                RemoteItems.Add(new RemoteFileEntry
                {
                    Name = "..", FullPath = _service.GetParent(_remotePath), IsDirectory = true
                });
            }

            foreach (var e in entries) RemoteItems.Add(e);
            RemotePathText.Text = "远端：" + _remotePath;
            Status($"已连接 · {RemoteItems.Count} 项", error: false);
        }
        catch (Exception ex)
        {
            Status("列目录失败：" + ex.Message, error: true);
        }
    }

    private void LocalGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (LocalGrid.SelectedItem is not FileRow row || !row.IsDirectory) return;
        _localPath = Path.GetFullPath(row.FullPath);
        LoadLocal();
    }

    private async void RemoteGrid_DoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (!_connected || RemoteGrid.SelectedItem is not RemoteFileEntry row || !row.IsDirectory) return;
        _remotePath = row.Name == ".." ? _service.GetParent(_remotePath) : row.FullPath;
        await LoadRemoteAsync();
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
    {
        LoadLocal();
        await LoadRemoteAsync();
    }

    private async void Upload_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureConnected()) return;
        if (LocalGrid.SelectedItem is not FileRow row || row.IsDirectory || row.Name == "..")
        {
            Status("请先在左侧选择一个本地文件。", error: true);
            return;
        }

        try
        {
            var target = _service.Combine(_remotePath, row.Name);
            Status($"上传 {row.Name} …");
            await _service.UploadAsync(row.FullPath, target);
            await LoadRemoteAsync();
            Status("上传完成：" + row.Name, error: false);
        }
        catch (Exception ex) { Status("上传失败：" + ex.Message, error: true); }
    }

    private async void Download_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureConnected()) return;
        if (RemoteGrid.SelectedItem is not RemoteFileEntry row || row.IsDirectory || row.Name == "..")
        {
            Status("请先在右侧选择一个远端文件。", error: true);
            return;
        }

        try
        {
            var target = Path.Combine(_localPath, Path.GetFileName(row.Name));
            Status($"下载 {row.Name} …");
            await _service.DownloadAsync(row.FullPath, target);
            LoadLocal();
            Status("下载完成：" + target, error: false);
        }
        catch (Exception ex) { Status("下载失败：" + ex.Message, error: true); }
    }

    private async void Mkdir_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureConnected()) return;
        var name = PromptInput("新建文件夹", "文件夹名称：");
        if (string.IsNullOrWhiteSpace(name)) return;
        try
        {
            await _service.CreateDirectoryAsync(_service.Combine(_remotePath, name.Trim()));
            await LoadRemoteAsync();
            Status("已创建：" + name, error: false);
        }
        catch (Exception ex) { Status("创建失败：" + ex.Message, error: true); }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureConnected()) return;
        if (RemoteGrid.SelectedItem is not RemoteFileEntry row || row.Name == "..")
        {
            Status("请先在右侧选择一个文件/文件夹。", error: true);
            return;
        }

        var name = PromptInput("重命名", "新名称：", row.Name);
        if (string.IsNullOrWhiteSpace(name) || name == row.Name) return;
        try
        {
            await _service.RenameAsync(row.FullPath, _service.Combine(_remotePath, name.Trim()));
            await LoadRemoteAsync();
            Status("已重命名。", error: false);
        }
        catch (Exception ex) { Status("重命名失败：" + ex.Message, error: true); }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (!EnsureConnected()) return;
        if (RemoteGrid.SelectedItem is not RemoteFileEntry row || row.Name == "..")
        {
            Status("请先在右侧选择一个文件/文件夹。", error: true);
            return;
        }

        if (PromptDialog.Show(this, $"确定删除远端「{row.Name}」？", "确认删除",
                System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Question)
            != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            await _service.DeleteAsync(row.FullPath, row.IsDirectory);
            await LoadRemoteAsync();
            Status("已删除：" + row.Name, error: false);
        }
        catch (Exception ex) { Status("删除失败：" + ex.Message, error: true); }
    }

    private bool EnsureConnected()
    {
        if (_connected) return true;
        Status("尚未连接成功，无法操作。", error: true);
        return false;
    }

    /// <summary>轻量输入弹窗（复用主界面配色）。</summary>
    private string? PromptInput(string title, string label, string? defaultValue = null)
    {
        var dlg = new TextInputDialog(title, label, defaultValue ?? string.Empty) { Owner = this };
        return dlg.ShowDialog() == true ? dlg.Value : null;
    }

    private async void Window_Closed(object? sender, EventArgs e)
    {
        _connected = false;
        try { await _service.DisposeAsync(); } catch { }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.OriginalSource is DependencyObject d)
        {
            var cur = d;
            while (cur is not null)
            {
                if (cur is System.Windows.Controls.Button or Wpf.Ui.Controls.Button) return;
                cur = VisualTreeHelper.GetParent(cur);
            }
        }
        DragMove();
    }

    /// <summary>本地文件行。</summary>
    public sealed class FileRow
    {
        public string Name { get; init; } = string.Empty;
        public string FullPath { get; init; } = string.Empty;
        public bool IsDirectory { get; init; }
        public long Size { get; init; }
        public string SizeText => IsDirectory ? "文件夹" : Size < 1024
            ? Size + " B"
            : (Size < 1024 * 1024
                ? (Size / 1024.0).ToString("F1") + " KB"
                : (Size / 1024.0 / 1024).ToString("F1") + " MB");
    }
}
