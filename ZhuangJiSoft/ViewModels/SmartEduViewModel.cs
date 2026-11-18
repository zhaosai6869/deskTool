using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskApp.Models;
using DeskApp.Utils;

namespace DeskApp.ViewModels;

/// <summary>
/// 智慧教育平台教材下载模块。筛选条件取自平台 tag 树的学段/学科/版本/年级四级；
/// 每次筛选按条件联网拉取教材清单后本地过滤（不落盘缓存）。
/// PDF 下载需要登录凭据，凭据以模块内粘贴 Token JSON 为主，账号密码自动登录视平台支持情况。
/// </summary>
public partial class SmartEduViewModel : ObservableObject
{
    private const int TimeoutSeconds = 90;
    private const int DownloadIntervalMs = 600;
    private const int PageSize = 20;

    private bool _initialized;
    private SmartEduClient.Catalog? _catalog;
    private List<SmartEduItem> _filtered = [];

    public SmartEduViewModel()
    {
        Config = SmartEduConfig.Load();
        DownloadDir = Config.DownloadDir;
        NameTemplate = Config.NameTemplate;
        TokenJson = Config.HasToken ? Config.TokenJson : string.Empty;
    }

    public SmartEduConfig Config { get; }

    public ObservableCollection<SmartEduOption> Stages { get; } = [];
    public ObservableCollection<SmartEduOption> Subjects { get; } = [];
    public ObservableCollection<SmartEduOption> Editions { get; } = [];
    public ObservableCollection<SmartEduOption> Grades { get; } = [];
    public ObservableCollection<SmartEduItem> PageItems { get; } = [];

    [ObservableProperty] private bool _busy;
    [ObservableProperty] private string _username = string.Empty;
    [ObservableProperty] private string _password = string.Empty;
    [ObservableProperty] private string _tokenJson = string.Empty;
    [ObservableProperty] private bool _hasToken;
    [ObservableProperty] private string _tokenText = "未设置凭据";
    [ObservableProperty] private string _keyword = string.Empty;
    [ObservableProperty] private SmartEduOption? _selectedStage;
    [ObservableProperty] private SmartEduOption? _selectedSubject;
    [ObservableProperty] private SmartEduOption? _selectedEdition;
    [ObservableProperty] private SmartEduOption? _selectedGrade;
    [ObservableProperty] private int _pageIndex = 1;
    [ObservableProperty] private int _pageCount = 1;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string _statusText = "就绪";
    [ObservableProperty] private string _catalogText = "目录未加载";
    [ObservableProperty] private string _downloadDir = string.Empty;
    [ObservableProperty] private string _nameTemplate = "[标题]";

    /// <summary>由 LauncherWindow 反射调用。</summary>
    public void Load()
    {
        if (_initialized)
        {
            return;
        }
        _initialized = true;
        HasToken = Config.HasToken;
        if (HasToken)
        {
            TokenText = "凭据已设置（本地保存）";
        }
        _ = InitializeAsync();
    }

    private async Task InitializeAsync()
    {
        Busy = true;
        try
        {
            await RefreshCatalogAsync();
        }
        catch (Exception ex)
        {
            StatusText = "加载目录失败：" + ex.Message;
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>联网拉取标签树和全量教材清单。</summary>
    public async Task RefreshCatalogAsync()
    {
        Busy = true;
        try
        {
            StatusText = "正在从站点加载教材目录…";
            var catalog = await SmartEduClient.LoadCatalogAsync(TimeoutSeconds, CancellationToken.None);
            _catalog = catalog;

            var previousStage = SelectedStage?.Id;
            Stages.Clear();
            Stages.Add(new SmartEduOption("全部学段", string.Empty));
            foreach (var s in catalog.Tree.Stages)
            {
                Stages.Add(s);
            }
            SelectedStage = Stages.FirstOrDefault(s => s.Id == previousStage) ?? Stages[0];

            CatalogText = $"目录 {catalog.Items.Count:N0} 本 · 更新于 {catalog.RefreshedAt}";
            Config.LastCatalogRefresh = catalog.RefreshedAt;
            Config.Save();
            StatusText = "教材目录已加载";
        }
        finally
        {
            Busy = false;
            ApplyFilters();
        }
    }

    private static IReadOnlyList<SmartEduOption> GetOptions(
        Dictionary<string, List<SmartEduOption>> map, string key) =>
        map.TryGetValue(key, out var list) ? list : [];

    private void SyncSubjects()
    {
        var previous = SelectedSubject?.Id;
        Subjects.Clear();
        Subjects.Add(new SmartEduOption("全部学科", string.Empty));
        if (_catalog is not null && !string.IsNullOrEmpty(SelectedStage?.Id))
        {
            foreach (var s in GetOptions(_catalog.SubjectsByStage, SelectedStage.Id))
            {
                Subjects.Add(s);
            }
        }
        SelectedSubject = Subjects.FirstOrDefault(s => s.Id == previous) ?? Subjects[0];
    }

    private void SyncEditions()
    {
        var previous = SelectedEdition?.Id;
        Editions.Clear();
        Editions.Add(new SmartEduOption("全部版本", string.Empty));
        if (_catalog is not null && !string.IsNullOrEmpty(SelectedSubject?.Id))
        {
            var key = $"{SelectedStage?.Id}|{SelectedSubject.Id}";
            foreach (var e in GetOptions(_catalog.EditionsBySubject, key))
            {
                Editions.Add(e);
            }
        }
        SelectedEdition = Editions.FirstOrDefault(e => e.Id == previous) ?? Editions[0];
    }

    private void SyncGrades()
    {
        var previous = SelectedGrade?.Id;
        Grades.Clear();
        Grades.Add(new SmartEduOption("全部年级", string.Empty));
        if (_catalog is not null && !string.IsNullOrEmpty(SelectedEdition?.Id))
        {
            // 版本 tag_id 跨学段重复，年级按 学段|学科|版本 组合键归属
            var key = $"{SelectedStage?.Id}|{SelectedSubject?.Id}|{SelectedEdition.Id}";
            foreach (var g in GetOptions(_catalog.GradesByEdition, key))
            {
                Grades.Add(g);
            }
        }
        SelectedGrade = Grades.FirstOrDefault(g => g.Id == previous) ?? Grades[0];
    }

    public void OnStageChanged()
    {
        SyncSubjects();
        SyncEditions();
        SyncGrades();
        ApplyFilters();
    }

    public void OnSubjectChanged()
    {
        SyncEditions();
        SyncGrades();
        ApplyFilters();
    }

    public void OnEditionChanged()
    {
        SyncGrades();
        ApplyFilters();
    }

    /// <summary>按当前筛选条件过滤已联网拉取的教材清单并分页。</summary>
    public void ApplyFilters()
    {
        if (Busy || _catalog is null)
        {
            return;
        }

        var query = new SmartEduQuery
        {
            StageId = SelectedStage?.Id ?? string.Empty,
            SubjectId = SelectedSubject?.Id ?? string.Empty,
            EditionId = SelectedEdition?.Id ?? string.Empty,
            GradeId = SelectedGrade?.Id ?? string.Empty,
            Keyword = Keyword.Trim()
        };

        _filtered = SmartEduClient.Filter(_catalog, query, TimeoutSeconds, CancellationToken.None);
        TotalCount = _filtered.Count;
        PageCount = Math.Max(1, (TotalCount + PageSize - 1) / PageSize);
        PageIndex = 1;
        RenderPage();
        StatusText = $"筛选结果 {TotalCount:N0} 本 · 第 1 / {PageCount} 页";
    }

    public void GoPage(int page)
    {
        if (Busy || page == PageIndex || page < 1 || page > PageCount)
        {
            return;
        }
        PageIndex = page;
        RenderPage();
        StatusText = $"筛选结果 {TotalCount:N0} 本 · 第 {PageIndex} / {PageCount} 页";
    }

    private void RenderPage()
    {
        PageItems.Clear();
        foreach (var item in _filtered.Skip((PageIndex - 1) * PageSize).Take(PageSize))
        {
            PageItems.Add(item);
        }
        SelectedCount = 0;
    }

    public void SelectPage(bool selected)
    {
        foreach (var item in PageItems)
        {
            item.IsSelected = selected;
        }
        SelectedCount = PageItems.Count(i => i.IsSelected);
    }

    public void RefreshSelectionCount() => SelectedCount = PageItems.Count(i => i.IsSelected);

    /// <summary>接收登录窗口取回的凭据 JSON 并保存。</summary>
    public bool ApplyLoginToken(string json, out string message) =>
        SaveToken(json, out message);

    /// <summary>保存 Token JSON：接受 { access_token, mac_key, diff }。</summary>
    public bool SaveToken(string json, out string message)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(json))
            {
                Config.AccessToken = string.Empty;
                Config.MacKey = string.Empty;
                Config.TokenDiff = 0;
                HasToken = false;
                TokenText = "未设置凭据";
                SaveSettings();
                message = "已清除凭据";
                return true;
            }

            using var doc = JsonDocument.Parse(json.Trim());
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                message = "凭据必须是 JSON 对象";
                return false;
            }

            string Read(string name)
            {
                if (!root.TryGetProperty(name, out var v))
                {
                    return string.Empty;
                }
                if (v.ValueKind == JsonValueKind.String)
                {
                    return v.GetString() ?? string.Empty;
                }
                return v.ToString();
            }

            var access = Read("access_token");
            var mac = Read("mac_key");
            var diffText = Read("diff");
            if (access.Length == 0 || mac.Length == 0)
            {
                message = "凭据必须包含 access_token 和 mac_key";
                return false;
            }

            Config.AccessToken = access;
            Config.MacKey = mac;
            Config.TokenDiff = long.TryParse(diffText, out var diff) ? diff : 0;
            HasToken = true;
            TokenText = "凭据已设置（本地保存）";
            TokenJson = Config.TokenJson;
            SaveSettings();
            message = "凭据已保存";
            return true;
        }
        catch (JsonException)
        {
            message = "凭据不是有效的 JSON";
            return false;
        }
    }

    public void SaveSettings()
    {
        // 目录为空时回退默认，避免后续下载写空路径导致「保存失败」
        if (string.IsNullOrWhiteSpace(DownloadDir))
        {
            DownloadDir = SmartEduConfig.DefaultDownloadDir;
        }
        Config.DownloadDir = DownloadDir.Trim();
        Config.NameTemplate = string.IsNullOrWhiteSpace(NameTemplate) ? "[标题]" : NameTemplate.Trim();
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
        DownloadRecordsAsync(PageItems.Where(i => i.IsSelected).ToList(), owner);

    public Task DownloadOneAsync(SmartEduItem item, Window? owner) =>
        DownloadRecordsAsync([item], owner);

    private async Task DownloadRecordsAsync(
        IReadOnlyCollection<SmartEduItem> items, Window? owner)
    {
        if (Busy)
        {
            return;
        }
        if (items.Count == 0)
        {
            MessageBox.Show(owner, "请先勾选要下载的教材", "国家中小学智慧教育平台",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        if (!Config.HasToken)
        {
            MessageBox.Show(owner,
                "请先设置登录凭据（Token JSON）。\n平台匿名下载 PDF 会返回 401/403。",
                "国家中小学智慧教育平台", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SaveSettings();
        try
        {
            Directory.CreateDirectory(DownloadDir);
        }
        catch (Exception ex)
        {
            MessageBox.Show(owner, "无法创建保存目录：" + ex.Message, "国家中小学智慧教育平台",
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
                item.Status = "解析中…";
                item.Error = string.Empty;
                try
                {
                    var pdf = await SmartEduClient.ResolvePdfAsync(
                        item.Id, Config, TimeoutSeconds, CancellationToken.None);
                    var path = BuildTargetPath(item, pdf.FileName);
                    var result = await SmartEduClient.DownloadAsync(
                        pdf.Url, path, Config, TimeoutSeconds, CancellationToken.None);
                    if (result.Ok)
                    {
                        succeeded++;
                        item.Status = "已下载";
                        item.FileName = Path.GetFileName(path);
                    }
                    else
                    {
                        failed++;
                        item.Status = "失败";
                        item.Error = result.Message;
                    }
                }
                catch (Exception ex)
                {
                    failed++;
                    item.Status = "失败";
                    item.Error = ex.Message;
                }

                StatusText = $"下载完成 {succeeded} 本，失败 {failed} 本";
                if (!ReferenceEquals(item, items.Last()))
                {
                    await Task.Delay(DownloadIntervalMs);
                }
            }

            foreach (var item in items)
            {
                item.IsSelected = false;
            }
            RefreshSelectionCount();
            MessageBox.Show(owner, $"下载完成：成功 {succeeded} 本，失败 {failed} 本",
                "国家中小学智慧教育平台", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        finally
        {
            Busy = false;
        }
    }

    private string BuildTargetPath(SmartEduItem item, string detailTitle)
    {
        var template = string.IsNullOrWhiteSpace(NameTemplate) ? "[标题]" : NameTemplate;
        var name = template
            .Replace("[标题]", item.Title)
            .Replace("[详情标题]", detailTitle)
            .Replace("[学段]", item.Stage)
            .Replace("[学科]", item.Subject)
            .Replace("[版本]", item.Edition)
            .Replace("[年级]", item.Grade)
            .Replace("[出版社]", item.Provider)
            .Replace("[日期]", DateTime.Now.ToString("yyyyMMdd"));

        name = Regex.Replace(name, @"[\\/:*?""<>|\r\n]", "_").Trim();
        name = Regex.Replace(name, @"\s+", " ");
        if (name.Length > 120)
        {
            name = name[..120];
        }
        if (string.IsNullOrWhiteSpace(name))
        {
            name = item.Id;
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
