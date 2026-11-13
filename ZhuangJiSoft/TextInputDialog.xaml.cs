using System.Windows;

namespace DeskApp;

/// <summary>轻量文本输入弹窗（文件管理器的新建文件夹 / 重命名）。</summary>
public partial class TextInputDialog : Window
{
    public string Value => ValueBox.Text.Trim();

    public TextInputDialog(string title, string label, string defaultValue)
    {
        InitializeComponent();
        DeskApp.Utils.WindowDragHelper.Enable(this);
        Title = title;
        LabelText.Text = label;
        ValueBox.Text = defaultValue;
        Loaded += (_, _) =>
        {
            ValueBox.Focus();
            ValueBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(Value))
        {
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
