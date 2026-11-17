using System.Windows.Controls;

namespace DeskApp;

/// <summary>
/// 学科网下载模块（占位）：页面已预留，下载逻辑后续接入。
/// 只声明 [ToolModule]，首页卡片 / 模块管理 / 数据管理会自动识别，无需改其它列表。
/// </summary>
[ToolModule("xuekewang", "学科网下载", "📄",
    "学科网资源下载，功能开发中", Order = 122)]
public partial class XuekewangView : UserControl
{
    public XuekewangView()
    {
        InitializeComponent();
    }
}
