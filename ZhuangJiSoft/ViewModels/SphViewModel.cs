using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
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
/// 视频号下载模块（参考 QinGeneral/wx-downloader 思路重构）：
/// 输入 weixin.qq.com/sph 分享链接 → 元宝接口解析直链 → 流式下载 MP4（去重、校验、失败重试）。
/// 与其它模块一致：EF Core 记录、构造函数不自动 Load，由导航调用。
/// </summary>
public partial class SphViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<SphRecord> _items = new();

    [ObservableProperty]
    private SphRecord? _selectedItem;

    [ObservableProperty]
    private string _loginText = "未登录元宝";

    [ObservableProperty]
    private bool _busy;

    public SphConfig Config { get; private set; } = SphConfig.Load();

    public void Load()
    {
        Config = SphConfig.Load();
        using var db = new AppDbContext();
        Items = new ObservableCollection<SphRecord>(
            db.SphRecords.OrderByDescending(r => r.Id).Take(500).ToList());
        RefreshLoginText();
    }

    public void RefreshLoginText()
    {
        var auth = YuanbaoAuth.Load();
        LoginText = auth is null ? "未登录元宝" : $"已登录元宝（{auth.SavedAt}）";
    }

    /// <summary>归一化分享链接：只接受 https://weixin.qq.com/sph/&lt;id&gt;，去掉查询串与结尾斜杠。</summary>
    public static string? NormalizeUrl(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var value = raw.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var url) ||
            url.Scheme != "https" || url.Host != "weixin.qq.com")
        {
            return null;
        }

        if (!Regex.IsMatch(url.AbsolutePath, @"^/sph/[A-Za-z0-9_-]+/?$"))
        {
            return null;
        }

        return $"https://weixin.qq.com/sph/{url.AbsolutePath["/sph/".Length..].TrimEnd('/')}";
    }

    /// <summary>
    /// 单条链接下载：校验登录 → 归一化链接 → 去重检查入库 → 解析下载。
    /// 返回 true 表示已开始处理（调用方可清空输入框）；false 表示被前置校验拦下（保留原地址）。
    /// </summary>
    public async Task<bool> DownloadAsync(string? rawInput, Window? owner)
    {
        if (Busy)
        {
            return false;
        }

        if (YuanbaoAuth.Load() is null)
        {
            PromptDialog.Show(owner, "尚未登录元宝，请先点击「登录元宝」完成扫码或手机号登录。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var link = NormalizeUrl(rawInput);
        if (link is null)
        {
            PromptDialog.Show(owner, "请输入视频号分享链接，格式为 https://weixin.qq.com/sph/...（微信里 分享 → 复制链接）。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        Busy = true;
        try
        {
            var record = CheckAndEnqueue(link);
            if (record is not null)
            {
                await ProcessAsync(record);
            }
        }
        finally
        {
            Busy = false;
        }

        return true;
    }

    /// <summary>右键「重新下载」：忽略去重记录强制重下。</summary>
    public async Task RetryAsync(SphRecord? record)
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

    /// <summary>去重检查：同链接已下载且文件大小一致 → 记一条「已跳过」；否则建「排队中」记录入库。</summary>
    private SphRecord? CheckAndEnqueue(string link)
    {
        using var db = new AppDbContext();
        var previous = db.SphRecords
            .Where(r => r.SourceUrl == link && r.Status == "已下载")
            .OrderByDescending(r => r.Id)
            .FirstOrDefault();

        if (previous is not null && !string.IsNullOrEmpty(previous.FileName))
        {
            var path = Path.Combine(Config.DownloadDir, previous.FileName);
            if (File.Exists(path) && new FileInfo(path).Length == previous.Bytes && previous.Bytes > 0)
            {
                var skipped = new SphRecord
                {
                    SourceUrl = link,
                    Title = previous.Title,
                    Author = previous.Author,
                    FileName = previous.FileName,
                    Bytes = previous.Bytes,
                    Status = "已跳过",
                    CreatedAt = DateTime.Now,
                    FinishedAt = DateTime.Now
                };
                db.SphRecords.Add(skipped);
                db.SaveChanges();
                Items.Insert(0, skipped);
                return null;
            }
        }

        var record = new SphRecord { SourceUrl = link, Status = "排队中", CreatedAt = DateTime.Now };
        db.SphRecords.Add(record);
        db.SaveChanges();
        Items.Insert(0, record);
        return record;
    }

    /// <summary>单条记录的完整处理：解析 → 下载（网络类失败按配置重试，指数退避）→ 落库。</summary>
    private async Task ProcessAsync(SphRecord record)
    {
        SetStatus(record, "解析中");
        var auth = YuanbaoAuth.Load();
        if (auth is null)
        {
            Fail(record, "登录凭据已失效，请重新登录元宝");
            return;
        }

        var client = new YuanbaoClient(auth, Config.TimeoutSeconds);
        var identity = SphDownloader.Identity(record.SourceUrl);

        for (var attempt = 0; attempt <= Config.Retries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    SetStatus(record, $"重试中（{attempt}/{Config.Retries}）");
                }

                var video = await client.ResolveAsync(record.SourceUrl, Config.Quality, CancellationToken.None);
                record.Title = video.Title;
                record.Author = video.Author;

                var fileName = $"{SphDownloader.SafeName(video.Title)}-{identity}.mp4";
                var target = Path.Combine(Config.DownloadDir, fileName);

                if (File.Exists(target))
                {
                    // 同名成品已存在（重下/换质量）：自动加序号，绝不覆盖旧文件
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
                    }, CancellationToken.None);

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
                // 登录失效 / 链接无效 / 视频不可见等业务错误不重试
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

    private static void SetStatus(SphRecord record, string status)
    {
        Application.Current?.Dispatcher.Invoke(() => record.Status = status);
    }

    private void Fail(SphRecord record, string message)
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
    private static void Persist(SphRecord record)
    {
        try
        {
            using var db = new AppDbContext();
            var entity = db.SphRecords.Find(record.Id);
            if (entity is null)
            {
                entity = new SphRecord();
                db.SphRecords.Add(entity);
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
        db.SphRecords.RemoveRange(db.SphRecords.ToList());
        db.SaveChanges();
        Items = new ObservableCollection<SphRecord>();
    }
}
