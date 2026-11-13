using System.ComponentModel.DataAnnotations.Schema;

namespace DeskApp.Models;

/// <summary>
/// 向日葵（AweSun）连接记录。采用「免费半自动」方案：保存识别码 + 无人值守访问密码，
/// 双击时拉起客户端并把识别码送到剪贴板，密码由右键菜单复制，在客户端里粘贴完成连接。
/// 不存 4 位验证码（临时值、会定期刷新），也不做 Ping（识别码不是 IP）。
/// 密码用 DPAPI（LocalMachine）加密成十六进制串存储，复用 Utils/RdpCrypto。
/// </summary>
public class SunflowerRecord
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    /// <summary>被控端显示的识别码 / 快码（通常 9 位数字，保存时去掉所有空白）。</summary>
    public string DeviceCode { get; set; } = string.Empty;

    public string PasswordEncrypted { get; set; } = string.Empty;

    public string Notes { get; set; } = string.Empty;

    [NotMapped]
    public string PasswordMask => string.IsNullOrEmpty(PasswordEncrypted) ? "未设置" : "已设置";
}
