using System.Windows;
using DeskApp.Models;
using DeskApp.Utils;

namespace DeskApp;

/// <summary>
/// Win 远程新增/修改弹窗。确定后通过 Record 属性回传完整 RdpRecord。
/// 密码：填写则加密保存；编辑时留空则保留原加密值；新建时留空表示连接时手动输入。
/// </summary>
public partial class WinRemoteEditDialog : Window
{
    private readonly RdpRecord? _existing;

    public RdpRecord Record { get; private set; } = new() { Port = 3389, UserName = "Administrator" };

    public WinRemoteEditDialog()
    {
        InitializeComponent();
        DeskApp.Utils.WindowDragHelper.Enable(this);
        // 新建时用户名默认 Administrator
        UserBox.Text = "Administrator";
    }

    public WinRemoteEditDialog(RdpRecord existing) : this()
    {
        _existing = existing;
        Record = existing;
        NameBox.Text = existing.Name;
        HostBox.Text = existing.Host;
        PortBox.Text = existing.Port.ToString();
        UserBox.Text = existing.UserName;
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

        var host = HostBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(host))
        {
            PromptDialog.Show(this, "主机/IP 不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        if (!int.TryParse(PortBox.Text.Trim(), out int port) || port <= 0 || port > 65535)
        {
            PromptDialog.Show(this, "端口需为 1-65535 之间的整数。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var rec = _existing ?? new RdpRecord();
        rec.Name = name;
        rec.Host = host;
        rec.Port = port;
        rec.UserName = UserBox.Text.Trim();
        rec.Notes = NotesBox.Text.Trim();
        // 全屏为默认行为（已移除复选框）；管理员模式与询问凭据不再提供开关，保持关闭
        rec.FullScreen = true;
        rec.AdminMode = false;
        rec.PromptCredentials = false;

        var pw = PwdBox.Text;
        if (!string.IsNullOrEmpty(pw))
        {
            rec.PasswordEncrypted = RdpCrypto.Encrypt(pw) ?? string.Empty;
        }
        else if (_existing is null)
        {
            rec.PasswordEncrypted = string.Empty;
        }
        // 编辑且未填密码：保留 _existing 中现有（已通过 Record = existing 带入）的加密值

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
