using System;
using System.IO;
using System.Text.Json;

namespace DeskApp.Utils;

/// <summary>
/// 视频号下载模块配置（参考 wx-downloader config：download_dir / cache_dir / quality / timeout / retries）。
/// 存 exe 同级 data\sph_config.json；文件损坏时回退默认值。
/// </summary>
public class SphConfig
{
    public string DownloadDir { get; set; } = string.Empty;

    public string CacheDir { get; set; } = string.Empty;

    /// <summary>首选编码：界面已固定 h264（H.264 缺失时自动回退 H.265）。</summary>
    public string Quality { get; set; } = "h264";

    /// <summary>单次网络读写超时秒数。</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>网络错误 / 429 / 5xx 的重试次数。</summary>
    public int Retries { get; set; } = 2;

    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "data", "sph_config.json");

    public static SphConfig Load()
    {
        var config = new SphConfig
        {
            DownloadDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "视频号"),
            CacheDir = Path.Combine(AppContext.BaseDirectory, "data", "sph_cache")
        };

        try
        {
            if (File.Exists(ConfigPath))
            {
                var saved = JsonSerializer.Deserialize<SphConfig>(File.ReadAllText(ConfigPath));
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

                    // 编码切换按钮已移除：固定 H.264 优先（缺失时自动回退 H.265），忽略旧配置里的值
                    config.Quality = "h264";
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
