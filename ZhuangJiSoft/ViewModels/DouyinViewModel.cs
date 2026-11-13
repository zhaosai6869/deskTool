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
/// 抖音下载模块（路线 A：分享页协议，参考 aiaoxd/douyinDownloader）：
/// 输入抖音分享链接 → 分享页 _ROUTER_DATA 解析 → /aweme/v1/play 无水印 1080p → 流式下载 MP4。
/// 无需登录；去重、失败重试、记录持久化与视频号模块同构。
/// </summary>
public partial class DouyinViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<DouyinRecord> _items = new();

    [ObservableProperty]
    private DouyinRecord? _selectedItem;

    [ObservableProperty]
    private bool _busy;

    public DouyinConfig Config { get; private set; } = DouyinConfig.Load();

    public void Load()
    {
        Config = DouyinConfig.Load();
        using var db = new AppDbContext();
        Items = new ObservableCollection<DouyinRecord>(
            db.DouyinRecords.OrderByDescending(r => r.Id).Take(500).ToList());
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
        if (string.IsNullOrWhiteSpace(input) || !IsDouyinLink(input))
        {
            PromptDialog.Show(owner, "请输入抖音分享链接（v.douyin.com 短链或 douyin.com/video/xxx，抖音里 分享 → 复制链接）。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        Busy = true;
        try
        {
            var record = new DouyinRecord { SourceUrl = input, Status = "排队中", CreatedAt = DateTime.Now };
            using (var db = new AppDbContext())
            {
                db.DouyinRecords.Add(record);
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
    public async Task RetryAsync(DouyinRecord? record)
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

    /// <summary>输入是否是抖音链接（v.douyin.com 短链或 douyin.com 任意页面链接）。</summary>
    private static bool IsDouyinLink(string value)
    {
        return Uri.TryCreate(value, UriKind.Absolute, out var url) &&
               url.Scheme is "http" or "https" &&
               (url.Host.Equals("v.douyin.com", StringComparison.OrdinalIgnoreCase) ||
                url.Host.EndsWith(".douyin.com", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>单条记录的完整处理：解析（含短链展开 + 访客凭证自动刷新）→ 归一化去重 → 下载（网络类失败重试）→ 落库。</summary>
    private async Task ProcessAsync(DouyinRecord record)
    {
        SetStatus(record, "解析中");
        var client = new DouyinClient(Config.TimeoutSeconds);
        var guestCookie = DouyinGuestAuth.Load();
        var identity = "dy";

        for (var attempt = 0; attempt <= Config.Retries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    SetStatus(record, $"重试中（{attempt}/{Config.Retries}）");
                }

                DouyinVideo video;
                try
                {
                    video = await client.ResolveAsync(record.SourceUrl, guestCookie, CancellationToken.None);
                }
                catch (DouyinGuestStaleException)
                {
                    // 访客 Cookie 失效（分享页只回壳页）：静默刷新后重试一次
                    SetStatus(record, "获取访客凭证中");
                    guestCookie = await DouyinGuestAuth.RefreshAsync();
                    video = await client.ResolveAsync(record.SourceUrl, guestCookie, CancellationToken.None);
                }

                // 归一化后的视频页链接是去重键（首次解析时 SourceUrl 还是原始输入）
                identity = SphDownloader.Identity(video.CanonicalUrl);
                record.SourceUrl = video.CanonicalUrl;
                record.Title = video.Title;
                record.Author = video.Author;

                // 去重：同作品已下载且文件大小一致 → 记「已跳过」
                using (var db = new AppDbContext())
                {
                    var previous = db.DouyinRecords
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
                    DouyinClient.DesktopUserAgent, DouyinClient.Referer);

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

                // 风控挑战页 / 429 需要等一会儿再试（指数退避，封顶 8 秒）
                await Task.Delay(Math.Min(200 << attempt, 8000));
            }
            catch (DouyinGuestStaleException ex)
            {
                // 刷新过一次仍然失效：不再重试，提示用户稍后再来
                Fail(record, "访客凭证刷新后仍无法解析：" + ex.Message);
                return;
            }
            catch (SphException ex)
            {
                // 链接无效 / 作品不可见 / 图文作品等业务错误不重试
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

    private static void SetStatus(DouyinRecord record, string status)
    {
        Application.Current?.Dispatcher.Invoke(() => record.Status = status);
    }

    private static void Fail(DouyinRecord record, string message)
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
    private static void Persist(DouyinRecord record)
    {
        try
        {
            using var db = new AppDbContext();
            var entity = db.DouyinRecords.Find(record.Id);
            if (entity is null)
            {
                entity = new DouyinRecord();
                db.DouyinRecords.Add(entity);
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
        db.DouyinRecords.RemoveRange(db.DouyinRecords.ToList());
        db.SaveChanges();
        Items = new ObservableCollection<DouyinRecord>();
    }
}
