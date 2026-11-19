using System;
using System.Windows;

namespace DeskApp;

/// <summary>添加 / 编辑漫画网站入口的弹窗。返回 SiteName / SiteUrl。</summary>
public partial class ComicSiteDialog : Wpf.Ui.Controls.FluentWindow
{
    public string SiteName => NameBox.Text.Trim();
    public string SiteUrl => UrlBox.Text.Trim();

    public ComicSiteDialog(string title, string name = "", string url = "")
    {
        InitializeComponent();
        DeskApp.Utils.WindowDragHelper.Enable(this);
        Title = title;
        NameBox.Text = name;
        UrlBox.Text = url;
        Loaded += (_, _) =>
        {
            NameBox.Focus();
            if (string.IsNullOrEmpty(name)) NameBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(SiteName))
        {
            PromptDialog.Show(this, "请填写网站名称。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var url = SiteUrl;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            PromptDialog.Show(this, "网址格式不正确，需以 http:// 或 https:// 开头。", "提示",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
