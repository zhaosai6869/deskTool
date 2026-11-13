using System.Net.NetworkInformation;

namespace DeskApp.Utils;

/// <summary>
/// 主机延迟探测。Win 远程（RDP）与 SSH/FTP 模块共用同一套口径：
/// ICMP Echo，2 秒超时，成功返回往返毫秒数，超时/不可达/异常返回 null（界面显示"未连通"）。
/// </summary>
public static class NetworkPing
{
    private const int TimeoutMs = 2000;

    public static int? Measure(string? host)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return null;
        }

        try
        {
            using var pinger = new Ping();
            var reply = pinger.Send(host, TimeoutMs);
            return reply?.Status == IPStatus.Success ? (int?)reply.RoundtripTime : null;
        }
        catch
        {
            return null;
        }
    }
}
