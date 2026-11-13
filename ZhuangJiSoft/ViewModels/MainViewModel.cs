using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.EntityFrameworkCore;
using DeskApp.Data;
using DeskApp.Models;

namespace DeskApp.ViewModels;

/// <summary>
/// 装机软件模块的数据逻辑。编辑改用弹窗，故不再持有表单(Form*)属性；排序功能已移除，按 Id 顺序展示。
/// </summary>
public partial class MainViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<SoftwareItem> _items = new();

    [ObservableProperty]
    private SoftwareItem? _selectedItem;

    public MainViewModel()
    {
        Load();
    }

    /// <summary>从数据库加载，按 Id 升序展示</summary>
    public void Load()
    {
        using var db = new AppDbContext();
        db.Database.EnsureCreated();
        Items = new ObservableCollection<SoftwareItem>(
            db.Items.OrderBy(i => i.Id).ToList());
    }

    public void Add(string name, string url)
    {
        using var db = new AppDbContext();
        db.Items.Add(new SoftwareItem { Name = name, DownloadUrl = url });
        db.SaveChanges();
        Load();
    }

    public void Update(SoftwareItem target, string name, string url)
    {
        using var db = new AppDbContext();
        var entity = db.Items.Find(target.Id);
        if (entity is not null)
        {
            entity.Name = name;
            entity.DownloadUrl = url;
            db.SaveChanges();
        }
        Load();
    }

    public void Delete(SoftwareItem target)
    {
        using var db = new AppDbContext();
        var entity = db.Items.Find(target.Id);
        if (entity is not null)
        {
            db.Items.Remove(entity);
            db.SaveChanges();
        }
        Load();
    }

    /// <summary>打开下载地址：点击表格里的链接用默认浏览器打开</summary>
    [RelayCommand]
    private void OpenUrl(SoftwareItem? item)
    {
        var url = item?.DownloadUrl;
        if (string.IsNullOrWhiteSpace(url))
        {
            return;
        }

        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PromptDialog.Show(null, $"无法打开链接：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
