using System;
using System.IO;
using System.Text.Json;

namespace DeskApp.Utils;

/// <summary>
/// 漫画下载模块配置。存 exe 同级 data\comic_config.json；文件损坏时回退默认值。
/// 各站点解析逻辑写死在 Provider 中，这里只保留下载相关参数与登录凭据。
/// </summary>
public class ComicConfig
{
    public string DownloadDir { get; set; } = string.Empty;

    /// <summary>B 漫登录凭据（Cookie 里的 SESSDATA 值）。B 漫接口已关闭匿名访问，必须提供。</summary>
    public string BiliSessData { get; set; } = string.Empty;

    /// <summary>单次网络读写超时秒数。</summary>
    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>网络错误 / 5xx 的重试次数。</summary>
    public int Retries { get; set; } = 2;

    /// <summary>请求间隔毫秒，避免对站点压力过大。</summary>
    public int DelayMs { get; set; } = 120;

    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "data", "comic_config.json");

    /// <summary>与其他下载模块一致：默认保存到 用户下载目录\漫画下载。</summary>
    public static string DefaultDownloadDir => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "漫画下载");

    public static ComicConfig Load()
    {
        var config = new ComicConfig { DownloadDir = DefaultDownloadDir };
        try
        {
            if (File.Exists(ConfigPath))
            {
                var saved = JsonSerializer.Deserialize<ComicConfig>(File.ReadAllText(ConfigPath));
                if (saved is not null)
                {
                    config.DownloadDir = string.IsNullOrWhiteSpace(saved.DownloadDir)
                        ? DefaultDownloadDir
                        : saved.DownloadDir;
                    config.BiliSessData = (saved.BiliSessData ?? string.Empty).Trim();
                    config.TimeoutSeconds = Math.Clamp(saved.TimeoutSeconds, 1, 3600);
                    config.Retries = Math.Clamp(saved.Retries, 0, 10);
                    config.DelayMs = Math.Clamp(saved.DelayMs, 0, 10000);
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
            var dir = Path.GetDirectoryName(ConfigPath);
            if (!string.IsNullOrEmpty(dir))
            {
                Directory.CreateDirectory(dir);
            }
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, new JsonSerializerOptions
            {
                WriteIndented = true
            }));
        }
        catch
        {
            // 配置保存失败不影响本次使用
        }
    }
}
