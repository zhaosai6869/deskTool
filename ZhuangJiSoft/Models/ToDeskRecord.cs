using System.ComponentModel.DataAnnotations.Schema;

namespace DeskApp.Models;

/// <summary>
/// ToDesk 连接记录。参考 MstscManager：设备代码填在 ip 栏、临时/连接密码填在 user_pass，
/// 连接时拼成 ToDesk.exe -control -id &lt;设备代码&gt; -passwd &lt;密码&gt;。
/// 密码用 DPAPI（LocalMachine）加密成十六进制串存储，复用 Utils/RdpCrypto。
/// 与 RDP 记录不同：ToDesk 没有主机 IP，因此不做 Ping 探测。
/// </summary>
public class ToDeskRecord
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>ToDesk 设备代码（纯数字，连接时去掉所有空白字符）。</summary>
    public string DeviceId { get; set; } = string.Empty;

    public string PasswordEncrypted { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;

    [NotMapped]
    public string PasswordMask => string.IsNullOrEmpty(PasswordEncrypted) ? "未设置" : "已设置";
}
