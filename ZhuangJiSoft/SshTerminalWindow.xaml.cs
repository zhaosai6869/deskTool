using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Renci.SshNet;
using DeskApp.Models;
using Wpf.Ui.Controls;

namespace DeskApp;

/// <summary>
/// SSH 内嵌终端（借鉴 electerm / nexus-terminal 的 ssh2 shell → xterm 思路）：
/// SSH.NET 的 ShellStream 与 WebView2 里的 xterm.js 双向桥接。
/// 输出用 base64 传递，规避控制台转义与 UTF-8 分块边界问题（nexus-terminal 同样做法）。
/// </summary>
public partial class SshTerminalWindow : FluentWindow
{
    private readonly SshConnectionInfo _info;
    private readonly CancellationTokenSource _cts = new();
    private readonly Channel<byte[]> _output = Channel.CreateUnbounded<byte[]>(
        new UnboundedChannelOptions { SingleReader = true, SingleWriter = true });
    private readonly TaskCompletionSource<(int Cols, int Rows)> _readyTcs = new();

    private SshClient? _client;
    private ShellStream? _shell;

    public SshTerminalWindow(SshConnectionInfo info)
    {
        InitializeComponent();
        _info = info;
        Title = $"SSH 终端 — {info.Name}";
        TitleText.Text = $"SSH 终端 — {info.Name}";
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        Loaded += Window_Loaded;
        Closed += Window_Closed;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            await TermView.EnsureCoreWebView2Async();

            var assets = Path.Combine(AppContext.BaseDirectory, "Assets");
            if (!Directory.Exists(assets))
            {
                Directory.CreateDirectory(assets);
            }

            // 本地资源映射到虚拟域名，避免 file:// 的限制
            TermView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "deskapp.local", assets, CoreWebView2HostResourceAccessKind.Allow);
            // 显式开启 WebMessage，避免环境策略把 window.chrome.webview 关掉
            TermView.CoreWebView2.Settings.IsWebMessageEnabled = true;
            TermView.WebMessageReceived += OnWebMessage;
            TermView.NavigationCompleted += OnNavigationCompleted;
            TermView.CoreWebView2.Navigate("https://deskapp.local/terminal.html");

            (int Cols, int Rows) size;
            try
            {
                size = await _readyTcs.Task.WaitAsync(TimeSpan.FromSeconds(12));
            }
            catch (TimeoutException)
            {
                // 页面就绪信号没回来，不能笼统报 "The operation has timed out."，否则会误判成 SSH 连不上
                throw new InvalidOperationException(
                    "终端页面未就绪：WebView2 未能加载 terminal.html，请确认已安装 WebView2 Runtime");
            }

            try
            {
                await ConnectAsync(size.Cols, size.Rows);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException("SSH 连接失败：" + ex.Message);
            }

            _ = PumpOutputAsync(_cts.Token);
            _ = WriteNoticeAsync($"已连接  {_info.UserName}@{_info.Host}:{_info.Port}", "14;138;46");
        }
        catch (Exception ex)
        {
            ShowError(ex.Message);
        }
    }

    /// <summary>
    /// 页面加载完成后主动向 JS 索取终端尺寸，并触发一次 ready 消息。
    /// 这是 ready 的第二条独立路径：即使页面自动 post 的时机/数值有问题也不会卡住连接。
    /// </summary>
    private async void OnNavigationCompleted(object? sender, CoreWebView2NavigationCompletedEventArgs e)
    {
        if (!e.IsSuccess)
        {
            ShowError("终端页面加载失败：" + e.WebErrorStatus);
            return;
        }

        bool bridgeWarned = false;
        for (int i = 0; i < 16 && !_readyTcs.Task.IsCompleted && !_cts.IsCancellationRequested; i++)
        {
            try
            {
                var bridge = await TermView.ExecuteScriptAsync(
                    "window.bridgeState ? window.bridgeState() : 'missing'");
                if (!bridgeWarned && bridge.Contains("missing", StringComparison.OrdinalIgnoreCase))
                {
                    bridgeWarned = true;
                    StatusText.Text = "警告：终端输入桥接不可用（window.chrome.webview 缺失）";
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xB8, 0x86, 0x0B));
                }

                var raw = await TermView.ExecuteScriptAsync(
                    "window.sendReady ? window.sendReady() : ''");
                var text = JsonSerializer.Deserialize<string>(raw) ?? string.Empty;
                var parts = text.Split('x');
                if (parts.Length == 2
                    && int.TryParse(parts[0], out var cols)
                    && int.TryParse(parts[1], out var rows)
                    && cols > 0 && rows > 0)
                {
                    _readyTcs.TrySetResult((cols, rows));
                    break;
                }
            }
            catch
            {
                // 页面脚本尚未执行完，下一轮重试
            }

            await Task.Delay(250);
        }
    }

    private async Task ConnectAsync(int cols, int rows)
    {
        if (cols <= 0) cols = 120;
        if (rows <= 0) rows = 36;

        var auth = new PasswordAuthenticationMethod(_info.UserName, _info.Password);
        var conn = new ConnectionInfo(_info.Host, _info.Port, _info.UserName, auth)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        _client = new SshClient(conn);
        await Task.Run(() => _client.Connect(), _cts.Token);

        _shell = _client.CreateShellStream("xterm-256color",
            (uint)cols, (uint)rows, 0, 0, 4096);

        StatusText.Text = $"已连接  {_info.UserName}@{_info.Host}:{_info.Port}";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x0E, 0x8A, 0x2E));

        // 后台读循环：阻塞读 ShellStream，写入无界通道，由 UI 线程串行推送给 xterm.js
        _ = Task.Run(() =>
        {
            var buf = new byte[8192];
            try
            {
                while (!_cts.IsCancellationRequested)
                {
                    int n = _shell!.Read(buf, 0, buf.Length);
                    if (n <= 0)
                    {
                        break;
                    }

                    var chunk = new byte[n];
                    Array.Copy(buf, chunk, n);
                    _output.Writer.TryWrite(chunk);
                }
            }
            catch
            {
                // 断开/释放时读会抛异常，正常收尾
            }
            finally
            {
                _output.Writer.TryComplete();
            }
        }, _cts.Token);
    }

    /// <summary>UI 线程串行把字节流写进 xterm.js，保证输出顺序。</summary>
    private async Task PumpOutputAsync(CancellationToken ct)
    {
        try
        {
            while (await _output.Reader.WaitToReadAsync(ct))
            {
                while (_output.Reader.TryRead(out var chunk))
                {
                    var b64 = Convert.ToBase64String(chunk);
                    await TermView.ExecuteScriptAsync($"window.writeOutput('{b64}')");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
    }

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var typeEl))
            {
                return;
            }

            switch (typeEl.GetString())
            {
                case "ready":
                    // cols/rows 为 null/NaN 时兜底，避免整条消息被丢弃导致永久等待
                    _readyTcs.TrySetResult((ReadInt(root, "cols", 120), ReadInt(root, "rows", 36)));
                    break;

                case "jserror":
                    var msg = root.TryGetProperty("message", out var m) ? m.GetString() : "未知脚本错误";
                    StatusText.Text = "终端脚本错误：" + msg;
                    StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38));
                    _ = WriteNoticeAsync("终端脚本错误：" + msg, "209;52;56");
                    break;

                case "input":
                    var data = root.GetProperty("data").GetString();
                    if (!string.IsNullOrEmpty(data) && _shell is not null && _shell.CanWrite)
                    {
                        var bytes = Encoding.UTF8.GetBytes(data);
                        _shell.Write(bytes, 0, bytes.Length);
                        _shell.Flush();
                    }
                    break;

                case "resize":
                    // ShellStream 无公开 resize API；记录但不强行改远端窗口，避免反射 hack
                    break;
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine("WebMessage 解析失败：" + ex.Message);
        }
    }

    private static int ReadInt(JsonElement root, string name, int fallback)
    {
        if (root.TryGetProperty(name, out var el)
            && el.ValueKind == JsonValueKind.Number
            && el.TryGetInt32(out var value)
            && value > 0)
        {
            return value;
        }

        return fallback;
    }

    private void ShowError(string message)
    {
        StatusText.Text = "连接失败：" + message;
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xD1, 0x34, 0x38));
        _ = WriteNoticeAsync("连接失败：" + message, "209;52;56");
    }

    /// <summary>把连接状态/错误写进终端区域，避免失败时一片空白。</summary>
    private async Task WriteNoticeAsync(string text, string rgb)
    {
        try
        {
            if (TermView.CoreWebView2 is null)
            {
                return;
            }

            var json = JsonSerializer.Serialize(text);
            await TermView.ExecuteScriptAsync($"window.writeNotice({json}, '{rgb}')");
        }
        catch
        {
            // 页面尚未就绪时不阻塞连接流程
        }
    }

    private void FileManager_Click(object sender, RoutedEventArgs e)
    {
        var win = new SshFileManagerWindow(_info) { Owner = this };
        win.Show();
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _cts.Cancel();
        try { _shell?.Dispose(); } catch { }
        try { if (_client?.IsConnected == true) _client.Disconnect(); } catch { }
        try { _client?.Dispose(); } catch { }
        _output.Writer.TryComplete();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => SystemCommands.MinimizeWindow(this);
    private void Close_Click(object sender, RoutedEventArgs e) => SystemCommands.CloseWindow(this);

    /// <summary>标题栏空白处拖动窗口；点到按钮时不拖动。</summary>
    private void TitleBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.OriginalSource is DependencyObject d)
        {
            var cur = d;
            while (cur is not null)
            {
                if (cur is System.Windows.Controls.Button or Wpf.Ui.Controls.Button)
                {
                    return;
                }
                cur = VisualTreeHelper.GetParent(cur);
            }
        }

        DragMove();
    }
}
