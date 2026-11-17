using System.Windows.Controls;

namespace DeskApp;

/// <summary>
/// 国家中小学智慧教育平台模块（占位）：页面已预留，下载逻辑后续接入。
/// 只声明 [ToolModule]，首页卡片 / 模块管理 / 数据管理会自动识别，无需改其它列表。
/// </summary>
[ToolModule("smartedu", "国家中小学智慧教育平台", "🎓",
    "国家中小学智慧教育资源下载，功能开发中", Order = 123)]
public partial class SmartEduView : UserControl
{
    public SmartEduView()
    {
        InitializeComponent();
    }
}
