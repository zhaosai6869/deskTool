using System;
using System.IO;
using System.Text.Json;

namespace DeskApp.Utils;

/// <summary>
/// 番茄小说下载模块配置。存 exe 同级 data\fanqie_config.json；损坏时回退默认值。
/// 目录/正文页纯 HttpClient 直连，搜索用 WebView2 过滑块。
/// </summary>
public class FanqieConfig
{
    public string DownloadDir { get; set; } = string.Empty;

    public string CacheDir { get; set; } = string.Empty;

    /// <summary>单次网络读写超时秒数。</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>单章网络错误的重试次数。</summary>
    public int Retries { get; set; } = 2;

    /// <summary>相邻章节之间的延时毫秒（防风控，默认 120ms）。</summary>
    public int ChapterDelayMs { get; set; } = 120;

    /// <summary>true=整本合并为一个 TXT；false=每章一个 TXT 存入以书名命名的子目录。</summary>
    public bool SingleFile { get; set; } = true;

    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "data", "fanqie_config.json");

    public static FanqieConfig Load()
    {
        var config = new FanqieConfig
        {
            DownloadDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "番茄小说"),
            CacheDir = Path.Combine(AppContext.BaseDirectory, "data", "fanqie_cache")
        };

        try
        {
            if (File.Exists(ConfigPath))
            {
                var saved = JsonSerializer.Deserialize<FanqieConfig>(File.ReadAllText(ConfigPath));
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
                    config.ChapterDelayMs = Math.Clamp(saved.ChapterDelayMs, 0, 5000);
                    config.SingleFile = saved.SingleFile;
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
