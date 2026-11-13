using System.ComponentModel;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace DeskApp.Models;

/// <summary>
/// Win 远程（RDP）连接记录。密码以 DPAPI 加密后的十六进制串存储（见 Utils/RdpCrypto）。
/// </summary>
public class RdpRecord : INotifyPropertyChanged
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 3389;

    public string UserName { get; set; } = string.Empty;

    /// <summary>DPAPI（LocalMachine）加密后的密码，十六进制字符串；为空表示连接时手动输入。</summary>
    public string PasswordEncrypted { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;

    /// <summary>全屏连接（screen mode id:2），默认开启</summary>
    public bool FullScreen { get; set; } = true;

    /// <summary>管理员模式（mstsc /admin）</summary>
    public bool AdminMode { get; set; }

    /// <summary>连接前询问凭据（mstsc /prompt，且不在 .rdp 中写入密码）</summary>
    public bool PromptCredentials { get; set; }

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
