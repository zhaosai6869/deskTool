using System.Windows.Controls;

namespace DeskApp;

/// <summary>
/// 第一试卷下载模块（占位）：页面已预留，下载逻辑后续接入。
/// 只声明 [ToolModule]，首页卡片 / 模块管理 / 数据管理会自动出现，无需改其它列表。
/// </summary>
[ToolModule("firstpaper", "第一试卷下载", "📝",
    "第一试卷资源下载，功能开发中", Order = 120)]
public partial class FirstPaperView : UserControl
{
    public FirstPaperView()
    {
        InitializeComponent();
    }
}
