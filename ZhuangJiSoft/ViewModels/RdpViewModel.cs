using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskApp.Data;
using DeskApp.Models;
using DeskApp.Utils;
using Microsoft.EntityFrameworkCore;

namespace DeskApp.ViewModels;

/// <summary>
/// Win 远程（RDP）模块数据逻辑：记录的增删改查 + 生成 .rdp 并拉起 mstsc。
/// </summary>
public partial class RdpViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<RdpRecord> _items = new();

    [ObservableProperty]
    private RdpRecord? _selectedItem;

    public RdpViewModel()
    {
    }

    public void Load()
    {
        using var db = new AppDbContext();
        db.Database.EnsureCreated();
        Items = new ObservableCollection<RdpRecord>(
            db.RdpRecords.OrderBy(r => r.Id).ToList());
        // 进入模块 / 增删改后，重新测一次所有记录的延迟
        PingAllAsync();
    }

    public void Add(RdpRecord record)
    {
        using var db = new AppDbContext();
        db.RdpRecords.Add(record);
        db.SaveChanges();
        Load();
    }

    public void Update(RdpRecord target, RdpRecord edited)
    {
        using var db = new AppDbContext();
        var entity = db.RdpRecords.Find(target.Id);
        if (entity is not null)
        {
            entity.Name = edited.Name;
            entity.Host = edited.Host;
            entity.Port = edited.Port;
            entity.UserName = edited.UserName;
            entity.PasswordEncrypted = edited.PasswordEncrypted;
            entity.Notes = edited.Notes;
            entity.FullScreen = edited.FullScreen;
            entity.AdminMode = edited.AdminMode;
            entity.PromptCredentials = edited.PromptCredentials;
            db.SaveChanges();
        }

        Load();
    }

    public void Delete(RdpRecord target)
    {
        using var db = new AppDbContext();
        var entity = db.RdpRecords.Find(target.Id);
        if (entity is not null)
        {
            db.RdpRecords.Remove(entity);
            db.SaveChanges();
        }

        Load();
    }

    /// <summary>
    /// 生成临时 .rdp 文件并拉起 mstsc.exe。密码以 "password 51:b:" 形式写入，
    /// mstsc 用本机 DPAPI 解密后自动登录（PromptCredentials 时则忽略密码、连接时询问）。
    /// </summary>
    public void Connect(RdpRecord? record, Window? owner)
    {
        if (record is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(record.Host))
        {
            PromptDialog.Show(owner, "主机地址不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dataDir = Path.Combine(AppContext.BaseDirectory, "data");
        Directory.CreateDirectory(dataDir);
        var rdpPath = Path.Combine(dataDir, $"rdp_{record.Id}.rdp");

        var lines = new List<string>
        {
            "screen mode id:i:" + (record.FullScreen ? "2" : "1"),
            "use multimon:i:0",
            "desktopwidth:i:1280",
            "desktopheight:i:720",
            "smart sizing:i:1",
            "session bpp:i:32",
            "username:s:" + record.UserName,
            "full address:s:" + record.Host + ":" + record.Port,
            "audiomode:i:0",
            "redirectclipboard:i:1",
            "autoreconnection enabled:i:1",
            "authentication level:i:2",
            "negotiate security layer:i:1",
            "prompt for credentials:i:" + (record.PromptCredentials ? "1" : "0")
        };

        if (!record.PromptCredentials && !string.IsNullOrEmpty(record.PasswordEncrypted))
        {
            lines.Add("password 51:b:" + record.PasswordEncrypted);
        }

        File.WriteAllLines(rdpPath, lines);

        var args = "\"" + rdpPath + "\"";
        if (record.AdminMode)
        {
            args += " /admin";
        }

        if (record.PromptCredentials)
        {
            args += " /prompt";
        }

        try
        {
            Process.Start(new ProcessStartInfo("mstsc.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            PromptDialog.Show(owner, $"无法启动远程桌面：{ex.Message}", "错误", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>测试全部记录的延迟（后台执行，UI 线程回写，避免界面卡顿）。</summary>
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
    public void PingOneAsync(RdpRecord? item)
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
}
