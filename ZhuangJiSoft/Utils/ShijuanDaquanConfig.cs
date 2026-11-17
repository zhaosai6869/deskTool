using System.IO;
using System.Text.Json;

namespace DeskApp.Utils;

/// <summary>
/// 试卷大全下载模块配置：存 exe 同级 data\shijuandaquan_config.json。
/// 账号密码按项目现有习惯明文保存在本机配置文件，仅用于向站点登录。
/// </summary>
public class ShijuanDaquanConfig
{
    public string DownloadDir { get; set; } = string.Empty;

    /// <summary>支持 [标题] [版本] [年级] [类型] [年份] [日期] [编号] 占位符。</summary>
    public string NameTemplate { get; set; } = "[标题]";

    public string Username { get; set; } = string.Empty;

    public string Password { get; set; } = string.Empty;

    /// <summary>进入模块时用已保存的账号自动登录一次。</summary>
    public bool AutoLogin { get; set; } = true;

    /// <summary>本地按天下载计数，仅作站点配额读取失败时的兜底。</summary>
    public Dictionary<string, int> DailyUsage { get; set; } = new();

    public int LastQuotaTotal { get; set; }

    public int LastQuotaRemaining { get; set; }

    public string LastQuotaDate { get; set; } = string.Empty;

    private static string ConfigPath =>
        Path.Combine(AppContext.BaseDirectory, "data", "shijuandaquan_config.json");

    public static ShijuanDaquanConfig Load()
    {
        var config = new ShijuanDaquanConfig
        {
            DownloadDir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "试卷大全")
        };

        try
        {
            if (!File.Exists(ConfigPath))
            {
                return config;
            }

            var saved = JsonSerializer.Deserialize<ShijuanDaquanConfig>(File.ReadAllText(ConfigPath));
            if (saved is null)
            {
                return config;
            }

            if (!string.IsNullOrWhiteSpace(saved.DownloadDir))
            {
                config.DownloadDir = saved.DownloadDir;
            }

            if (!string.IsNullOrWhiteSpace(saved.NameTemplate))
            {
                config.NameTemplate = saved.NameTemplate;
            }

            config.Username = saved.Username ?? string.Empty;
            config.Password = saved.Password ?? string.Empty;
            config.AutoLogin = saved.AutoLogin;
            config.DailyUsage = saved.DailyUsage ?? new Dictionary<string, int>();
            config.LastQuotaTotal = saved.LastQuotaTotal;
            config.LastQuotaRemaining = saved.LastQuotaRemaining;
            config.LastQuotaDate = saved.LastQuotaDate ?? string.Empty;
        }
        catch
        {
            // 配置损坏回退默认值，不影响主程序
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

    public int UsedToday()
    {
        var key = DateTime.Now.ToString("yyyy-MM-dd");
        return DailyUsage.TryGetValue(key, out var used) ? used : 0;
    }

    public void AddUsage(int count = 1)
    {
        var key = DateTime.Now.ToString("yyyy-MM-dd");
        DailyUsage[key] = UsedToday() + count;
        // 只保留最近 7 天，避免配置无限增长
        foreach (var old in DailyUsage.Keys
                     .Where(k => DateTime.TryParse(k, out var d) && (DateTime.Now - d).TotalDays > 7)
                     .ToList())
        {
            DailyUsage.Remove(old);
        }
    }
}
