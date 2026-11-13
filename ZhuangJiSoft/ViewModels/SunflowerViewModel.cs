using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskApp.Data;
using DeskApp.Models;
using DeskApp.Utils;
using Microsoft.EntityFrameworkCore;

namespace DeskApp.ViewModels;

/// <summary>
/// 向日葵模块数据逻辑：记录增删改查 + 免费半自动连接。
/// 与 ToDesk 的差异：不发 -control、不依赖后台服务/UAC/企业 CLI 试用权限；
/// 双击只做「拉起客户端 + 复制识别码」，访问密码由右键菜单复制。
/// 任何失败都有明确提示，不静默返回。
/// </summary>
public partial class SunflowerViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<SunflowerRecord> _items = new();

    [ObservableProperty]
    private SunflowerRecord? _selectedItem;

    public void Load()
    {
        using var db = new AppDbContext();
        db.Database.EnsureCreated();
        Items = new ObservableCollection<SunflowerRecord>(
            db.SunflowerRecords.OrderBy(r => r.Id).ToList());
    }

    public void Add(SunflowerRecord record)
    {
        using var db = new AppDbContext();
        db.SunflowerRecords.Add(record);
        db.SaveChanges();
        Load();
    }

    public void Update(SunflowerRecord target, SunflowerRecord edited)
    {
        using var db = new AppDbContext();
        var entity = db.SunflowerRecords.Find(target.Id);
        if (entity is not null)
        {
            entity.Name = edited.Name;
            entity.DeviceCode = edited.DeviceCode;
            entity.PasswordEncrypted = edited.PasswordEncrypted;
            entity.Notes = edited.Notes;
            db.SaveChanges();
        }

        Load();
    }

    public void Delete(SunflowerRecord target)
    {
        using var db = new AppDbContext();
        var entity = db.SunflowerRecords.Find(target.Id);
        if (entity is not null)
        {
            db.SunflowerRecords.Remove(entity);
            db.SaveChanges();
        }

        Load();
    }

    /// <summary>
    /// 双击行：解析 exe → 未安装提醒 → ① 先复制识别码 → ② 打开并激活向日葵 → ③ 右下角置顶信息窗。
    /// 官方免费版没有「带 ID + 密码直连」的命令行/协议，所以这里不伪造连接参数：
    /// 用户在向日葵【远程协助】里粘贴识别码、输入访问密码或临时验证码即可。
    /// 已运行的向日葵会被恢复并激活到前台顶部，避免只复制了识别码却看不到客户端。
    /// </summary>
    public async Task Connect(SunflowerRecord? record, Window? owner)
    {
        if (record is null)
        {
            return;
        }

        var code = string.Concat((record.DeviceCode ?? string.Empty)
            .Where(c => !char.IsWhiteSpace(c)));
        if (string.IsNullOrEmpty(code))
        {
            PromptDialog.Show(owner, "识别码不能为空。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var exe = SunflowerLauncher.Resolve();
        if (exe is null)
        {
            if (PromptDialog.Show(owner,
                    "未检测到向日葵（AweSun）客户端，请先安装向日葵 Windows 版。\n" +
                    "若已安装但不在标准位置，是否手动指定主程序？",
                    "向日葵未安装", MessageBoxButton.YesNo, MessageBoxImage.Warning)
                != MessageBoxResult.Yes)
            {
                return;
            }

            exe = SunflowerLauncher.PickPath(owner);
            if (exe is null)
            {
                return;
            }

            SunflowerLauncher.SavePath(exe);
        }

        // ① 先把识别码复制好（用户要求：复制在打开软件之前完成）
        try
        {
            Clipboard.SetText(code);
        }
        catch (Exception ex)
        {
            PromptDialog.Show(owner, "识别码复制到剪贴板失败：" + ex.Message,
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // ② 打开向日葵；已运行则不再重复启动，但要把主窗口恢复、激活、放到顶部
        var running = SunflowerLauncher.IsRunning(exe);
        var startOk = running || SunflowerLauncher.StartMain(exe);
        if (!startOk)
        {
            PromptDialog.Show(owner,
                "向日葵主程序启动失败，请手动打开向日葵；识别码已复制到剪贴板。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        else
        {
            // 刚启动时窗口可能还没出现，给 5 秒轮询；已运行只需 2 秒
            var activated = await SunflowerLauncher.ActivateAsync(
                exe, TimeSpan.FromSeconds(running ? 2 : 5));
            if (!activated)
            {
                // 最小化到托盘时可能没有主窗口，交给单实例机制唤起一次并再试激活
                SunflowerLauncher.StartMain(exe);
                await SunflowerLauncher.ActivateAsync(exe, TimeSpan.FromSeconds(2));
            }
        }

        // ③ 右下角置顶信息窗：明文显示识别码与访问密码，各带复制按钮；
        //    点击「复制密码」后由窗口自己倒计时 5 秒关闭
        SunflowerConnectWindow.ShowInfo(code,
            RdpCrypto.Decrypt(record.PasswordEncrypted) ?? string.Empty);
    }
}
