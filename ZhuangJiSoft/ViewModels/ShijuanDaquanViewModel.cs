using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskApp.Models;
using DeskApp.Utils;

namespace DeskApp.ViewModels;

/// <summary>
/// 试卷大全下载模块（精简版）：按 /list 的 4 类筛选（学科/年级/学期/版本）+ 排序 + 关键词实时浏览，
/// 勾选批量下载 PDF。不建 SQLite 表、不做全站扫描，避免消耗站点每日下载配额。
/// 配额以详情页「今日还可下载 N 套」为准，本地按天计数仅作兜底。
/// </summary>
public partial class ShijuanDaquanViewModel : ObservableObject
{
    private const int TimeoutSeconds = 60;
    private const int DownloadIntervalMs = 1200;
    private const int GuestDailyQuota = 20;
    private const int MemberDailyQuota = 40;

    private bool _initialized;
    private string _quotaSource = "本地估算";
    private int _lastQuotaId;

    public ShijuanDaquanViewModel()
    {
        Config = ShijuanDaquanConfig.Load();
    }

    public ShijuanDaquanConfig Config { get; private set; }

    public ObservableCollection<SjOption> Subjects { get; } = new(ShijuanDaquanCatalog.Subjects);
    public ObservableCollection<SjOption> Grades { get; } = new(ShijuanDaquanCatalog.Grades);
    public ObservableCollection<SjOption> Terms { get; } = new(ShijuanDaquanCatalog.Terms);
    public ObservableCollection<SjOption> Versions { get; } = new(ShijuanDaquanCatalog.Versions);
    public ObservableCollection<SjSortOption> Sorts { get; } = new(ShijuanDaquanCatalog.Sorts);

    /// <summary>当前页条目（DataGrid 绑定）。</summary>
    public ObservableCollection<ShijuanDaquanItem> PageItems { get; } = new();

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private bool _isLoggedIn;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _keyword = string.Empty;
    [ObservableProperty] private SjOption _selectedSubject = ShijuanDaquanCatalog.Subjects[0];
    [ObservableProperty] private SjOption _selectedGrade = ShijuanDaquanCatalog.Grades[0];
    [ObservableProperty] private SjOption _selectedTerm = ShijuanDaquanCatalog.Terms[0];
    [ObservableProperty] private SjOption _selectedVersion = ShijuanDaquanCatalog.Versions[0];
    [ObservableProperty] private SjSortOption _selectedSort = ShijuanDaquanCatalog.Sorts[0];
    [ObservableProperty] private int _pageIndex = 1;
    [ObservableProperty] private int _pageCount = 1;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private int _remaining;
    [ObservableProperty] private int _quotaTotal;
    [ObservableProperty] private string _quotaText = "配额读取中…";
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private string _downloadDir = string.Empty;
    [ObservableProperty] private string _nameTemplate = "[标题]";

    /// <summary>由 LauncherWindow 反射调用；首次进入做初始化，之后只刷新配额。</summary>
    public void Load()
    {
        if (_initialized)
        {
            _ = RefreshQuotaAsync();
            return;
        }

        _initialized = true;
        Config = ShijuanDaquanConfig.Load();
        DownloadDir = Config.DownloadDir;
        NameTemplate = Config.NameTemplate;
        Username = Config.Username;
        Password = Config.Password;
        QuotaTotal = Config.LastQuotaTotal > 0 ? Config.LastQuotaTotal : GuestDailyQuota;
        Remaining = Config.LastQuotaDate == DateTime.Now.ToString("yyyy-MM-dd")
            ? Math.Max(0, Config.LastQuotaRemaining - Config.UsedToday())
            : Math.Max(0, QuotaTotal - Config.UsedToday());
        UpdateQuotaText();
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        Busy = true;
        try
        {
            if (Config.AutoLogin && !string.IsNullOrWhiteSpace(Username) &&
                !string.IsNullOrWhiteSpace(Password))
            {
                var login = await ShijuanDaquanClient.LoginAsync(Username, Password, 30, CancellationToken.None);
                IsLoggedIn = login.Ok;
                StatusText = login.Ok ? "自动登录成功" : "自动登录失败：" + login.Message;
            }

            await RefreshCoreAsync(resetPage: true);
        }
        catch (Exception ex)
        {
            StatusText = "初始化失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    private SjQuery BuildQuery() => new()
    {
        Sid = SelectedSubject?.Value ?? 0,
        Grade = SelectedGrade?.Value ?? 0,
        Term = SelectedTerm?.Value ?? 0,
        Version = SelectedVersion?.Value ?? 0,
        Sort = SelectedSort?.Key ?? "comprehensive",
        Keyword = Keyword.Trim(),
        Page = Math.Max(1, PageIndex)
    };

    /// <summary>筛选/排序/关键词变化后重新拉第一页。</summary>
    public void ApplyFilters()
    {
        if (!Busy)
        {
            _ = RefreshAsync(resetPage: true);
        }
    }

    public void GoPage(int page)
    {
        if (Busy)
        {
            return;
        }

        var target = Math.Clamp(page, 1, Math.Max(1, PageCount));
        if (target == PageIndex && PageItems.Count > 0)
        {
            return;
        }

        PageIndex = target;
        _ = RefreshAsync(resetPage: false);
    }

    private async Task RefreshAsync(bool resetPage)
    {
        Busy = true;
        try
        {
            await RefreshCoreAsync(resetPage);
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task RefreshCoreAsync(bool resetPage)
    {
        try
        {
            if (resetPage)
            {
                PageIndex = 1;
            }

            var query = BuildQuery();
            var page = await ShijuanDaquanClient.FetchListAsync(query, TimeoutSeconds, CancellationToken.None);

            PageItems.Clear();
            foreach (var item in page.Items)
            {
                PageItems.Add(item);
            }

            if (PageItems.Count > 0)
            {
                _lastQuotaId = PageItems[0].Id;
            }

            TotalCount = page.TotalCount;
            PageCount = Math.Max(1, page.TotalPages);
            if (PageIndex > PageCount)
            {
                PageIndex = PageCount;
            }

            SelectedCount = 0;
            StatusText = string.IsNullOrWhiteSpace(query.Keyword)
                ? $"第 {PageIndex} / {PageCount} 页 · 共 {TotalCount:N0} 套 · 本页 {PageItems.Count} 套"
                : $"关键词全文检索（站点搜索页不与筛选组合）· 共 {TotalCount:N0} 套 · 本页 {PageItems.Count} 套";

            await RefreshQuotaAsync();
        }
        catch (Exception ex)
        {
            StatusText = "加载列表失败：" + ex.Message;
        }
    }

    /// <summary>详情页的配额文案是站点真实剩余次数，该请求本身不消耗下载次数。</summary>
    /// <returns>true 表示成功从站点读到配额，false 表示退化为本地估算。</returns>
    private async Task<bool> RefreshQuotaAsync()
    {
        var id = PageItems.FirstOrDefault()?.Id ?? _lastQuotaId;
        SjQuota? quota = null;
        if (id > 0)
        {
            try
            {
                quota = await ShijuanDaquanClient.FetchQuotaAsync(id, 30, CancellationToken.None);
                if (quota is not null)
                {
                    _lastQuotaId = id;
                }
            }
            catch
            {
                // 站点配额读取失败时用本地计数兜底
            }
        }

        if (quota is not null)
        {
            Remaining = Math.Max(0, quota.Remaining);
            if (quota.DailyTotal > 0)
            {
                QuotaTotal = quota.DailyTotal;
            }

            _quotaSource = "站点";
        }
        else
        {
            QuotaTotal = Config.LastQuotaTotal > 0
                ? Config.LastQuotaTotal
                : (IsLoggedIn ? MemberDailyQuota : GuestDailyQuota);
            Remaining = Math.Max(0, QuotaTotal - Config.UsedToday());
            _quotaSource = "本地估算";
        }

        Config.LastQuotaTotal = QuotaTotal;
        Config.LastQuotaRemaining = Remaining;
        Config.LastQuotaDate = DateTime.Now.ToString("yyyy-MM-dd");
        Config.Save();
        UpdateQuotaText();
        return quota is not null;
    }

    /// <summary>点击右上角配额文字时调用：强制回站点同步今日下载次数。</summary>
    public async Task SyncQuotaAsync()
    {
        if (Busy)
        {
            StatusText = "正在处理其他任务，请稍后再同步配额";
            return;
        }

        Busy = true;
        try
        {
            var ok = await RefreshQuotaAsync();
            StatusText = ok
                ? $"已从站点同步下载配额：今日还可下载 {Remaining} 套"
                : "站点配额读取失败，当前显示本地估算值";
        }
        catch (Exception ex)
        {
            StatusText = "同步配额失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    private void UpdateQuotaText()
    {
        var mode = IsLoggedIn ? "已登录" : "未登录";
        QuotaText = $"{mode} · 今日还可下载 {Remaining} 套（每人每天 {QuotaTotal} 套 · {_quotaSource}）";
    }

    public async Task LoginAsync(Window? owner)
    {
        if (Busy)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(Password))
        {
            MessageBox.Show(owner, "请输入站点用户名和密码", "试卷大全下载",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        Busy = true;
        try
        {
            var result = await ShijuanDaquanClient.LoginAsync(Username, Password, 30, CancellationToken.None);
            IsLoggedIn = result.Ok;
            if (result.Ok)
            {
                Config.AutoLogin = true;
                SaveSettings();
                var quotaOk = await RefreshQuotaAsync();
                StatusText = quotaOk
                    ? "登录成功，已从站点同步下载配额"
                    : "登录成功，但配额同步失败（当前为本地估算）";
            }

            MessageBox.Show(owner, result.Message, "试卷大全下载",
                MessageBoxButton.OK, result.Ok ? MessageBoxImage.Information : MessageBoxImage.Warning);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "登录失败：" + ex.Message, "试卷大全下载",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Busy = false;
        }
    }

    public async Task LogoutAsync(Window? owner)
    {
        if (Busy)
        {
            return;
        }

        ShijuanDaquanClient.Logout();
        IsLoggedIn = false;
        Config.AutoLogin = false;
        SaveSettings();

        Busy = true;
        bool quotaOk;
        try
        {
            quotaOk = await RefreshQuotaAsync();
        }
        catch
        {
            quotaOk = false;
        }
        finally
        {
            Busy = false;
        }

        StatusText = quotaOk
            ? "已退出登录，已从站点同步下载配额"
            : "已退出登录，配额同步失败（当前为本地估算）";
        if (owner is not null)
        {
            MessageBox.Show(owner, "已退出登录，后续按未登录配额下载", "试卷大全下载",
                MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    public void SelectPage(bool selected)
    {
        foreach (var item in PageItems)
        {
            item.IsSelected = selected;
        }

        RefreshSelectionCount();
    }

    public void RefreshSelectionCount() => SelectedCount = PageItems.Count(i => i.IsSelected);

    public void SaveSettings()
    {
        Config.DownloadDir = DownloadDir.Trim();
        Config.NameTemplate = string.IsNullOrWhiteSpace(NameTemplate) ? "[标题]" : NameTemplate.Trim();
        Config.Username = Username.Trim();
        Config.Password = Password;
        Config.Save();
    }

    public void OpenDownloadFolder()
    {
        try
        {
            Directory.CreateDirectory(DownloadDir);
            Process.Start(new ProcessStartInfo { FileName = DownloadDir, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusText = "打开目录失败：" + ex.Message;
        }
    }

    public Task DownloadSelectedAsync(Window? owner) =>
        DownloadRecordsAsync(PageItems.Where(i => i.IsSelected).ToList(), owner, clearSelectionAfter: true);

    public Task DownloadOneAsync(ShijuanDaquanItem item, Window? owner) =>
        DownloadRecordsAsync(new[] { item }, owner, clearSelectionAfter: false);

    private async Task DownloadRecordsAsync(
        IReadOnlyCollection<ShijuanDaquanItem> items, Window? owner, bool clearSelectionAfter)
    {
        if (Busy)
        {
            return;
        }

        if (items.Count == 0)
        {
            MessageBox.Show(owner, "请先勾选要下载的试卷", "试卷大全下载",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        if (Remaining <= 0)
        {
            MessageBox.Show(owner, "今日下载配额已用完，请明天再试或登录账号", "试卷大全下载",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SaveSettings();
        try
        {
            Directory.CreateDirectory(DownloadDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "无法创建保存目录：" + ex.Message, "试卷大全下载",
                MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        Busy = true;
        var succeeded = 0;
        var failed = 0;
        try
        {
            foreach (var item in items)
            {
                if (Remaining <= 0)
                {
                    StatusText = "今日下载配额已用完，已停止后续下载";
                    break;
                }

                item.Status = "下载中…";
                item.Error = string.Empty;

                // 每次下载前重新读站点剩余次数，登录后 40 套会自动跟随站点数字
                try
                {
                    var quota = await ShijuanDaquanClient.FetchQuotaAsync(item.Id, 30, CancellationToken.None);
                    if (quota is not null)
                    {
                        Remaining = Math.Max(0, quota.Remaining);
                        if (quota.DailyTotal > 0)
                        {
                            QuotaTotal = quota.DailyTotal;
                        }

                        _quotaSource = "站点";
                        UpdateQuotaText();
                        if (Remaining <= 0)
                        {
                            StatusText = "站点提示今日下载配额已用完";
                            break;
                        }
                    }
                }
                catch
                {
                    // 读取失败继续用本地兜底值
                }

                var path = BuildTargetPath(item);
                var result = await ShijuanDaquanClient.DownloadAsync(
                    item.Id, path, TimeoutSeconds, CancellationToken.None);

                if (result.Ok)
                {
                    succeeded++;
                    item.Status = "已下载";
                    item.FileName = Path.GetFileName(path);
                    Config.AddUsage();
                    Remaining = Math.Max(0, Remaining - 1);
                }
                else
                {
                    failed++;
                    item.Status = "失败";
                    item.Error = result.Message;
                    if (result.Message.Contains("限制") || result.Message.Contains("次数") ||
                        result.Message.Contains("上限") || result.Message.Contains("用完"))
                    {
                        Remaining = 0;
                    }
                }

                Config.LastQuotaRemaining = Remaining;
                Config.LastQuotaDate = DateTime.Now.ToString("yyyy-MM-dd");
                Config.Save();
                UpdateQuotaText();
                StatusText = $"下载完成 {succeeded} 份，失败 {failed} 份 · 今日还可下载 {Remaining} 套";

                if (!ReferenceEquals(item, items.Last()))
                {
                    await Task.Delay(DownloadIntervalMs);
                }
            }

            if (clearSelectionAfter)
            {
                foreach (var item in items)
                {
                    item.IsSelected = false;
                }

                RefreshSelectionCount();
            }

            MessageBox.Show(owner, $"下载完成：成功 {succeeded} 份，失败 {failed} 份\n今日还可下载 {Remaining} 套",
                "试卷大全下载", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            StatusText = "下载中断：" + ex.Message;
            MessageBox.Show(owner, "下载中断：" + ex.Message, "试卷大全下载",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Busy = false;
        }
    }

    private string BuildTargetPath(ShijuanDaquanItem item)
    {
        var template = string.IsNullOrWhiteSpace(NameTemplate) ? "[标题]" : NameTemplate;
        var name = template
            .Replace("[标题]", item.Title)
            .Replace("[版本]", item.Version)
            .Replace("[年级]", item.Grade)
            .Replace("[类型]", item.PaperType)
            .Replace("[年份]", item.Year)
            .Replace("[日期]", DateTime.Now.ToString("yyyyMMdd"))
            .Replace("[编号]", item.Id.ToString());

        name = Regex.Replace(name, @"[\\/:*?""<>|\r\n]", "_").Trim();
        name = Regex.Replace(name, @"\s+", " ");
        if (name.Length > 120)
        {
            name = name[..120];
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            name = item.Id.ToString();
        }

        var path = Path.Combine(DownloadDir, name + ".pdf");
        var index = 2;
        while (File.Exists(path))
        {
            path = Path.Combine(DownloadDir, $"{name} ({index++}).pdf");
        }

        return path;
    }
}
