using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text;
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
/// 番茄小说下载模块状态机：
/// 输入书号/目录页链接（或搜索结果）→ 解析全量目录 → 逐章 GET /reader/{itemId} → PUA 解码 → 写 TXT。
/// 支持整本合并 / 分章两种保存模式、逐章进度、章节级重试与用户取消、去重、记录持久化。
/// 与视频号/抖音/B站模块同构（记录表、失败提示、清空历史），但下载主体是文本而非流媒体。
/// </summary>
public partial class FanqieViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<FanqieRecord> _items = new();

    [ObservableProperty]
    private FanqieRecord? _selectedItem;

    [ObservableProperty]
    private bool _busy;

    [ObservableProperty]
    private bool _singleFile = true;

    private CancellationTokenSource? _cts;

    public FanqieConfig Config { get; private set; } = FanqieConfig.Load();

    public void Load()
    {
        Config = FanqieConfig.Load();
        SingleFile = Config.SingleFile;
        using var db = new AppDbContext();
        Items = new ObservableCollection<FanqieRecord>(
            db.FanqieRecords.OrderByDescending(r => r.Id).Take(500).ToList());
    }

    /// <summary>保存模式切换即时写回配置（下次启动沿用）。</summary>
    partial void OnSingleFileChanged(bool value)
    {
        Config.SingleFile = value;
        Config.Save();
    }

    /// <summary>
    /// 按书号/链接下载。返回 true 表示已开始处理（调用方可清空输入框）；false 表示前置校验拦下（保留输入）。
    /// </summary>
    public async Task<bool> DownloadAsync(string? rawInput, Window? owner)
    {
        if (Busy)
        {
            PromptDialog.Show(owner, "已有下载任务在进行中，请等待完成或先取消。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var input = rawInput?.Trim();
        var bookId = FanqieClient.ParseBookId(input ?? string.Empty);
        if (bookId is null)
        {
            PromptDialog.Show(owner,
                "请输入番茄小说目录页链接或小说 ID。\n例如：fanqienovel.com/page/7276384138653862966\n也可直接填 7276384138653862966，或点「按名称搜索」。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        var canonical = FanqieClient.PagePrefix + bookId;
        return await StartDownloadAsync(canonical, owner);
    }

    /// <summary>从搜索结果下载（复用 ID 直达逻辑）。</summary>
    public async Task<bool> DownloadBySearchResultAsync(FanqieSearchResult? result, Window? owner)
    {
        if (Busy)
        {
            PromptDialog.Show(owner, "已有下载任务在进行中，请等待完成或先取消。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        if (result is null)
        {
            PromptDialog.Show(owner, "未选择有效的小说。", "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return false;
        }

        return await StartDownloadAsync(FanqieClient.PagePrefix + result.BookId, owner);
    }

    private async Task<bool> StartDownloadAsync(string canonical, Window? owner)
    {
        Busy = true;
        _cts = new CancellationTokenSource();
        try
        {
            // 去重：同一本已下载且成品文件仍在 → 直接记「已跳过」
            using (var db = new AppDbContext())
            {
                var previous = db.FanqieRecords
                    .Where(r => r.SourceUrl == canonical && r.Status == "已下载")
                    .OrderByDescending(r => r.Id)
                    .FirstOrDefault();
                if (previous is not null && !string.IsNullOrEmpty(previous.FileName))
                {
                    var path = Path.Combine(Config.DownloadDir, previous.FileName);
                    if (File.Exists(path) && new FileInfo(path).Length > 0)
                    {
                        var skip = new FanqieRecord
                        {
                            SourceUrl = canonical, Title = previous.Title, Author = previous.Author,
                            FileName = previous.FileName, Bytes = previous.Bytes,
                            Status = "已跳过", ChapterTotal = previous.ChapterTotal,
                            CreatedAt = DateTime.Now, FinishedAt = DateTime.Now
                        };
                        db.FanqieRecords.Add(skip);
                        db.SaveChanges();
                        Items.Insert(0, skip);
                        return true;
                    }
                }
            }

            var record = new FanqieRecord { SourceUrl = canonical, Status = "排队中", CreatedAt = DateTime.Now };
            using (var db = new AppDbContext())
            {
                db.FanqieRecords.Add(record);
                db.SaveChanges();
            }

            Items.Insert(0, record);
            await ProcessAsync(record, _cts.Token);
            return true;
        }
        finally
        {
            Busy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>用户点「取消」：协作式取消，已写盘的章节保留，未完成章节不落盘为成品。</summary>
    public void Cancel()
    {
        _cts?.Cancel();
    }

    /// <summary>解析目录 → 逐章下载解码 → 写盘 → 落库。</summary>
    private async Task ProcessAsync(FanqieRecord record, CancellationToken ct)
    {
        SetStatus(record, "解析中");
        var client = new FanqieClient(Config.TimeoutSeconds);

        try
        {
            var book = await client.ResolveBookAsync(record.SourceUrl, ct);
            record.Title = book.BookName;
            record.Author = book.Author;
            record.SourceUrl = book.CanonicalUrl;
            record.ChapterTotal = book.Chapters.Count;
            SetStatus(record, "下载中");

            Directory.CreateDirectory(Config.DownloadDir);

            var completed = new List<(FanqieChapter Chapter, string Text)>();

            for (var i = 0; i < book.Chapters.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                var chapter = book.Chapters[i];
                var text = await FetchChapterWithRetryAsync(record, client, book.BookId, chapter, ct);

                // 某章彻底失败：占位保留章节结构，正文标注失败原因，不中断整本
                completed.Add((chapter, text ?? "【本章获取失败，可稍后重新下载本书补回】"));
                record.ChapterDone = i + 1;
                SetStatus(record, "下载中");

                if (Config.ChapterDelayMs > 0)
                {
                    try
                    {
                        await Task.Delay(Config.ChapterDelayMs, ct);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                }
            }

            if (Config.SingleFile)
            {
                await WriteSingleFileAsync(record, book, completed, ct);
            }
            else
            {
                await WriteSplitFilesAsync(record, book, completed, ct);
            }

            SetStatus(record, "已下载");
            record.FinishedAt = DateTime.Now;
            Persist(record);
        }
        catch (OperationCanceledException)
        {
            Fail(record, "已取消");
        }
        catch (SphTransientException ex)
        {
            Fail(record, ex.Message);
        }
        catch (SphException ex)
        {
            Fail(record, ex.Message);
        }
        catch (Exception ex)
        {
            Fail(record, "下载过程出错：" + ex.Message);
        }
    }

    /// <summary>单章抓取：网络类错误按配置重试；业务错误（加锁/无正文）直接返回 null 占位，不重试。</summary>
    private async Task<string?> FetchChapterWithRetryAsync(
        FanqieRecord record, FanqieClient client, string bookId, FanqieChapter chapter, CancellationToken ct)
    {
        for (var attempt = 0; attempt <= Config.Retries; attempt++)
        {
            try
            {
                if (attempt > 0)
                {
                    SetStatus(record, $"重试第{chapter.Order}章（{attempt}/{Config.Retries}）");
                }

                return await client.FetchChapterTextAsync(chapter.ItemId, bookId, ct);
            }
            catch (SphTransientException)
            {
                if (attempt == Config.Retries)
                {
                    return null;
                }

                await Task.Delay(Math.Min(300 << attempt, 4000), ct);
            }
            catch (SphException)
            {
                // 加锁/无正文等业务错误不重试
                return null;
            }
        }

        return null;
    }

    private async Task WriteSingleFileAsync(
        FanqieRecord record, FanqieBook book, List<(FanqieChapter Chapter, string Text)> chapters, CancellationToken ct)
    {
        var safeName = SphDownloader.SafeName(book.BookName);
        var fileName = $"{safeName}.txt";
        var target = Path.Combine(Config.DownloadDir, fileName);

        // 同名成品已存在：加序号，绝不覆盖
        if (File.Exists(target))
        {
            var stem = Path.Combine(Config.DownloadDir, safeName);
            var index = 2;
            do
            {
                target = $"{stem} ({index++}).txt";
            }
            while (File.Exists(target));
            fileName = Path.GetFileName(target);
        }

        var cacheDir = Path.Combine(Config.CacheDir, $"fanqie-{Guid.NewGuid():N}.part");
        Directory.CreateDirectory(Config.CacheDir);
        try
        {
            await using (var output = new StreamWriter(cacheDir, false, new UTF8Encoding(false)))
            {
                await output.WriteLineAsync(book.BookName);
                if (!string.IsNullOrWhiteSpace(book.Author))
                {
                    await output.WriteLineAsync($"作者：{book.Author}");
                }

                await output.WriteLineAsync($"共 {book.Chapters.Count} 章");
                await output.WriteLineAsync();
                await output.WriteLineAsync();

                foreach (var (chapter, text) in chapters)
                {
                    ct.ThrowIfCancellationRequested();
                    await output.WriteLineAsync(chapter.Title);
                    await output.WriteLineAsync();
                    await output.WriteLineAsync(text);
                    await output.WriteLineAsync();
                    await output.WriteLineAsync();
                }

                await output.FlushAsync();
            }

            File.Copy(cacheDir, target, overwrite: false);
            record.FileName = fileName;
            record.Bytes = new FileInfo(target).Length;
        }
        finally
        {
            try { File.Delete(cacheDir); } catch { }
        }
    }

    private async Task WriteSplitFilesAsync(
        FanqieRecord record, FanqieBook book, List<(FanqieChapter Chapter, string Text)> chapters, CancellationToken ct)
    {
        var safeName = SphDownloader.SafeName(book.BookName);
        var dir = Path.Combine(Config.DownloadDir, safeName);
        if (Directory.Exists(dir))
        {
            // 同名目录已存在：加序号，不覆盖旧文件
            var index = 2;
            string candidate;
            do
            {
                candidate = Path.Combine(Config.DownloadDir, $"{safeName} ({index++})");
            }
            while (Directory.Exists(candidate));
            dir = candidate;
        }

        Directory.CreateDirectory(dir);
        long total = 0;
        foreach (var (chapter, text) in chapters)
        {
            ct.ThrowIfCancellationRequested();
            var chapterName = SphDownloader.SafeName($"{chapter.Order:D4}-{chapter.Title}");
            var file = Path.Combine(dir, chapterName + ".txt");
            if (File.Exists(file))
            {
                var idx = 2;
                string candidate;
                do
                {
                    candidate = Path.Combine(dir, $"{chapterName} ({idx++}).txt");
                }
                while (File.Exists(candidate));
                file = candidate;
            }

            await File.WriteAllTextAsync(file, $"{chapter.Title}\n\n{text}\n", new UTF8Encoding(false), ct);
            total += new FileInfo(file).Length;
        }

        record.FileName = safeName + $"（{chapters.Count}章）";
        record.Bytes = total;
    }

    /// <summary>右键重新下载：忽略去重强制重下。</summary>
    public async Task RetryAsync(FanqieRecord? record)
    {
        if (Busy || record is null)
        {
            return;
        }

        Busy = true;
        _cts = new CancellationTokenSource();
        try
        {
            record.Status = "排队中";
            record.Error = string.Empty;
            record.ChapterDone = 0;
            await ProcessAsync(record, _cts.Token);
        }
        finally
        {
            Busy = false;
            _cts?.Dispose();
            _cts = null;
        }
    }

    private static void SetStatus(FanqieRecord record, string status)
    {
        Application.Current?.Dispatcher.Invoke(() => record.Status = status);
    }

    private static void Fail(FanqieRecord record, string message)
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            record.Status = "失败";
            record.Error = message;
            record.FinishedAt = DateTime.Now;
        });
        Persist(record);
    }

    private static void Persist(FanqieRecord record)
    {
        try
        {
            using var db = new AppDbContext();
            var entity = db.FanqieRecords.Find(record.Id);
            if (entity is null)
            {
                entity = new FanqieRecord();
                db.FanqieRecords.Add(entity);
            }

            entity.SourceUrl = record.SourceUrl;
            entity.Title = record.Title;
            entity.Author = record.Author;
            entity.FileName = record.FileName;
            entity.Bytes = record.Bytes;
            entity.ChapterDone = record.ChapterDone;
            entity.ChapterTotal = record.ChapterTotal;
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

    public void ClearHistory()
    {
        using var db = new AppDbContext();
        db.FanqieRecords.RemoveRange(db.FanqieRecords.ToList());
        db.SaveChanges();
        Items = new ObservableCollection<FanqieRecord>();
    }
}
