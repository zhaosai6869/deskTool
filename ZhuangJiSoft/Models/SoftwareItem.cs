namespace DeskApp.Models;

/// <summary>
/// 装机软件条目：软件名称、下载地址。
/// </summary>
public class SoftwareItem
{
    public int Id { get; set; }

    public string Name { get; set; } = string.Empty;

    public string DownloadUrl { get; set; } = string.Empty;

    /// <summary>
    /// 兼容字段：旧版数据库 Items 表此列为 NOT NULL 且无默认值，
    /// 为保证已有 app.db 能继续新增记录而保留，业务上已不再使用（排序功能已移除）。
    /// </summary>
    public int SortOrder { get; set; }
}
