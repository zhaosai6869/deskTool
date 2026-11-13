using System;
using System.IO;
using System.Text.Json;

namespace DeskApp.Utils;

/// <summary>
/// 抖音下载模块配置。存 exe 同级 data\douyin_config.json；文件损坏时回退默认值。
/// 路线 A（分享页协议）无需登录 Cookie，清晰度固定 1080p（协议上限）。
/// </summary>
public class DouyinConfig
{
    public string DownloadDir { get; set; } = string.Empty;

    public string CacheDir { get; set; } = string.Empty;

    /// <summary>单次网络读写超时秒数（分享页与下载共用）。</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>网络错误 / 风控挑战页 / 429 / 5xx 的重试次数。</summary>
    public int Retries { get; set; } = 2;

    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "data", "douyin_config.json");

    public static DouyinConfig Load()
    {
        var config = new DouyinConfig
        {
            DownloadDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "抖音"),
            CacheDir = Path.Combine(AppContext.BaseDirectory, "data", "douyin_cache")
        };

        try
        {
            if (File.Exists(ConfigPath))
            {
                var saved = JsonSerializer.Deserialize<DouyinConfig>(File.ReadAllText(ConfigPath));
                if (saved is not null)
                {
                    if (!string.IsNullOrWhiteSpace(saved.DownloadDir))
                    {
                        config.DownloadDir = saved.DownloadDir;
                    }

                    if (!string.IsNullOrWhiteSpace(saved.CacheDir))
                    {
                        config.CacheDir = saved.CacheDir;
                    }

                    config.TimeoutSeconds = Math.Clamp(saved.TimeoutSeconds, 1, 3600);
                    config.Retries = Math.Clamp(saved.Retries, 0, 10);
                }
            }
        }
        catch
        {
            // 配置损坏回退默认值
        }

        return config;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch
        {
            // 配置保存失败不影响下载
        }
    }
}
