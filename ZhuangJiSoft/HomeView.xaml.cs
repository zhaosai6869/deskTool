using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using DeskApp.Models;

namespace DeskApp;

/// <summary>
/// 九宫格首页。每个格子是一个工具/功能入口，点击进入对应二级页。
/// </summary>
public partial class HomeView : UserControl
{
    public List<ToolEntry> Tools { get; } = new()
    {
        new ToolEntry { Key = "software",   Name = "装机软件",  Icon = "🛠" },
        new ToolEntry { Key = "winremote",  Name = "win远程",  Icon = "🖥" },
        new ToolEntry { Key = "ssh",        Name = "ssh远程",  Icon = "🐧" },
        new ToolEntry { Key = "sunflower",  Name = "向日葵",   Icon = "🌻" },
        new ToolEntry { Key = "todesk",     Name = "todesk",   Icon = "💻" },
        new ToolEntry { Key = "quicklaunch", Name = "一键启动", Icon = "🚀" },
        new ToolEntry { Key = "douyin",     Name = "抖音下载",  Icon = "🎵" },
        new ToolEntry { Key = "kuaishou",   Name = "快手下载",  Icon = "📺" },
        new ToolEntry { Key = "bilibili",   Name = "B站下载",   Icon = "🎥" },
        new ToolEntry { Key = "wechat_channels", Name = "视频号下载", Icon = "🎬" },
        new ToolEntry { Key = "fanqie",     Name = "番茄小说",  Icon = "📖" }
    };

    public HomeView()
    {
        InitializeComponent();
        DataContext = this;
    }

    private void Tool_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not ToolEntry tool ||
            Window.GetWindow(this) is not LauncherWindow w)
        {
            return;
        }

        switch (tool.Key)
        {
            case "software":
                w.NavigateSoftware();
                break;
            case "winremote":
                w.NavigateWinRemote();
                break;
            case "ssh":
                w.NavigateSshRemote();
                break;
            case "todesk":
                w.NavigateToDesk();
                break;
            case "sunflower":
                w.NavigateSunflower();
                break;
            case "wechat_channels":
                // 视频号下载：分享链接 → 元宝解析 → 下载 MP4
                w.NavigateSph();
                break;
            case "quicklaunch":
                // 一键启动：多模式分组，单击批量拉起一组软件
                w.NavigateQuickLaunch();
                break;
            case "douyin":
                // 抖音下载：分享链接 → 分享页协议解析 → 无水印 1080p
                w.NavigateDouyin();
                break;
            case "kuaishou":
                // 快手下载：分享链接 → live_api 解析 → 无水印视频
                w.NavigateKuaishou();
                break;
            case "bilibili":
                // B站下载：视频链接 → wbi playurl（durl 单文件 MP4）→ 720P
                w.NavigateBili();
                break;
            case "fanqie":
                // 番茄小说：按名称搜索（WebView2 过滑块）或书号直达 → 逐章下载解码 → TXT
                w.NavigateFanqie();
                break;
            default:
                // 功能未实现的模块：提示占位，后续接入二级页
                PromptDialog.Show(w, $"「{tool.Name}」功能开发中，敬请期待。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                break;
        }
    }
}
