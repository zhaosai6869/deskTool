using DeskApp.Models;
using DeskApp.Utils;

namespace DeskApp.Models;

/// <summary>
/// 解密后的连接快照：从数据库记录生成，交给终端窗口 / 文件管理窗口使用，
/// 避免把加密串和 EF 实体传进 UI 层。
/// </summary>
public sealed class SshConnectionInfo
{
    public int Id { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Host { get; init; } = string.Empty;
    public int Port { get; init; } = 22;
    public string UserName { get; init; } = string.Empty;
    public string Password { get; init; } = string.Empty;
    public string Protocol { get; init; } = SshProtocol.Ssh;

    public bool IsFtp => Protocol == SshProtocol.Ftp;

    public static SshConnectionInfo From(SshRecord r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Host = r.Host,
        Port = r.Port,
        UserName = r.UserName,
        Password = RdpCrypto.Decrypt(r.PasswordEncrypted) ?? string.Empty,
        Protocol = r.Protocol
    };
}
