using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DeskApp.Utils;

/// <summary>
/// 腾讯元宝登录凭据（参考 wx-downloader auth.py）。分享链接解析依赖元宝 Cookie
/// （至少含 hy_user / hy_token），由 YuanbaoLoginWindow 用 WebView2 登录后写入这里。
/// Cookie 用 DPAPI 加密落盘，明文只出现在内存与请求头里。
/// </summary>
public class YuanbaoAuth
{
    public const string Origin = "https://yuanbao.tencent.com";

    public const string ParseUrl = Origin + "/api/weixin/get_parse_result";

    /// <summary>解析接口要求的附加请求头白名单（authorization 等在元宝页面请求中出现时才带上）。</summary>
    public static readonly string[] ExtraHeaderNames =
    {
        "authorization", "t-userid", "x-id", "x-device-id", "x-hy92", "x-hy93"
    };

    private static string AuthPath => Path.Combine(AppContext.BaseDirectory, "data", "yuanbao_auth.json");

    /// <summary>拼装发往解析接口的 Cookie 头：域名匹配元宝、未过期、path 是接口路径前缀。</summary>
    public static string BuildCookieHeader(IEnumerable<(string Name, string Value, string Path)> cookies)
    {
        var apiPath = new Uri(ParseUrl).AbsolutePath;
        return string.Join("; ", cookies
            .Where(c => !string.IsNullOrEmpty(c.Name) && !string.IsNullOrEmpty(c.Value))
            .Where(c => apiPath.StartsWith(c.Path, StringComparison.OrdinalIgnoreCase))
            .Select(c => $"{c.Name}={c.Value}"));
    }

    /// <summary>读取已保存凭据；Cookie 头里缺 hy_user / hy_token 时返回 null。</summary>
    public static YuanbaoAuth? Load()
    {
        try
        {
            if (!File.Exists(AuthPath))
            {
                return null;
            }

            var doc = JsonSerializer.Deserialize<AuthFile>(File.ReadAllText(AuthPath));
            if (doc is null || string.IsNullOrEmpty(doc.CookieEncrypted))
            {
                return null;
            }

            var cookie = RdpCrypto.Decrypt(doc.CookieEncrypted);
            if (string.IsNullOrEmpty(cookie))
            {
                return null;
            }

            var names = cookie.Split(';').Select(p => p.Split('=', 2)[0].Trim());
            if (!names.Contains("hy_user") || !names.Contains("hy_token"))
            {
                return null;
            }

            return new YuanbaoAuth
            {
                Cookie = cookie,
                UserAgent = doc.UserAgent,
                SavedAt = doc.SavedAt,
                ExtraHeaders = doc.Headers ?? new Dictionary<string, string>()
            };
        }
        catch
        {
            return null;
        }
    }

    public static void Save(string cookie, string userAgent, Dictionary<string, string> headers)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(AuthPath)!);
            var doc = new AuthFile
            {
                CookieEncrypted = RdpCrypto.Encrypt(cookie) ?? string.Empty,
                UserAgent = userAgent,
                SavedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"),
                Headers = headers
            };
            File.WriteAllText(AuthPath, JsonSerializer.Serialize(doc, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch
        {
            // 保存失败由调用方按登录失败处理
        }
    }

    public static void Clear()
    {
        try
        {
            File.Delete(AuthPath);
        }
        catch
        {
            // 文件不存在或被占用
        }
    }

    /// <summary>拼好的 Cookie 头（已过滤域名/有效期/path）。</summary>
    public string Cookie { get; private init; } = string.Empty;

    public string UserAgent { get; private init; } = string.Empty;

    public string SavedAt { get; private init; } = string.Empty;

    public Dictionary<string, string> ExtraHeaders { get; private init; } = new();

    private class AuthFile
    {
        public string CookieEncrypted { get; set; } = string.Empty;

        public string UserAgent { get; set; } = string.Empty;

        public string SavedAt { get; set; } = string.Empty;

        public Dictionary<string, string>? Headers { get; set; }
    }
}
