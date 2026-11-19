using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using DeskApp.Models;

namespace DeskApp.Utils;

/// <summary>单个网盘的登录凭据（加密存储）。</summary>
public sealed class PanCredential
{
    /// <summary>WebView2 里该站点域名下的全部 Cookie（序列化字符串 / header 形式）。</summary>
    public string Cookies { get; set; } = string.Empty;

    /// <summary>可选的 access_token（阿里/百度等 API 用）。</summary>
    public string AccessToken { get; set; } = string.Empty;

    /// <summary>凭据获取时间。</summary>
    public DateTime UpdatedAt { get; set; }

    public bool IsValid => !string.IsNullOrWhiteSpace(Cookies) || !string.IsNullOrWhiteSpace(AccessToken);
}

/// <summary>
/// 网盘下载模块配置。存 exe 同级 data\pan_config.json。
/// 凭据用 DPAPI 加密后写入，明文不落盘。
/// </summary>
public class PanConfig
{
    public string DownloadDir { get; set; } = string.Empty;

    /// <summary>并发线程数（8-32）。</summary>
    public int Threads { get; set; } = 16;

    /// <summary>单任务重试次数。</summary>
    public int Retries { get; set; } = 2;

    /// <summary>各网盘凭据（值为加密后的密文）。</summary>
    public Dictionary<string, PanCredentialEncrypted> Credentials { get; set; } = new();

    /// <summary>存储时的加密凭据结构。</summary>
    public sealed class PanCredentialEncrypted
    {
        public string Cookies { get; set; } = string.Empty;
        public string AccessToken { get; set; } = string.Empty;
        public DateTime UpdatedAt { get; set; }
    }

    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "data", "pan_config.json");

    public static string DefaultDownloadDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "网盘下载");

    public static PanConfig Load()
    {
        var config = new PanConfig { DownloadDir = DefaultDownloadDir };
        try
        {
            if (File.Exists(ConfigPath))
            {
                var saved = JsonSerializer.Deserialize<PanConfig>(File.ReadAllText(ConfigPath));
                if (saved is not null)
                {
                    config.DownloadDir = string.IsNullOrWhiteSpace(saved.DownloadDir)
                        ? DefaultDownloadDir : saved.DownloadDir;
                    config.Threads = Math.Clamp(saved.Threads, 1, 32);
                    config.Retries = Math.Clamp(saved.Retries, 0, 10);
                    config.Credentials = saved.Credentials ?? new();
                }
            }
        }
        catch
        {
            // 配置损坏回退默认
        }
        return config;
    }

    public void Save()
    {
        try
        {
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch
        {
            // 保存失败不影响本次使用
        }
    }

    /// <summary>读取某网盘凭据（解密）。</summary>
    public PanCredential GetCredential(PanSite site)
    {
        var key = site.ToString();
        if (!Credentials.TryGetValue(key, out var enc)) return new PanCredential();
        return new PanCredential
        {
            Cookies = RdpCrypto.Decrypt(enc.Cookies) ?? string.Empty,
            AccessToken = RdpCrypto.Decrypt(enc.AccessToken) ?? string.Empty,
            UpdatedAt = enc.UpdatedAt
        };
    }

    /// <summary>保存某网盘凭据（加密）。</summary>
    public void SetCredential(PanSite site, PanCredential cred)
    {
        Credentials[site.ToString()] = new PanCredentialEncrypted
        {
            Cookies = RdpCrypto.Encrypt(cred.Cookies) ?? string.Empty,
            AccessToken = RdpCrypto.Encrypt(cred.AccessToken) ?? string.Empty,
            UpdatedAt = DateTime.Now
        };
        Save();
    }

    public void ClearCredential(PanSite site)
    {
        Credentials.Remove(site.ToString());
        Save();
    }
}
