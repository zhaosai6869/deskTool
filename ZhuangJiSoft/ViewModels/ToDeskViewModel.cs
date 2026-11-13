using System;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using DeskApp.Data;
using DeskApp.Models;
using DeskApp.Utils;
using Microsoft.EntityFrameworkCore;

namespace DeskApp.ViewModels;

/// <summary>
/// ToDesk 模块数据逻辑：记录增删改查 + 拉起 ToDesk.exe。
/// 连接方式按 MstscManager：ToDesk.exe -control -id &lt;设备代码&gt; -passwd &lt;密码&gt;。
/// 与 RdpViewModel 一致但不含任何 Ping 逻辑（设备代码不是 IP，无法探测）。
/// </summary>
public partial class ToDeskViewModel : ObservableObject
{
    [ObservableProperty]
    private ObservableCollection<ToDeskRecord> _items = new();

    [ObservableProperty]
    private ToDeskRecord? _selectedItem;

    public void Load()
    {
        using var db = new AppDbContext();
        db.Database.EnsureCreated();
        Items = new ObservableCollection<ToDeskRecord>(
            db.ToDeskRecords.OrderBy(r => r.Id).ToList());
    }

    public void Add(ToDeskRecord record)
    {
        using var db = new AppDbContext();
        db.ToDeskRecords.Add(record);
        db.SaveChanges();
        Load();
    }

    public void Update(ToDeskRecord target, ToDeskRecord edited)
    {
        using var db = new AppDbContext();
        var entity = db.ToDeskRecords.Find(target.Id);
        if (entity is not null)
        {
            entity.Name = edited.Name;
            entity.DeviceId = edited.DeviceId;
            entity.PasswordEncrypted = edited.PasswordEncrypted;
            entity.Notes = edited.Notes;
            db.SaveChanges();
        }

        Load();
    }

    public void Delete(ToDeskRecord target)
    {
        using var db = new AppDbContext();
        var entity = db.ToDeskRecords.Find(target.Id);
        if (entity is not null)
        {
            db.ToDeskRecords.Remove(entity);
            db.SaveChanges();
        }

        Load();
    }

    /// <summary>
    /// 双击行连接：解析 exe → 未安装提醒 → 未启动先拉起主程序 → 激活主窗口到顶部
    /// → 轮询命令行服务就绪 → 用与人工验证一致的方式发 -control，读取 Success
    /// → 连接成功后把远端会话窗口切到前台。
    /// 全过程写 data\todesk_connect.log，任何失败都给明确提示，不静默返回。
    /// </summary>
    public async Task Connect(ToDeskRecord? record, Window? owner)
    {
        if (record is null)
        {
            return;
        }

        Log("=== Connect start ===");
        try
        {
            var id = string.Concat((record.DeviceId ?? string.Empty).Where(c => !char.IsWhiteSpace(c)));
            if (string.IsNullOrEmpty(id))
            {
                PromptDialog.Show(owner, "设备代码不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                Log("abort: empty device id");
                return;
            }

            var pwd = RdpCrypto.Decrypt(record.PasswordEncrypted) ?? string.Empty;
            Log($"device id length={id.Length}, password length={pwd.Length}");

            var exe = ToDeskLauncher.Resolve();
            Log("exe=" + (exe ?? "null"));
            if (exe is null)
            {
                if (ToDeskLauncher.IsProtocolRegistered() && TryProtocol(id, pwd))
                {
                    Log("exe not found, used todesk:// protocol");
                    // 没探测到 exe 也能按进程名把已运行的客户端切到前台
                    var protoActivated = await WindowActivator.ActivateAsync(
                        "ToDesk.exe", TimeSpan.FromSeconds(4), true, "ToDesk");
                    Log("protocol activated=" + protoActivated);
                    return;
                }

                if (PromptDialog.Show(owner,
                        "未检测到 ToDesk 客户端，请先安装 ToDesk for Windows 4.0.0 或更高版本。\n" +
                        "若已安装但不在标准位置，是否手动指定 ToDesk.exe？",
                        "ToDesk 未安装", MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes)
                {
                    Log("abort: user cancelled install hint");
                    return;
                }

                exe = ToDeskLauncher.PickPath(owner);
                if (exe is null)
                {
                    Log("abort: no exe picked");
                    return;
                }

                ToDeskLauncher.SavePath(exe);
            }

            var running = ToDeskLauncher.IsRunning(exe);
            Log("main running=" + running);
            if (!running)
            {
                Log("StartMain=" + ToDeskLauncher.StartMain(exe));
            }

            // 已运行时恢复最小化窗口并切到顶部；冷启动时等窗口出现。
            // 与 -status 就绪探测并行，不额外拖慢连接。
            var activating = ToDeskLauncher.ActivateAsync(
                exe, TimeSpan.FromSeconds(running ? 2 : 6));

            var ready = await ToDeskLauncher.WaitForReadyAsync(exe, TimeSpan.FromSeconds(5));
            Log("ready(fast)=" + ready);
            var activated = await activating;
            Log("activated=" + activated);
            if (!activated && running)
            {
                // 客户端缩在托盘且一直没冒出窗口时，交给单实例机制再唤起一次
                Log("relaunch for activation=" + ToDeskLauncher.StartMain(exe));
                activated = await ToDeskLauncher.ActivateAsync(exe, TimeSpan.FromSeconds(2));
                Log("activated(after relaunch)=" + activated);
            }
            if (!ready)
            {
                var elevated = await ToDeskLauncher.TryStartServiceElevatedAsync();
                Log("elevated=" + elevated);
                if (elevated)
                {
                    ready = await ToDeskLauncher.WaitForReadyAsync(exe, TimeSpan.FromSeconds(20));
                    Log("ready(after UAC)=" + ready);
                }
            }

            if (!ready)
            {
                Log("abort: command service not ready");
                PromptDialog.Show(owner,
                    "ToDesk 后台服务（ToDesk Service）未启动，命令行无法发起连接。\n" +
                    "请在 UAC 提示中允许，或在管理员 PowerShell 执行：sc config ToDesk_Service start= auto; sc start ToDesk_Service",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            var args = ToDeskLauncher.BuildArguments(id, pwd);
            Log("sending -control args=" +
                (string.IsNullOrEmpty(pwd) ? args : args.Replace(pwd, "***")));

            // 服务刚被拉起时 -control 常返回 Error（命令通道尚未初始化完），
            // 最多重试 3 次，每次间隔后重新用 -status 确认通道可用。
            var result = (Ok: false, Output: string.Empty);
            for (var attempt = 1; attempt <= 3; attempt++)
            {
                result = await RunControlAsync(exe, args, pwd);
                var brief = result.Output.Length > 600 ? result.Output[..600] : result.Output;
                Log($"control #{attempt} ok={result.Ok} output={brief.Replace('\n', '|')}");
                if (result.Ok)
                {
                    break;
                }

                if (attempt < 3)
                {
                    await Task.Delay(2000);
                    Log("recheck ready=" +
                        (await ToDeskLauncher.WaitForReadyAsync(exe, TimeSpan.FromSeconds(3))));
                }
            }

            if (!result.Ok)
            {
                var detail = result.Output.Length > 300 ? result.Output[..300] : result.Output;
                PromptDialog.Show(owner,
                    "ToDesk 未接受连接命令（已重试 3 次）：\n" + detail +
                    "\n\n常见原因：后台服务刚启动尚未就绪、设备未在 ToDesk 设备列表中解锁、" +
                    "或填写的不是「安全密码」。\n详情见 data\\todesk_connect.log",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // -control 已被接受后，等远端会话窗口出现，再把面积最大的 ToDesk 窗口带到前台
            await Task.Delay(1200);
            var front = await ToDeskLauncher.ActivateAsync(
                exe, TimeSpan.FromSeconds(2), true);
            Log("post-control activated=" + front);
        }
        catch (Exception ex)
        {
            Log("EXCEPTION " + ex);
            PromptDialog.Show(owner, "连接过程出错：" + ex.Message, "错误",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// 发 -control 并精确判定结果。ToDesk 成功时输出独立 token「Success」，
    /// 失败时输出独立一行「Error」或「Out of service」。
    /// 注意不能用 IndexOf("Success")：输出里 "Successfully loaded UsbRSrcCore.dll"
    /// 会被误判为成功（这是上一版只打开软件却不连接的直接原因之一）。
    /// </summary>
    private static async Task<(bool Ok, string Output)> RunControlAsync(
        string exe, string args, string pwd)
    {
        var exec = await ToDeskLauncher.RunCommandAsync(exe, args, pwd);
        if (!exec.Ok)
        {
            return (false, exec.Output);
        }

        var text = exec.Output;
        var success = Regex.IsMatch(text, @"\bSuccess\b", RegexOptions.IgnoreCase)
                      && !Regex.IsMatch(text, @"\bError\b", RegexOptions.IgnoreCase)
                      && text.IndexOf("Out of service", StringComparison.OrdinalIgnoreCase) < 0;
        return (success, text);
    }

    private static void Log(string message)
    {
        try
        {
            var path = Path.Combine(AppContext.BaseDirectory, "data", "todesk_connect.log");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, $"[{DateTime.Now:HH:mm:ss}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败不影响连接
        }
    }

    /// <summary>官方 ToDesk://connect&amp;设备代码&amp;密码 协议触发，成功返回 true。</summary>
    private static bool TryProtocol(string id, string pwd)
    {
        try
        {
            Process.Start(new ProcessStartInfo(ToDeskLauncher.BuildConnectUri(id, pwd))
            {
                UseShellExecute = true
            });
            return true;
        }
        catch
        {
            return false;
        }
    }
}
