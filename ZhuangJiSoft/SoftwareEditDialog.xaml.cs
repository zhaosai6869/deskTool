using System.Windows;
using DeskApp.Models;

namespace DeskApp;

/// <summary>
/// 新增/修改弹窗。确定后通过 SoftwareName/Url 属性回传输入结果。
/// </summary>
public partial class SoftwareEditDialog : Window
{
    public string SoftwareName { get; private set; } = string.Empty;
    public string Url { get; private set; } = string.Empty;

    public SoftwareEditDialog()
    {
        InitializeComponent();
        DeskApp.Utils.WindowDragHelper.Enable(this);
    }

    public SoftwareEditDialog(SoftwareItem item) : this()
    {
        NameBox.Text = item.Name;
        UrlBox.Text = item.DownloadUrl;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            PromptDialog.Show(this, "软件名称不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        SoftwareName = name;
        Url = UrlBox.Text.Trim();
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
