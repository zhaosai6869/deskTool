using System.Windows;
using Wpf.Ui.Controls;

namespace DeskApp;

/// <summary>手动粘贴登录凭据 JSON 的兜底输入框。</summary>
public partial class TokenInputDialog : FluentWindow
{
    public TokenInputDialog(string json)
    {
        InitializeComponent();
        Wpf.Ui.Appearance.SystemThemeWatcher.Watch(this);
        TokenBox.Text = json;
    }

    public string TokenText => TokenBox.Text.Trim();

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
