using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
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
/// 第一试卷下载模块：题库扫描（分页缓存到本地 SQLite）→ 学科/年级/版本/关键词筛选 →
/// 勾选批量下载。下载时才请求详情页拿真实文件地址，避免一次抓几千个详情页。
/// </summary>
public class FirstPaperViewModel : ObservableObject
{
    public const int PageSize = 25;

    private readonly List<FirstPaperRecord> _cache = new();
    private List<FirstPaperRecord> _filtered = new();
    private bool _busy;
    private int _pageIndex = 1;
    private int _pageCount = 1;
    private int _totalCount;
    private int _filteredCount;
    private int _selectedCount;
    private string _statusText = "就绪";
    private string _libraryInfo = "题库未更新";
    private string _downloadDir = string.Empty;
    private string _nameTemplate = "[版本]-[标题]";
    private string _keyword = string.Empty;
    private string _selectedVersion = "全部";

    public FirstPaperViewModel()
    {
        Config = FirstPaperConfig.Load();
    }

    public FirstPaperConfig Config { get; private set; }

    public IReadOnlyList<FirstPaperSubject> Subjects => FirstPaperCatalog.Subjects;

    public ObservableCollection<string> Grades { get; } = new();

    public ObservableCollection<string> Versions { get; } = new();

    /// <summary>当前页条目（DataGrid 绑定）。</summary>
    public ObservableCollection<FirstPaperRecord> PageItems { get; } = new();

    public bool Busy
    {
        get => _busy;
        private set => SetProperty(ref _busy, value);
    }

    public int PageIndex
    {
        get => _pageIndex;
        private set => SetProperty(ref _pageIndex, value);
    }

    public int PageCount
    {
        get => _pageCount;
        private set => SetProperty(ref _pageCount, value);
    }

    public int TotalCount
    {
        get => _totalCount;
        private set => SetProperty(ref _totalCount, value);
    }

    public int FilteredCount
    {
        get => _filteredCount;
        private set => SetProperty(ref _filteredCount, value);
    }

    public int SelectedCount
    {
        get => _selectedCount;
        private set => SetProperty(ref _selectedCount, value);
    }

    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public string LibraryInfo
    {
        get => _libraryInfo;
        private set => SetProperty(ref _libraryInfo, value);
    }

    public string DownloadDir
    {
        get => _downloadDir;
        set => SetProperty(ref _downloadDir, value);
    }

    public string NameTemplate
    {
        get => _nameTemplate;
        set => SetProperty(ref _nameTemplate, value);
    }

    public string Keyword
    {
        get => _keyword;
        set => SetProperty(ref _keyword, value);
    }

    public string SelectedVersion
    {
        get => _selectedVersion;
        set => SetProperty(ref _selectedVersion, value);
    }

    /// <summary>由 LauncherWindow 反射调用；首次进入模块加载配置与本地题库。</summary>
    public void Load()
    {
        Config = FirstPaperConfig.Load();
        DownloadDir = Config.DownloadDir;
        NameTemplate = Config.NameTemplate;

        RebuildGrades(Config.Grade);
        ReloadCache();
        RebuildVersions();
        ApplyFilter();

        var subject = FirstPaperCatalog.GetSubject(Config.SubjectKey);
        var key = CacheKey(subject.Key, Config.Grade);
        if (Config.UpdateTimes.TryGetValue(key, out var updated))
        {
            LibraryInfo = $"共 {_cache.Count:N0} 份 · 更新于 {updated:yyyy-MM-dd HH:mm}";
        }
        else
        {
            LibraryInfo = $"共 {_cache.Count:N0} 份 · 题库未更新";
        }

        StatusText = "就绪";
    }

    /// <summary>切换学科：刷新年级列表并加载对应题库。</summary>
    public void SetSubject(FirstPaperSubject? subject)
    {
        if (Busy || subject is null)
        {
            return;
        }

        Config.SubjectKey = subject.Key;
        Config.Save();
        RebuildGrades(Config.Grade);
        ReloadCache();
        ResetFilter();
        RefreshLibraryInfo();
    }

    /// <summary>切换年级：加载对应题库。</summary>
    public void SetGrade(string? grade)
    {
        if (Busy || string.IsNullOrWhiteSpace(grade))
        {
            return;
        }

        Config.Grade = grade;
        Config.Save();
        ReloadCache();
        ResetFilter();
        RefreshLibraryInfo();
    }

    private void RebuildGrades(string preferred)
    {
        var subject = FirstPaperCatalog.GetSubject(Config.SubjectKey);
        Grades.Clear();
        foreach (var grade in subject.Grades)
        {
            Grades.Add(grade);
        }

        Config.Grade = subject.Grades.Contains(preferred) ? preferred : subject.Grades[0];
    }

    private void ReloadCache()
    {
        _cache.Clear();
        using var db = new AppDbContext();
        _cache.AddRange(db.FirstPaperRecords
            .AsNoTracking()
            .Where(r => r.SubjectKey == Config.SubjectKey &&
                        r.GradeKey == FirstPaperCatalog.GradeCode(Config.Grade) &&
                        r.Available)
            .ToList());
    }

    private void RebuildVersions()
    {
        Versions.Clear();
        Versions.Add("全部");
        foreach (var version in _cache.Select(r => r.Version)
                     .Where(v => !string.IsNullOrWhiteSpace(v))
                     .Distinct(StringComparer.CurrentCulture)
                     .OrderBy(v => v, StringComparer.CurrentCulture))
        {
            Versions.Add(version);
        }

        SelectedVersion = Versions.Contains(SelectedVersion) ? SelectedVersion : "全部";
    }

    private void ResetFilter()
    {
        SelectedVersion = "全部";
        Keyword = string.Empty;
        RebuildVersions();
        ApplyFilter();
    }

    /// <summary>按版本 + 关键词筛选并分页。</summary>
    public void ApplyFilter()
    {
        var keyword = Keyword?.Trim() ?? string.Empty;
        _filtered = _cache
            .Where(r => SelectedVersion == "全部" ||
                        string.Equals(r.Version, SelectedVersion, StringComparison.CurrentCulture))
            .Where(r => keyword.Length == 0 ||
                        r.Title.Contains(keyword, StringComparison.CurrentCultureIgnoreCase))
            .OrderByDescending(r => r.UploadDate)
            .ThenByDescending(r => r.Id)
            .ToList();

        TotalCount = _cache.Count;
        FilteredCount = _filtered.Count;
        PageCount = Math.Max(1, (int)Math.Ceiling(_filtered.Count / (double)PageSize));
        if (PageIndex > PageCount)
        {
            PageIndex = PageCount;
        }

        FillPage();
    }

    public void Query(string keyword, string version)
    {
        Keyword = keyword ?? string.Empty;
        SelectedVersion = string.IsNullOrWhiteSpace(version) ? "全部" : version;
        PageIndex = 1;
        ApplyFilter();
    }

    public void GoPage(int page)
    {
        var target = Math.Clamp(page, 1, PageCount);
        if (target == PageIndex)
        {
            return;
        }

        PageIndex = target;
        FillPage();
    }

    private void FillPage()
    {
        PageItems.Clear();
        foreach (var item in _filtered.Skip((PageIndex - 1) * PageSize).Take(PageSize))
        {
            PageItems.Add(item);
        }

        RefreshSelectionCount();
    }

    public void SelectPage(bool selected)
    {
        foreach (var item in PageItems)
        {
            item.IsSelected = selected;
        }

        RefreshSelectionCount();
    }

    public void RefreshSelectionCount()
    {
        SelectedCount = _cache.Count(r => r.IsSelected);
    }

    /// <summary>扫描当前「学科+年级」的全部列表页，增量更新本地题库。</summary>
    public async Task UpdateLibraryAsync(Window? owner)
    {
        if (Busy)
        {
            return;
        }

        var subject = FirstPaperCatalog.GetSubject(Config.SubjectKey);
        var grade = Config.Grade;
        var firstUrl = FirstPaperCatalog.ListUrl(subject.Key, grade);
        var gradeKey = FirstPaperCatalog.GradeCode(grade);

        Busy = true;
        try
        {
            StatusText = "正在读取站点目录…";
            var first = await FetchPageWithRetryAsync(firstUrl, subject.Key, gradeKey);
            var all = new List<FirstPaperRecord>(first.Items);
            var totalPages = first.TotalPages;
            var scanned = 1;

            if (totalPages > 1 && first.TypeId > 0)
            {
                using var throttle = new SemaphoreSlim(4, 4);
                var tasks = Enumerable.Range(2, totalPages - 1).Select(async page =>
                {
                    await throttle.WaitAsync();
                    try
                    {
                        var url = FirstPaperClient.BuildPageUrl(firstUrl, first.TypeId, page);
                        return await FetchPageWithRetryAsync(url, subject.Key, gradeKey);
                    }
                    finally
                    {
                        throttle.Release();
                    }
                }).ToList();

                foreach (var task in tasks)
                {
                    var result = await task;
                    all.AddRange(result.Items);
                    var done = Interlocked.Increment(ref scanned);
                    Ui(() => StatusText = $"正在更新题库… {done}/{totalPages} 页");
                }
            }

            // 同一详情页可能被重复收录，按 URL 去重
            var unique = all
                .GroupBy(r => r.DetailUrl, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToList();

            Ui(() => StatusText = "正在写入本地题库…");
            var added = 0;
            var siteCount = first.TotalCount > 0 ? first.TotalCount : unique.Count;

            using (var db = new AppDbContext())
            {
                var existing = await db.FirstPaperRecords
                    .Where(r => r.SubjectKey == subject.Key && r.GradeKey == gradeKey)
                    .ToListAsync();
                var map = existing.ToDictionary(r => r.DetailUrl, StringComparer.OrdinalIgnoreCase);
                var siteUrls = new HashSet<string>(unique.Select(r => r.DetailUrl), StringComparer.OrdinalIgnoreCase);

                foreach (var item in unique)
                {
                    if (map.TryGetValue(item.DetailUrl, out var old))
                    {
                        old.Title = item.Title;
                        old.Version = item.Version;
                        old.FileType = item.FileType;
                        old.SizeText = item.SizeText;
                        old.UploadDate = item.UploadDate;
                        old.Available = true;
                    }
                    else
                    {
                        db.FirstPaperRecords.Add(item);
                        added++;
                    }
                }

                // 站点已移除的条目仅下架，不删下载记录与文件
                foreach (var old in existing.Where(r => !siteUrls.Contains(r.DetailUrl)))
                {
                    old.Available = false;
                }

                await db.SaveChangesAsync();
                var localCount = await db.FirstPaperRecords
                    .CountAsync(r => r.SubjectKey == subject.Key &&
                                     r.GradeKey == gradeKey &&
                                     r.Available);
                siteCount = localCount;
            }

            var key = CacheKey(subject.Key, grade);
            Config.UpdateTimes[key] = DateTime.Now;
            Config.Save();

            ReloadCache();
            RebuildVersions();
            ApplyFilter();
            RefreshLibraryInfo();
            Ui(() => StatusText = $"题库更新完成：新增 {added:N0} 份，扫描 {totalPages} 页");

            Ui(() => PromptDialog.Show(owner,
                $"新增 {added:N0} 份，扫描 {totalPages} 页\n本地共 {_cache.Count:N0} 份 / 站点标题 {siteCount:N0} 份",
                "扫描完成"));
        }
        catch (Exception ex)
        {
            StatusText = "题库更新失败：" + ex.Message;
            Ui(() => PromptDialog.Show(owner, StatusText, "更新失败",
                MessageBoxButton.OK, MessageBoxImage.Error));
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task<FirstPaperClient.ListPage> FetchPageWithRetryAsync(
        string url, string subjectKey, string gradeKey)
    {
        for (var attempt = 0; attempt <= Config.Retries; attempt++)
        {
            try
            {
                return await FirstPaperClient.FetchListPageAsync(
                    url, subjectKey, gradeKey, Config.TimeoutSeconds, CancellationToken.None);
            }
            catch (SphTransientException) when (attempt < Config.Retries)
            {
                await Task.Delay(600 * (attempt + 1));
            }
        }

        throw new SphException("列表页多次请求失败：" + url);
    }

    /// <summary>下载当前勾选的全部条目。</summary>
    public async Task DownloadSelectedAsync(Window? owner)
    {
        var selected = _cache.Where(r => r.IsSelected).ToList();
        await DownloadRecordsAsync(selected, owner, clearSelectionAfter: true);
    }

    /// <summary>
    /// 下载指定条目（批量勾选或右键单条都走这里）。单条失败不中断批次，最终汇总。
    /// clearSelectionAfter=true 时下载完成后自动取消该行勾选。
    /// </summary>
    public async Task DownloadRecordsAsync(
        IEnumerable<FirstPaperRecord> records, Window? owner, bool clearSelectionAfter)
    {
        if (Busy)
        {
            return;
        }

        var list = records.ToList();
        if (list.Count == 0)
        {
            PromptDialog.Show(owner, "请先勾选要下载的试卷。", "提示");
            return;
        }

        Busy = true;
        var ok = 0;
        var failed = 0;
        try
        {
            Directory.CreateDirectory(Config.DownloadDir);
            for (var i = 0; i < list.Count; i++)
            {
                var record = list[i];
                Ui(() => StatusText = $"正在下载 {i + 1}/{list.Count}：{record.Title}");
                var result = await DownloadOneAsync(record);
                if (result)
                {
                    ok++;
                }
                else
                {
                    failed++;
                }

                if (clearSelectionAfter)
                {
                    Ui(() => record.IsSelected = false);
                    RefreshSelectionCountOnUi();
                }
            }

            Ui(() => StatusText = $"下载完成：成功 {ok} 份，失败 {failed} 份");
            Ui(() => PromptDialog.Show(owner, StatusText, "下载完成", MessageBoxButton.OK,
                failed > 0 ? MessageBoxImage.Warning : MessageBoxImage.Information));
        }
        catch (Exception ex)
        {
            Ui(() => StatusText = "下载过程出错：" + ex.Message);
            Ui(() => PromptDialog.Show(owner, StatusText, "下载失败",
                MessageBoxButton.OK, MessageBoxImage.Error));
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task<bool> DownloadOneAsync(FirstPaperRecord record)
    {
        record.Error = string.Empty;
        SetRecordStatus(record, "解析中");
        try
        {
            for (var attempt = 0; attempt <= Config.Retries; attempt++)
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(record.DownloadUrl))
                    {
                        record.DownloadUrl = await FirstPaperClient.ResolveDownloadUrlAsync(
                            record.DetailUrl, Config.TimeoutSeconds, CancellationToken.None);
                    }

                    var extension = Path.GetExtension(new Uri(record.DownloadUrl).AbsolutePath);
                    if (string.IsNullOrWhiteSpace(extension) || extension.Length > 8)
                    {
                        extension = ".rar";
                    }

                    var fileName = BuildFileName(record) + extension;
                    var target = UniqueTarget(Config.DownloadDir, fileName);

                    var cacheDir = Path.Combine(AppContext.BaseDirectory, "data", "firstpaper_cache");
                    SetRecordStatus(record, "下载中");
                    var bytes = await FirstPaperClient.DownloadFileAsync(
                        record.DownloadUrl, target, cacheDir,
                        Config.TimeoutSeconds,
                        (done, total, text) =>
                        {
                            SetRecordStatus(record, "下载中 " + text);
                            return Task.CompletedTask;
                        }, CancellationToken.None);

                    record.FileName = Path.GetFileName(target);
                    record.Bytes = bytes;

                    if (FirstPaperExtractor.IsArchive(target))
                    {
                        SetRecordStatus(record, "解压中");
                        var extract = await FirstPaperExtractor.ExtractOfficeFilesAsync(
                            target, Config.DownloadDir, cacheDir,
                            text => SetRecordStatus(record, text), CancellationToken.None);

                        if (extract.Success)
                        {
                            // 只在确认 Office 文件已落到下载目录后才删压缩包，解压失败时保留原包
                            try
                            {
                                File.Delete(target);
                            }
                            catch (Exception ex)
                            {
                                record.Error = "Office 已提取，但压缩包删除失败：" + ex.Message;
                            }

                            record.FileName = Path.GetFileName(extract.Files[0]);
                            record.Bytes = extract.TotalBytes;
                            SetRecordStatus(record, "已提取");
                        }
                        else
                        {
                            SetRecordStatus(record, "已下载（未提取）");
                            record.Error = extract.Error ?? "解压失败";
                        }
                    }
                    else
                    {
                        SetRecordStatus(record, "已下载");
                    }

                    record.FinishedAt = DateTime.Now;
                    SaveRecord(record);
                    return true;
                }
                catch (SphTransientException) when (attempt < Config.Retries)
                {
                    SetRecordStatus(record, $"重试中 {attempt + 1}/{Config.Retries}");
                    await Task.Delay(800 * (attempt + 1));
                }
            }

            throw new SphException("下载多次重试仍失败");
        }
        catch (Exception ex)
        {
            SetRecordStatus(record, "失败");
            record.Error = ex.Message;
            record.FinishedAt = DateTime.Now;
            SaveRecord(record);
            return false;
        }
    }

    private string BuildFileName(FirstPaperRecord record)
    {
        var subject = FirstPaperCatalog.GetSubject(record.SubjectKey);
        var template = string.IsNullOrWhiteSpace(NameTemplate) ? "[版本]-[标题]" : NameTemplate;
        var name = template
            .Replace("[版本]", record.Version, StringComparison.Ordinal)
            .Replace("[标题]", record.Title, StringComparison.Ordinal)
            .Replace("[学科]", subject.Name, StringComparison.Ordinal)
            .Replace("[年级]", Config.Grade, StringComparison.Ordinal)
            .Replace("[日期]", record.UploadDate, StringComparison.Ordinal);

        name = SphDownloader.SafeName(name);
        if (name.Length > 110)
        {
            name = name[..110].Trim('_', '.', ' ');
        }

        return string.IsNullOrWhiteSpace(name) ? SphDownloader.SafeName(record.Title) : name;
    }

    private static string UniqueTarget(string dir, string fileName)
    {
        var target = Path.Combine(dir, fileName);
        if (!File.Exists(target))
        {
            return target;
        }

        var stem = Path.GetFileNameWithoutExtension(fileName);
        var ext = Path.GetExtension(fileName);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private static void SaveRecord(FirstPaperRecord record)
    {
        try
        {
            using var db = new AppDbContext();
            var entity = db.FirstPaperRecords.Find(record.Id);
            if (entity is null)
            {
                return;
            }

            entity.DownloadUrl = record.DownloadUrl;
            entity.FileName = record.FileName;
            entity.Bytes = record.Bytes;
            entity.Status = record.Status;
            entity.Error = record.Error;
            entity.FinishedAt = record.FinishedAt;
            db.SaveChanges();
        }
        catch
        {
            // 记录保存失败不影响已下载文件
        }
    }

    public void SaveSettings()
    {
        Config.DownloadDir = DownloadDir;
        Config.NameTemplate = NameTemplate;
        Config.Save();
    }

    public void OpenDownloadFolder()
    {
        Directory.CreateDirectory(Config.DownloadDir);
        ShellHelper.OpenAndSelect(Config.DownloadDir);
    }

    private void RefreshLibraryInfo()
    {
        var key = CacheKey(Config.SubjectKey, Config.Grade);
        var suffix = Config.UpdateTimes.TryGetValue(key, out var updated)
            ? $"更新于 {updated:yyyy-MM-dd HH:mm}"
            : "题库未更新";
        LibraryInfo = $"共 {_cache.Count:N0} 份 · {suffix}";
    }

    private static string CacheKey(string subjectKey, string grade) => $"{subjectKey}|{grade}";

    private static void SetRecordStatus(FirstPaperRecord record, string status) =>
        Ui(() => record.Status = status);

    private void RefreshSelectionCountOnUi() => Ui(RefreshSelectionCount);

    private static void Ui(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess())
        {
            action();
            return;
        }

        dispatcher.Invoke(action);
    }
}
