using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DeskApp.Utils;

/// <summary>智慧教育平台模块的本地配置：Token、保存目录、命名规则。仅存本机。</summary>
public sealed class SmartEduConfig
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    public string DownloadDir { get; set; } = string.Empty;
    public string NameTemplate { get; set; } = "[标题]";

    /// <summary>从登录凭据 JSON 解析出的三项；不上传任何服务器。</summary>
    public string AccessToken { get; set; } = string.Empty;
    public string MacKey { get; set; } = string.Empty;
    public long TokenDiff { get; set; }

    /// <summary>标签树与教材清单的拉取时间，仅作界面提示。</summary>
    public string LastCatalogRefresh { get; set; } = string.Empty;

    public static string ConfigPath => Path.Combine(
        AppContext.BaseDirectory, "data", "smartedu_config.json");

    public static SmartEduConfig Load()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                return new SmartEduConfig();
            }

            return JsonSerializer.Deserialize<SmartEduConfig>(
                       File.ReadAllText(ConfigPath), JsonOptions) ?? new SmartEduConfig();
        }
        catch
        {
            return new SmartEduConfig();
        }
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
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch
        {
            // 配置保存失败不影响本次使用
        }
    }

    public bool HasToken => !string.IsNullOrWhiteSpace(AccessToken) &&
                            !string.IsNullOrWhiteSpace(MacKey);

    /// <summary>界面展示用的 Token JSON，方便用户复制核对。</summary>
    public string TokenJson => JsonSerializer.Serialize(new
    {
        access_token = AccessToken,
        mac_key = MacKey,
        diff = TokenDiff
    });
}
