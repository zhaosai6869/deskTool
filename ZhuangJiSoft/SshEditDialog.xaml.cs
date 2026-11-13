using System.Windows;
using System.Windows.Controls;
using DeskApp.Models;
using DeskApp.Utils;

namespace DeskApp;

/// <summary>
/// SSH / FTP 连接新增与编辑弹窗。密码填写则 DPAPI 加密保存；编辑留空保留原加密值。
/// 协议切换时自动把端口改为该协议默认值（SSH 22 / FTP 21）。
/// </summary>
public partial class SshEditDialog : Window
{
    private readonly SshRecord? _existing;
    private bool _loading;

    public SshRecord Record { get; private set; } = new() { Port = 22, Protocol = SshProtocol.Ssh };

    public SshEditDialog()
    {
        InitializeComponent();
        DeskApp.Utils.WindowDragHelper.Enable(this);
        _loading = true;
        ProtocolBox.SelectedIndex = 0;
        PortBox.Text = "22";
        _loading = false;
    }

    public SshEditDialog(SshRecord existing) : this()
    {
        _existing = existing;
        Record = existing;
        _loading = true;
        NameBox.Text = existing.Name;
        HostBox.Text = existing.Host;
        PortBox.Text = existing.Port.ToString();
        UserBox.Text = existing.UserName;
        NotesBox.Text = existing.Notes;
        PwdBox.Text = RdpCrypto.Decrypt(existing.PasswordEncrypted) ?? string.Empty;
        ProtocolBox.SelectedIndex = existing.IsFtp ? 1 : 0;
        _loading = false;
    }

    private string CurrentProtocol =>
        ProtocolBox.SelectedIndex == 1 ? SshProtocol.Ftp : SshProtocol.Ssh;

    private void ProtocolBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        PortBox.Text = CurrentProtocol == SshProtocol.Ftp ? "21" : "22";
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

        var rec = _existing ?? new SshRecord();
        rec.Name = name;
        rec.Host = host;
        rec.Port = port;
        rec.UserName = UserBox.Text.Trim();
        rec.Notes = NotesBox.Text.Trim();
        rec.Protocol = CurrentProtocol;

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
