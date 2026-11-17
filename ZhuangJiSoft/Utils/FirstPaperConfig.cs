using System.IO;
using System.Text.Json;

namespace DeskApp.Utils;

/// <summary>
/// 第一试卷下载模块配置。存 exe 同级 data\firstpaper_config.json，
/// 损坏时回退默认值，不影响主程序启动。
/// </summary>
public class FirstPaperConfig
{
    public string DownloadDir { get; set; } = string.Empty;

    public string SubjectKey { get; set; } = "sjyw";

    public string Grade { get; set; } = "一年级";

    /// <summary>支持 [版本] [标题] [学科] [年级] [日期] 占位符。</summary>
    public string NameTemplate { get; set; } = "[版本]-[标题]";

    public int TimeoutSeconds { get; set; } = 60;

    public int Retries { get; set; } = 2;

    /// <summary>各「学科+年级」题库最近一次扫描时间。</summary>
    public Dictionary<string, DateTime> UpdateTimes { get; set; } = new();

    private static string ConfigPath => Path.Combine(AppContext.BaseDirectory, "data", "firstpaper_config.json");

    public static FirstPaperConfig Load()
    {
        var config = new FirstPaperConfig
        {
            DownloadDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "第一试卷")
        };

        try
        {
            if (File.Exists(ConfigPath))
            {
                var saved = JsonSerializer.Deserialize<FirstPaperConfig>(File.ReadAllText(ConfigPath));
                if (saved is not null)
                {
                    if (!string.IsNullOrWhiteSpace(saved.DownloadDir))
                    {
                        config.DownloadDir = saved.DownloadDir;
                    }

                    if (!string.IsNullOrWhiteSpace(saved.SubjectKey))
                    {
                        config.SubjectKey = saved.SubjectKey;
                    }

                    if (!string.IsNullOrWhiteSpace(saved.Grade))
                    {
                        config.Grade = saved.Grade;
                    }

                    if (!string.IsNullOrWhiteSpace(saved.NameTemplate))
                    {
                        config.NameTemplate = saved.NameTemplate;
                    }

                    config.TimeoutSeconds = Math.Clamp(saved.TimeoutSeconds, 1, 3600);
                    config.Retries = Math.Clamp(saved.Retries, 0, 10);
                    config.UpdateTimes = saved.UpdateTimes ?? new Dictionary<string, DateTime>();
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
