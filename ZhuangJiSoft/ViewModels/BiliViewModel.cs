using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskApp.Data;
using DeskApp.Models;
using DeskApp.Utils;
using Microsoft.EntityFrameworkCore;

namespace DeskApp.ViewModels;

/// <summary>
/// B站下载模块：输入视频链接 → view API + wbi playurl（durl 单文件 MP4）→ 流式下载。
/// 免登录（720P 上限）、无需 Cookie；去重、失败重试、记录持久化与视频号/抖音/快手模块同构。
/// </summary>
public partial class BiliViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<BiliRecord> _items = new();

    [ObservableProperty]
    private BiliRecord? _selectedItem;

    [ObservableProperty]
    private bool _busy;

    public BiliConfig Config { get; private set; } = BiliConfig.Load();

    public void Load()
    {
        Config = BiliConfig.Load();
        using var db = new AppDbContext();
        Items = new ObservableCollection<BiliRecord>(
            db.BiliRecords.OrderByDescending(r => r.Id).Take(500).ToList());
    }

    /// <summary>
    /// 单条链接下载：校验输入 → 建记录入库 → 解析下载。
    /// 返回 true 表示已开始处理（调用方可清空输入框）；false 表示被前置校验拦下（保留原地址）。
    /// </summary>
    public async Task<bool> DownloadAsync(string? rawInput, Window? owner)
    {
        if (Busy)
        {
            return false;
        }

        var input = rawInput?.Trim();
        if (string.IsNullOrWhiteSpace(input) || !IsBiliLink(input))
        {
            PromptDialog.Show(owner, "请输入B站视频链接（www.bilibili.com/video/BV… 或 b23.tv 短链，App/网页里 分享 → 复制链接）。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        Busy = true;
        try
        {
            var record = new BiliRecord { SourceUrl = input, Status = "排队中", CreatedAt = DateTime.Now };
            using (var db = new AppDbContext())
            {
                db.BiliRecords.Add(record);
                db.SaveChanges();
            }

            Items.Insert(0, record);
            await ProcessAsync(record);
        }
        finally
        {
            Busy = false;
        }

        return true;
    }

    /// <summary>右键「重新下载」：忽略去重记录强制重下。</summary>
    public async Task RetryAsync(BiliRecord? record)
    {
        if (Busy || record is null)
        {
            return;
        }

        Busy = true;
        try
        {
            record.Status = "排队中";
            record.Error = string.Empty;
            await ProcessAsync(record);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>输入是否是B站链接（bilibili.com 任意页面或 b23.tv 短链）。</summary>
    private static bool IsBiliLink(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) || url.Scheme is not ("http" or "https"))
        {
            return false;
        }

        return url.Host.EndsWith("bilibili.com", StringComparison.OrdinalIgnoreCase) ||
               url.Host.Equals("b23.tv", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>单条记录的完整处理：解析（含短链展开与分P）→ 归一化去重 → 下载（网络类失败重试）→ 落库。</summary>
    private async Task ProcessAsync(BiliRecord record)
    {
        SetStatus(record, "解析中");
        var client = new BiliClient(Config.TimeoutSeconds);
        var identity = "bili";

        for (var attempt = 0; attempt <= Config.Retries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    SetStatus(record, $"重试中（{attempt}/{Config.Retries}）");
                }

                var video = await client.ResolveAsync(record.SourceUrl, CancellationToken.None);

                // 归一化后的视频链接是去重键（首次解析时 SourceUrl 还是原始输入）
                identity = SphDownloader.Identity(video.CanonicalUrl);
                record.SourceUrl = video.CanonicalUrl;
                record.Title = video.Title;
                record.Author = video.Author;

                // 去重：同视频已下载且文件大小一致 → 记「已跳过」
                using (var db = new AppDbContext())
                {
                    var previous = db.BiliRecords
                        .Where(r => r.SourceUrl == video.CanonicalUrl && r.Status == "已下载")
                        .OrderByDescending(r => r.Id)
                        .FirstOrDefault();
                    if (previous is not null && !string.IsNullOrEmpty(previous.FileName))
                    {
                        var path = Path.Combine(Config.DownloadDir, previous.FileName);
                        if (File.Exists(path) && new FileInfo(path).Length == previous.Bytes && previous.Bytes > 0)
                        {
                            SetStatus(record, "已跳过");
                            record.FileName = previous.FileName;
                            record.Bytes = previous.Bytes;
                            record.FinishedAt = DateTime.Now;
                            Persist(record);
                            return;
                        }
                    }
                }

                var fileName = $"{SphDownloader.SafeName(video.Title)}-{identity}.mp4";
                var target = Path.Combine(Config.DownloadDir, fileName);

                if (File.Exists(target))
                {
                    // 同名成品已存在（重下）：自动加序号，绝不覆盖旧文件
                    var stem = Path.Combine(Config.DownloadDir, Path.GetFileNameWithoutExtension(fileName));
                    var index = 2;
                    string candidate;
                    do
                    {
                        candidate = $"{stem} ({index++}).mp4";
                    }
                    while (File.Exists(candidate));

                    fileName = Path.GetFileName(candidate);
                    target = candidate;
                }

                SetStatus(record, "下载中");
                var bytes = await SphDownloader.DownloadAsync(
                    video.MediaUrl, target, Config.CacheDir, Config.TimeoutSeconds,
                    (done, total, text) =>
                    {
                        record.Bytes = done;
                        SetStatus(record, $"下载中 {text}");
                        return Task.CompletedTask;
                    }, CancellationToken.None,
                    BiliClient.DesktopUserAgent, BiliClient.Referer);

                record.FileName = fileName;
                record.Bytes = bytes;
                SetStatus(record, "已下载");
                record.FinishedAt = DateTime.Now;
                Persist(record);
                return;
            }
            catch (SphTransientException ex)
            {
                if (attempt == Config.Retries)
                {
                    Fail(record, ex.Message);
                    return;
                }

                await Task.Delay(Math.Min(200 << attempt, 8000));
            }
            catch (SphException ex)
            {
                // 链接无效 / 视频不可见 / 分段视频等业务错误不重试
                Fail(record, ex.Message);
                return;
            }
            catch (Exception ex)
            {
                Fail(record, "下载过程出错：" + ex.Message);
                return;
            }
        }
    }

    private static void SetStatus(BiliRecord record, string status)
    {
        Application.Current?.Dispatcher.Invoke(() => record.Status = status);
    }

    private static void Fail(BiliRecord record, string message)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            record.Status = "失败";
            record.Error = message;
            record.FinishedAt = DateTime.Now;
        });
        Persist(record);
    }

    /// <summary>把内存里的最终状态写回数据库（按 Id 更新，找不到就插入）。</summary>
    private static void Persist(BiliRecord record)
    {
        try
        {
            using var db = new AppDbContext();
            var entity = db.BiliRecords.Find(record.Id);
            if (entity is null)
            {
                entity = new BiliRecord();
                db.BiliRecords.Add(entity);
            }

            entity.SourceUrl = record.SourceUrl;
            entity.Title = record.Title;
            entity.Author = record.Author;
            entity.FileName = record.FileName;
            entity.Bytes = record.Bytes;
            entity.Status = record.Status;
            entity.Error = record.Error;
            entity.CreatedAt = record.CreatedAt;
            entity.FinishedAt = record.FinishedAt;
            db.SaveChanges();
        }
        catch
        {
            // 记录失败不影响已下载的文件
        }
    }

    /// <summary>清空历史记录（仅删表内数据，不动已下载文件）。</summary>
    public void ClearHistory()
    {
        using var db = new AppDbContext();
        db.BiliRecords.RemoveRange(db.BiliRecords.ToList());
        db.SaveChanges();
        Items = new ObservableCollection<BiliRecord>();
    }
}
