using System.Windows;
using System.Windows.Media;
using SymbolRegular = Wpf.Ui.Controls.SymbolRegular;

namespace DeskApp;

/// <summary>
/// 与编辑弹窗同款风格的提示框，替代系统默认 MessageBox，保证外观统一。
/// 用法：PromptDialog.Show(owner, "消息", "标题", MessageBoxButton.YesNo, MessageBoxImage.Question)。
/// </summary>
public partial class PromptDialog : Window
{
    private enum Mode { Ok, OkCancel, YesNo }

    private Mode _mode = Mode.Ok;
    private MessageBoxResult _result = MessageBoxResult.None;

    public PromptDialog()
    {
        InitializeComponent();
        DeskApp.Utils.WindowDragHelper.Enable(this);
    }

    public static MessageBoxResult Show(
        Window? owner,
        string message,
        string title = "提示",
        MessageBoxButton buttons = MessageBoxButton.OK,
        MessageBoxImage icon = MessageBoxImage.Information)
    {
        var dlg = new PromptDialog
        {
            Owner = owner,
            Title = title
        };

        dlg.MessageText.Text = message;

        dlg.IconCtl.Symbol = icon switch
        {
            MessageBoxImage.Warning => SymbolRegular.Warning24,
            MessageBoxImage.Error => SymbolRegular.ErrorCircle24,
            MessageBoxImage.Question => SymbolRegular.Question24,
            _ => SymbolRegular.Info24
        };

        dlg.IconCtl.Foreground = (Brush)new BrushConverter().ConvertFrom(
            icon == MessageBoxImage.Error ? "#C42B1C"
                : icon == MessageBoxImage.Warning ? "#B26A00"
                : "#0F6CBD")!;

        dlg._mode = buttons switch
        {
            MessageBoxButton.YesNo => Mode.YesNo,
            MessageBoxButton.OKCancel => Mode.OkCancel,
            _ => Mode.Ok
        };

        if (dlg._mode == Mode.Ok)
        {
            dlg.BtnPrimary.Content = "确定";
            dlg.BtnSecondary.Visibility = Visibility.Collapsed;
        }
        else if (dlg._mode == Mode.YesNo)
        {
            dlg.BtnPrimary.Content = "是";
            dlg.BtnSecondary.Content = "否";
            dlg.BtnSecondary.Visibility = Visibility.Visible;
        }
        else
        {
            dlg.BtnPrimary.Content = "确定";
            dlg.BtnSecondary.Content = "取消";
            dlg.BtnSecondary.Visibility = Visibility.Visible;
        }

        dlg.ShowDialog();
        return dlg._result;
    }

    private void BtnPrimary_Click(object sender, RoutedEventArgs e)
    {
        _result = _mode == Mode.YesNo ? MessageBoxResult.Yes : MessageBoxResult.OK;
        DialogResult = true;
        Close();
    }

    private void BtnSecondary_Click(object sender, RoutedEventArgs e)
    {
        _result = _mode == Mode.YesNo ? MessageBoxResult.No : MessageBoxResult.Cancel;
        DialogResult = false;
        Close();
    }
}
