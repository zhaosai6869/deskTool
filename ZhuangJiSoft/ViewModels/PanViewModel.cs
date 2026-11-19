using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskApp.Models;
using DeskApp.Utils;
using DeskApp.Utils.PanProviders;

namespace DeskApp.ViewModels;

/// <summary>网盘直链下载模块 ViewModel。</summary>
public partial class PanViewModel : ObservableObject
{
    public PanConfig Config { get; }

    public ObservableCollection<PanFile> Files { get; } = [];
    public ObservableCollection<PanSiteInfo> Sites { get; } = [];

    /// <summary>站点登录状态展示。</summary>
    public sealed partial class PanSiteInfo : ObservableObject
    {
        public PanSite Site { get; init; }
        public string Name { get; init; } = "";
        public string Icon { get; init; } = "";
        [ObservableProperty] private bool _loggedIn;
    }

    public PanViewModel()
    {
        Config = PanConfig.Load();
        DownloadDir = Config.DownloadDir;
        Threads = Config.Threads;
        foreach (var info in PanSites.All)
        {
            var logged = Config.GetCredential(info.Site).IsValid;
            Sites.Add(new PanSiteInfo
            {
                Site = info.Site, Name = info.Name, Icon = info.Icon, LoggedIn = logged
            });
        }
    }

    [ObservableProperty] private PanSite _selectedSite = PanSite.Quark;
    [ObservableProperty] private string _shareUrl = string.Empty;
    [ObservableProperty] private string _downloadDir = string.Empty;
    [ObservableProperty] private int _threads = 16;
    [ObservableProperty] private string _statusText = "粘贴分享链接，选择网盘后解析";
    [ObservableProperty] private bool _busy;

    private string _contextJson = "{}";
    private PanDownloader? _downloader;

    /// <summary>当前站点的凭据。</summary>
    private PanCredential Credential => Config.GetCredential(SelectedSite);

    public void Login(Window? owner)
    {
        var dlg = new PanLoginWindow(SelectedSite) { Owner = owner };
        if (dlg.ShowDialog() == true && dlg.Credential is not null)
        {
            Config.SetCredential(SelectedSite, dlg.Credential);
            var siteInfo = Sites.FirstOrDefault(s => s.Site == SelectedSite);
            if (siteInfo is not null) siteInfo.LoggedIn = true;
            StatusText = $"{PanSites.NameOf(SelectedSite)} 登录凭据已保存";
        }
    }

    public void Logout(Window? owner)
    {
        if (MessageBox.Show(owner, $"清除 {PanSites.NameOf(SelectedSite)} 的登录凭据？", "退出登录",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        Config.ClearCredential(SelectedSite);
        var siteInfo = Sites.FirstOrDefault(s => s.Site == SelectedSite);
        if (siteInfo is not null) siteInfo.LoggedIn = false;
        StatusText = "已清除登录凭据";
    }

    /// <summary>解析分享链接，列出文件。</summary>
    public async Task ParseAsync(Window? owner)
    {
        if (Busy) return;
        var url = ShareUrl.Trim();
        if (url.Length == 0)
        {
            MessageBox.Show(owner, "请先粘贴分享链接", "网盘下载", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 自动识别网盘
        var detected = PanSites.Detect(url);
        if (detected is not null && detected != SelectedSite)
        {
            SelectedSite = detected.Value;
            StatusText = $"已自动识别为 {PanSites.NameOf(detected.Value)}";
        }

        Busy = true;
        try
        {
            StatusText = "正在解析分享链接…";
            var provider = PanProviderFactory.Create(SelectedSite);
            var result = await provider.ParseShareAsync(url, Credential);
            Files.Clear();
            foreach (var f in result.Files) Files.Add(f);
            _contextJson = result.ContextJson;
            StatusText = result.Files.Count > 0
                ? $"解析到 {result.Files.Count} 个文件"
                : "未解析到文件：该网盘分享解析可能需要登录态或真实链接联调";
        }
        catch (Exception ex)
        {
            Files.Clear();
            StatusText = "解析失败：" + ex.Message;
            MessageBox.Show(owner, "解析失败：" + ex.Message +
                "\n\n请确认：①已登录该网盘 ②链接有效 ③如仍失败，该网盘接口可能需要联调适配。",
                "网盘下载", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>下载勾选文件。</summary>
    public async Task DownloadAllAsync(Window? owner)
    {
        if (Busy) return;
        var selected = Files.Where(f => f.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(owner, "请先勾选要下载的文件", "网盘下载", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (string.IsNullOrWhiteSpace(DownloadDir)) DownloadDir = PanConfig.DefaultDownloadDir;
        Config.DownloadDir = DownloadDir.Trim();
        Config.Threads = Threads;
        Config.Save();
        try { Directory.CreateDirectory(Config.DownloadDir); }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "无法创建保存目录：" + ex.Message, "网盘下载",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Busy = true;
        _downloader = new PanDownloader();
        var provider = PanProviderFactory.Create(SelectedSite);
        var ok = 0;
        var fail = 0;
        try
        {
            foreach (var file in selected)
            {
                file.Status = "获取直链…";
                try
                {
                    var direct = await provider.GetDirectLinkAsync(file, _contextJson, Credential);
                    var target = Path.Combine(Config.DownloadDir, Sanitize(file.Name));
                    file.Status = "下载中…";
                    var progress = new Progress<PanDownloader.ProgressInfo>(p =>
                    {
                        file.Progress = p.Percent;
                        file.Status = $"下载中 {p.Percent:F0}%" +
                            (p.SpeedBytesPerSec > 0 ? $" · {FormatSpeed(p.SpeedBytesPerSec)}" : "");
                    });
                    await _downloader.DownloadAsync(direct, target, new PanDownloader.Options
                    {
                        Threads = Threads,
                        Retries = Config.Retries,
                        Referer = GetReferer(),
                        UserAgent = PanHttp.DefaultUa
                    }, progress);
                    file.Progress = 100;
                    file.Status = "完成";
                    ok++;
                }
                catch (Exception ex)
                {
                    file.Status = "失败：" + ex.Message;
                    fail++;
                }
            }
            StatusText = $"全部完成：成功 {ok}，失败 {fail}";
            MessageBox.Show(owner, $"下载完成：成功 {ok} 个，失败 {fail} 个\n目录：{Config.DownloadDir}",
                "网盘下载", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            Busy = false;
            _downloader = null;
        }
    }

    public void Cancel() => _downloader?.Cancel();

    private string GetReferer() => PanSites.Get(SelectedSite).HomeUrl;

    private static string Sanitize(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(name.Where(c => !invalid.Contains(c)).ToArray());
    }

    private static string FormatSpeed(long bps) => bps >= 1024 * 1024
        ? $"{bps / 1024.0 / 1024:F1} MB/s" : $"{bps / 1024.0:F0} KB/s";
}
