using System.Linq;
using System.Windows;
using DeskApp.Models;
using DeskApp.Utils;

namespace DeskApp;

/// <summary>
/// ToDesk 新增/修改弹窗。确定后通过 Record 回传完整记录。
/// 密码填写则 DPAPI 加密保存；编辑时留空保留原加密值；新建留空则连接时由客户端询问临时密码。
/// </summary>
public partial class ToDeskEditDialog : Window
{
    private readonly ToDeskRecord? _existing;

    public ToDeskRecord Record { get; private set; } = new();

    public ToDeskEditDialog()
    {
        InitializeComponent();
        DeskApp.Utils.WindowDragHelper.Enable(this);
    }

    public ToDeskEditDialog(ToDeskRecord existing) : this()
    {
        _existing = existing;
        Record = existing;
        NameBox.Text = existing.Name;
        IdBox.Text = existing.DeviceId;
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

        // 参考项目要求设备代码不带空格；这里保存时就去净，表格与命令行都干净
        var id = string.Concat(IdBox.Text.Where(c => !char.IsWhiteSpace(c)));
        if (string.IsNullOrEmpty(id))
        {
            PromptDialog.Show(this, "设备代码不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var rec = _existing ?? new ToDeskRecord();
        rec.Name = name;
        rec.DeviceId = id;
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
