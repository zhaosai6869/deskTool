using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskApp.Models;
using DeskApp.Utils;
using DeskApp.Utils.ComicProviders;

namespace DeskApp.ViewModels;

/// <summary>
/// 漫画下载模块：按站点独立解析。
/// 支持的站点固定为内置 8 家，每家有自己写死的解析逻辑，不再有通用启发式识别。
/// </summary>
public partial class ComicViewModel : ObservableObject
{
    public ComicViewModel()
    {
        Config = ComicConfig.Load();
        DownloadDir = Config.DownloadDir;
        ExportCbz = true;
        ExportPdf = true;

        foreach (var p in ComicProviderFactory.All)
        {
            Sites.Add(new ComicSite { Key = p.Key, Name = p.DisplayName, Url = p.HomeUrl });
        }
    }

    public ComicConfig Config { get; }

    public ObservableCollection<ComicChapter> Chapters { get; } = [];

    /// <summary>内置固定站点入口（只读，不提供增删改）。</summary>
    public ObservableCollection<ComicSite> Sites { get; } = [];

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _url = string.Empty;
    [ObservableProperty] private string _bookTitle = string.Empty;
    [ObservableProperty] private string _statusText = "粘贴 8 个支持站点之一的漫画目录页网址，点「解析章节」开始";
    [ObservableProperty] private string _siteHint = string.Empty;
    [ObservableProperty] private bool _exportCbz;
    [ObservableProperty] private bool _exportPdf;
    [ObservableProperty] private string _downloadDir = string.Empty;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private int _progress;
    [ObservableProperty] private int _progressTotal;
    [ObservableProperty] private bool _hasChapters;

    private CancellationTokenSource? _cts;

    /// <summary>解析目录页，提取章节列表。</summary>
    public async Task ParseAsync(Window? owner)
    {
        if (Busy) return;
        var input = Url.Trim();
        if (input.Length == 0)
        {
            MessageBox.Show(owner, "请先粘贴漫画目录页网址", "漫画下载",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!Uri.TryCreate(input, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show(owner, "网址格式不正确，需以 http:// 或 https:// 开头", "漫画下载",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var provider = ComicProviderFactory.Resolve(input);
        if (provider is null)
        {
            var hosts = string.Join("、", ComicProviderFactory.All.Select(p => p.Hosts[0]));
            SiteHint = "不支持的网址";
            StatusText = "该网址不属于内置支持的站点";
            MessageBox.Show(owner,
                $"当前只支持这 8 个站点：\n{string.Join("、", ComicProviderFactory.All.Select(p => p.DisplayName))}\n\n" +
                $"可识别的主机：{hosts}",
                "漫画下载", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Busy = true;
        SiteHint = $"{provider.DisplayName} · {provider.Usage}";
        try
        {
            StatusText = $"正在从 {provider.DisplayName} 抓取目录页…";
            var result = await provider.ParseChaptersAsync(input, Config, CancellationToken.None);

            Chapters.Clear();
            foreach (var ch in result.Chapters)
            {
                ch.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ComicChapter.IsSelected)) RefreshSelectionCount();
                };
                Chapters.Add(ch);
            }

            BookTitle = string.IsNullOrWhiteSpace(result.BookTitle) ? provider.DisplayName : result.BookTitle;
            HasChapters = Chapters.Count > 0;
            RefreshSelectionCount();
            StatusText = result.Hint;
        }
        catch (Exception ex)
        {
            Chapters.Clear();
            HasChapters = false;
            RefreshSelectionCount();
            StatusText = "解析失败：" + ex.Message;
            MessageBox.Show(owner, "解析失败：" + ex.Message + "\n\n" + provider.Usage, "漫画下载",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        finally
        {
            Busy = false;
        }
    }

    public void SelectAll(bool selected)
    {
        foreach (var ch in Chapters) ch.IsSelected = selected;
    }

    public void RefreshSelectionCount() => SelectedCount = Chapters.Count(c => c.IsSelected);

    /// <summary>下载勾选章节。</summary>
    public async Task DownloadAsync(Window? owner)
    {
        if (Busy) return;
        var selected = Chapters.Where(c => c.IsSelected).ToList();
        if (selected.Count == 0)
        {
            MessageBox.Show(owner, "请先勾选要下载的章节", "漫画下载",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!ExportCbz && !ExportPdf)
        {
            MessageBox.Show(owner, "请至少勾选一种导出格式（CBZ 或 PDF）", "漫画下载",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var provider = ComicProviderFactory.Resolve(selected[0].Url);
        if (provider is null)
        {
            MessageBox.Show(owner, "无法识别章节所属站点，请重新解析", "漫画下载",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (string.IsNullOrWhiteSpace(DownloadDir))
        {
            DownloadDir = ComicConfig.DefaultDownloadDir;
        }
        Config.DownloadDir = DownloadDir.Trim();
        Config.Save();

        try
        {
            Directory.CreateDirectory(Config.DownloadDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "无法创建保存目录：" + ex.Message, "漫画下载",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Busy = true;
        Progress = 0;
        ProgressTotal = selected.Count;
        var okCount = 0;
        var failCount = 0;

        try
        {
            foreach (var ch in selected)
            {
                ct.ThrowIfCancellationRequested();
                ch.Status = "抓取图片中…";
                ch.ImageCount = 0;
                try
                {
                    var images = await provider.FetchImagesAsync(ch.Url, Config, ct);
                    if (images.Count == 0)
                    {
                        ch.Status = "未找到图片（可能需登录或为付费章节）";
                        failCount++;
                    }
                    else
                    {
                        var safeTitle = ComicExporter.SanitizeFileName(
                            string.IsNullOrWhiteSpace(BookTitle) ? provider.DisplayName : BookTitle);
                        var tmpDir = Path.Combine(Path.GetTempPath(), "comic_dl",
                            Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(tmpDir);
                        try
                        {
                            var referer = provider.RefererFor(ch.Url);
                            var imagePaths = new List<string>();
                            for (var i = 0; i < images.Count; i++)
                            {
                                ct.ThrowIfCancellationRequested();
                                var ext = GuessExt(images[i]);
                                var imgPath = Path.Combine(tmpDir, $"{i + 1:0000}{ext}");
                                var r = await ComicClient.DownloadImageAsync(
                                    images[i], referer, imgPath, Config, ct);
                                if (r.Ok)
                                {
                                    imagePaths.Add(imgPath);
                                    ch.ImageCount = imagePaths.Count;
                                    ch.Status = $"已抓 {imagePaths.Count}/{images.Count} 张";
                                }
                            }

                            if (imagePaths.Count == 0)
                            {
                                ch.Status = "图片全部下载失败（防盗链？）";
                                failCount++;
                            }
                            else
                            {
                                var chName = ComicExporter.SanitizeFileName(ch.Title);
                                var baseName = $"{safeTitle}_{chName}";

                                if (ExportCbz)
                                {
                                    var cbz = Path.Combine(Config.DownloadDir, baseName + ".cbz");
                                    ComicExporter.ExportCbz(imagePaths, UniquePath(cbz));
                                }
                                if (ExportPdf)
                                {
                                    var pdf = Path.Combine(Config.DownloadDir, baseName + ".pdf");
                                    ComicExporter.ExportPdf(imagePaths, UniquePath(pdf));
                                }
                                ch.Status = $"完成（{imagePaths.Count} 张）";
                                okCount++;
                            }
                        }
                        finally
                        {
                            try { Directory.Delete(tmpDir, recursive: true); } catch { /* 忽略 */ }
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    ch.Status = "已取消";
                    throw;
                }
                catch (Exception ex)
                {
                    ch.Status = "失败：" + ex.Message;
                    failCount++;
                }

                Progress++;
                StatusText = $"处理中 {Progress}/{ProgressTotal} · 成功 {okCount} 失败 {failCount}";
            }

            StatusText = $"全部完成：成功 {okCount} 章，失败 {failCount} 章";
            MessageBox.Show(owner, $"下载完成：成功 {okCount} 章，失败 {failCount} 章\n保存目录：{Config.DownloadDir}",
                "漫画下载", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (OperationCanceledException)
        {
            StatusText = $"已取消 · 成功 {okCount} 章，失败 {failCount} 章";
        }
        finally
        {
            Busy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    public void Cancel() => _cts?.Cancel();

    public void OpenDownloadFolder()
    {
        try
        {
            var dir = string.IsNullOrWhiteSpace(DownloadDir) ? ComicConfig.DefaultDownloadDir : DownloadDir;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo { FileName = dir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = "打开目录失败：" + ex.Message;
        }
    }

    /// <summary>用系统默认浏览器打开网站首页。</summary>
    public void OpenSite(ComicSite? site, Window? owner)
    {
        if (site is null || string.IsNullOrWhiteSpace(site.Url)) return;
        if (!Uri.TryCreate(site.Url.Trim(), UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            MessageBox.Show(owner, $"「{site.Name}」的网址无效：{site.Url}", "漫画下载",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo { FileName = uri.AbsoluteUri, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "打开网站失败：" + ex.Message, "漫画下载",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    public void SaveSettings()
    {
        if (string.IsNullOrWhiteSpace(DownloadDir)) DownloadDir = ComicConfig.DefaultDownloadDir;
        Config.DownloadDir = DownloadDir.Trim();
        Config.Save();
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path)) return path;
        var dir = Path.GetDirectoryName(path) ?? string.Empty;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; i < 1000; i++)
        {
            var p = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(p)) return p;
        }
        return path;
    }

    private static string GuessExt(string url)
    {
        var path = url.Split('?')[0].Split('#')[0];
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext is ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".bmp" ? ext : ".jpg";
    }
}
