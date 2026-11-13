using System.Security.Cryptography;
using System.Text;

namespace DeskApp.Utils;

/// <summary>
/// RDP 密码加解密。参考 MstscManager 的 RDPcrypt：
/// 使用 DPAPI（DataProtectionScope.LocalMachine）加密，结果转十六进制后写入 .rdp 的
/// "password 51:b:" 字段，mstsc 即可在同一台机器上解密自动登录。
/// </summary>
public static class RdpCrypto
{
    public static string? Encrypt(string? plain)
    {
        if (string.IsNullOrEmpty(plain))
        {
            return string.Empty;
        }

        byte[] secret = Encoding.Unicode.GetBytes(plain);
        byte[] encrypted = ProtectedData.Protect(secret, null, DataProtectionScope.LocalMachine);
        var sb = new StringBuilder(encrypted.Length * 2);
        foreach (byte b in encrypted)
        {
            sb.Append(b.ToString("X2"));
        }

        return sb.ToString();
    }

    public static string? Decrypt(string? encryptedHex)
    {
        if (string.IsNullOrEmpty(encryptedHex))
        {
            return string.Empty;
        }

        try
        {
            byte[] data = new byte[encryptedHex.Length / 2];
            for (int i = 0; i < data.Length; i++)
            {
                data[i] = Convert.ToByte(encryptedHex.Substring(i * 2, 2), 16);
            }

            byte[] decrypted = ProtectedData.Unprotect(data, null, DataProtectionScope.LocalMachine);
            return Encoding.Unicode.GetString(decrypted);
        }
        catch
        {
            // 旧库/跨机器留下的损坏密文：返回空串，不阻断编辑弹窗与连接信息展示
            return string.Empty;
        }
    }
}
