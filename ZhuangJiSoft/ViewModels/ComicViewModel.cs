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

namespace DeskApp.ViewModels;

/// <summary>
/// 漫画下载模块：粘贴目录页网址，通用规则解析章节；勾选后逐章抓图，打包 CBZ/PDF。
/// 不绑定任何站点；识别不准时可用 CSS 选择器校正。
/// </summary>
public partial class ComicViewModel : ObservableObject
{
    public ComicViewModel()
    {
        Config = ComicConfig.Load();
        DownloadDir = Config.DownloadDir;
        ChapterSelector = Config.ChapterSelector;
        ImageSelector = Config.ImageSelector;
        ExportCbz = true;
        ExportPdf = true;
    }

    public ComicConfig Config { get; }

    public ObservableCollection<ComicChapter> Chapters { get; } = [];

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _url = string.Empty;
    [ObservableProperty] private string _chapterSelector = string.Empty;
    [ObservableProperty] private string _imageSelector = string.Empty;
    [ObservableProperty] private string _bookTitle = string.Empty;
    [ObservableProperty] private string _statusText = "粘贴漫画目录页网址，点「解析章节」开始";
    [ObservableProperty] private bool _exportCbz;
    [ObservableProperty] private bool _exportPdf;
    [ObservableProperty] private string _downloadDir = string.Empty;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private int _progress;
    [ObservableProperty] private int _progressTotal;
    [ObservableProperty] private bool _hasChapters;

    private CancellationTokenSource? _cts;
    private string _pageReferer = string.Empty;

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

        Config.ChapterSelector = ChapterSelector.Trim();
        Config.ImageSelector = ImageSelector.Trim();
        Config.Save();

        Busy = true;
        _pageReferer = input;
        try
        {
            StatusText = "正在抓取目录页…";
            var html = await ComicClient.GetHtmlAsync(input, Config, CancellationToken.None);
            var result = ComicParser.ParseChapters(html, input, Config.ChapterSelector);

            Chapters.Clear();
            foreach (var ch in result.Chapters)
            {
                ch.PropertyChanged += (_, e) =>
                {
                    if (e.PropertyName == nameof(ComicChapter.IsSelected)) RefreshSelectionCount();
                };
                Chapters.Add(ch);
            }

            // 尝试从 <title> 推断书名
            BookTitle = ExtractTitle(html) ?? GetHostLabel(uri);
            HasChapters = Chapters.Count > 0;
            RefreshSelectionCount();
            StatusText = result.Hint;
        }
        catch (Exception ex)
        {
            Chapters.Clear();
            HasChapters = false;
            StatusText = "解析失败：" + ex.Message;
            MessageBox.Show(owner, "解析失败：" + ex.Message +
                "\n\n可尝试：换一个能正常访问的网址，或在「手动校正」里填章节 CSS 选择器。",
                "漫画下载", MessageBoxButton.OK, MessageBoxImage.Warning);
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
                    var html = await ComicClient.GetHtmlAsync(ch.Url, Config, ct);
                    var images = ComicParser.ParseImages(html, ch.Url, Config.ImageSelector);
                    if (images.Count == 0)
                    {
                        ch.Status = "未找到图片";
                        failCount++;
                    }
                    else
                    {
                        // 下载图片到临时目录
                        var safeTitle = ComicExporter.SanitizeFileName(
                            string.IsNullOrWhiteSpace(BookTitle) ? "comic" : BookTitle);
                        var tmpDir = Path.Combine(Path.GetTempPath(), "comic_dl",
                            Guid.NewGuid().ToString("N"));
                        Directory.CreateDirectory(tmpDir);
                        try
                        {
                            var imagePaths = new List<string>();
                            for (var i = 0; i < images.Count; i++)
                            {
                                ct.ThrowIfCancellationRequested();
                                var ext = GuessExt(images[i]);
                                var imgPath = Path.Combine(tmpDir, $"{i + 1:0000}{ext}");
                                var r = await ComicClient.DownloadImageAsync(
                                    images[i], ch.Url, imgPath, Config, ct);
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

    public void SaveSettings()
    {
        if (string.IsNullOrWhiteSpace(DownloadDir)) DownloadDir = ComicConfig.DefaultDownloadDir;
        Config.DownloadDir = DownloadDir.Trim();
        Config.ChapterSelector = ChapterSelector.Trim();
        Config.ImageSelector = ImageSelector.Trim();
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

    private static string? ExtractTitle(string html)
    {
        var m = System.Text.RegularExpressions.Regex.Match(html,
            @"<title>\s*(.*?)\s*</title>",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Singleline);
        if (!m.Success) return null;
        var title = System.Net.WebUtility.HtmlDecode(m.Groups[1].Value.Trim());
        // 去掉常见的站点后缀
        title = System.Text.RegularExpressions.Regex.Replace(title,
            @"[\s_|—-]+(?:漫画|在线阅读|免费.*?|最新章节).*$", "").Trim();
        return title.Length is > 0 and < 80 ? title : null;
    }

    private static string GetHostLabel(Uri uri)
    {
        var host = uri.Host.Replace("www.", "");
        var parts = host.Split('.');
        return parts.Length >= 2 ? parts[^2] : host;
    }
}
