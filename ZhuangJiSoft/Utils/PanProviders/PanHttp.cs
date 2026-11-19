using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using DeskApp.Models;

namespace DeskApp.Utils.PanProviders;

/// <summary>网盘 HTTP 请求封装：统一 UA、Cookie、超时。各 Provider 复用。</summary>
public static class PanHttp
{
    public const string DefaultUa =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/131.0.0.0 Safari/537.36";

    public static HttpClient CreateClient(PanCredential? cred, string? ua = null, int timeoutSec = 30)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,
            UseCookies = false // 我们手动带 Cookie header，避免 WebView2 cookie 与 handler 冲突
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(timeoutSec)
        };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(ua ?? DefaultUa);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json, text/plain, */*");
        if (cred is not null && !string.IsNullOrWhiteSpace(cred.Cookies))
        {
            client.DefaultRequestHeaders.Add("Cookie", cred.Cookies);
        }
        return client;
    }

    public static async Task<string> GetStringAsync(HttpClient client, string url,
        Dictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        if (headers is not null)
        {
            foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }
        using var resp = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        resp.EnsureSuccessStatusCode();
        return Decode(bytes);
    }

    public static async Task<string> PostJsonAsync(HttpClient client, string url, string json,
        Dictionary<string, string>? headers = null, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        if (headers is not null)
        {
            foreach (var kv in headers) req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
        }
        using var resp = await client.SendAsync(req, ct);
        var bytes = await resp.Content.ReadAsByteArrayAsync(ct);
        resp.EnsureSuccessStatusCode();
        return Decode(bytes);
    }

    /// <summary>编码自适应：BOM / UTF-8 优先，回退 GB18030。</summary>
    private static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        try
        {
            // 严格 UTF-8，失败则回退 GB18030
            return new UTF8Encoding(false, true).GetString(bytes);
        }
        catch
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding("GB18030").GetString(bytes);
            }
            catch
            {
                return Encoding.UTF8.GetString(bytes);
            }
        }
    }
}
