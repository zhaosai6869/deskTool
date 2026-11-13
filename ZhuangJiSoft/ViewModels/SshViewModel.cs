using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskApp.Data;
using DeskApp.Models;
using DeskApp.Utils;
using Microsoft.EntityFrameworkCore;

namespace DeskApp.ViewModels;

/// <summary>
/// SSH / FTP 模块数据逻辑：记录增删改查 + 延迟探测。
/// 连接动作（终端 / 文件管理）由 View 打开对应窗口，ViewModel 只负责提供解密后的连接信息。
/// </summary>
public partial class SshViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<SshRecord> _items = new();

    [ObservableProperty]
    private SshRecord? _selectedItem;

    public SshViewModel()
    {
    }

    public void Load()
    {
        using var db = new AppDbContext();
        db.Database.EnsureCreated();
        Items = new ObservableCollection<SshRecord>(
            db.SshRecords.OrderBy(r => r.Id).ToList());
        PingAllAsync();
    }

    public void Add(SshRecord record)
    {
        using var db = new AppDbContext();
        db.SshRecords.Add(record);
        db.SaveChanges();
        Load();
    }

    public void Update(SshRecord target, SshRecord edited)
    {
        using var db = new AppDbContext();
        var entity = db.SshRecords.Find(target.Id);
        if (entity is not null)
        {
            entity.Name = edited.Name;
            entity.Host = edited.Host;
            entity.Port = edited.Port;
            entity.UserName = edited.UserName;
            entity.PasswordEncrypted = edited.PasswordEncrypted;
            entity.Protocol = edited.Protocol;
            entity.Notes = edited.Notes;
            db.SaveChanges();
        }

        Load();
    }

    public void Delete(SshRecord target)
    {
        using var db = new AppDbContext();
        var entity = db.SshRecords.Find(target.Id);
        if (entity is not null)
        {
            db.SshRecords.Remove(entity);
            db.SaveChanges();
        }

        Load();
    }

    /// <summary>测试全部记录延迟（后台执行，UI 线程回写）。</summary>
    public void PingAllAsync()
    {
        var list = Items.ToList();
        if (list.Count == 0)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            var results = new Dictionary<int, int?>();
            foreach (var it in list)
            {
                results[it.Id] = NetworkPing.Measure(it.Host);
            }

            Application.Current.Dispatcher.Invoke(() =>
            {
                foreach (var it in Items)
                {
                    if (results.TryGetValue(it.Id, out var ms))
                    {
                        it.PingMs = ms;
                    }
                }
            });
        });
    }

    /// <summary>测试单条记录延迟。</summary>
    public void PingOneAsync(SshRecord? item)
    {
        if (item is null)
        {
            return;
        }

        _ = Task.Run(() =>
        {
            var ms = NetworkPing.Measure(item.Host);
            Application.Current.Dispatcher.Invoke(() => item.PingMs = ms);
        });
    }

    public static SshConnectionInfo CreateConnectionInfo(SshRecord record) =>
        SshConnectionInfo.From(record);
}
