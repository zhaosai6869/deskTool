using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DeskApp.Models;

/// <summary>连接协议。SSH 走内嵌终端 + SFTP 文件管理；FTP 走 FluentFTP 文件管理。</summary>
public static class SshProtocol
{
    public const string Ssh = "SSH";
    public const string Ftp = "FTP";
}

/// <summary>
/// SSH / FTP 连接记录。密码以 DPAPI 加密后的十六进制串存储（见 Utils/RdpCrypto，通用凭据加密）。
/// </summary>
public class SshRecord : INotifyPropertyChanged
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 22;

    public string UserName { get; set; } = string.Empty;

    /// <summary>SshProtocol.Ssh / SshProtocol.Ftp</summary>
    public string Protocol { get; set; } = SshProtocol.Ssh;

    /// <summary>DPAPI（LocalMachine）加密后的密码，十六进制字符串；为空表示连接时手动输入。</summary>
    public string PasswordEncrypted { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;

    [NotMapped]
    public bool IsFtp => Protocol == SshProtocol.Ftp;

    [NotMapped]
    public int DefaultPort => IsFtp ? 21 : 22;

    [NotMapped]
    public string PasswordMask => string.IsNullOrEmpty(PasswordEncrypted) ? "未设置" : "已设置";

    /// <summary>延迟（毫秒），由 Ping 测得；null 表示未连通。不入库。</summary>
    private int? _pingMs;
    [NotMapped]
    public int? PingMs
    {
        get => _pingMs;
        set
        {
            if (_pingMs != value)
            {
                _pingMs = value;
                OnPropertyChanged(nameof(PingMs));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
