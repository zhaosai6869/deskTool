using System.Linq;
using System.Windows;
using DeskApp.Models;
using DeskApp.Utils;

namespace DeskApp;

/// <summary>
/// 向日葵新增/修改弹窗。确定后通过 Record 回传完整记录。
/// 访问密码填写则 DPAPI 加密保存；编辑时留空保留原加密值；留空不影响识别码复制。
/// </summary>
public partial class SunflowerEditDialog : Window
{
    private readonly SunflowerRecord? _existing;

    public SunflowerRecord Record { get; private set; } = new();

    public SunflowerEditDialog()
    {
        InitializeComponent();
        DeskApp.Utils.WindowDragHelper.Enable(this);
    }

    public SunflowerEditDialog(SunflowerRecord existing) : this()
    {
        _existing = existing;
        Record = existing;
        NameBox.Text = existing.Name;
        CodeBox.Text = existing.DeviceCode;
        NotesBox.Text = existing.Notes;
        PwdBox.Text = RdpCrypto.Decrypt(existing.PasswordEncrypted) ?? string.Empty;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var name = NameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            PromptDialog.Show(this, "名称不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // 识别码统一去掉所有空白，复制与显示都干净
        var code = string.Concat(CodeBox.Text.Where(c => !char.IsWhiteSpace(c)));
        if (string.IsNullOrEmpty(code))
        {
            PromptDialog.Show(this, "识别码不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var rec = _existing ?? new SunflowerRecord();
        rec.Name = name;
        rec.DeviceCode = code;
        rec.Notes = NotesBox.Text.Trim();

        var pw = PwdBox.Text;
        if (!string.IsNullOrEmpty(pw))
        {
            rec.PasswordEncrypted = RdpCrypto.Encrypt(pw) ?? string.Empty;
        }
        else if (_existing is null)
        {
            rec.PasswordEncrypted = string.Empty;
        }
        // 编辑且未填密码：保留 _existing 中现有的加密值

        Record = rec;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
