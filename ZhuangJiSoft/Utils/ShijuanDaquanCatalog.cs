using System.Text;

namespace DeskApp.Utils;

/// <summary>试卷大全 /list 页面的一个筛选项：显示名 + 站点参数值。</summary>
public sealed record SjOption(string Name, int Value)
{
    // ComboBox 的选中框可能走 ToString()，重写后无论用不用 DisplayMemberPath 都显示中文名
    public override string ToString() => Name;
}

/// <summary>排序项：显示名 + 站点 sort 参数。</summary>
public sealed record SjSortOption(string Name, string Key)
{
    public override string ToString() => Name;
}

/// <summary>一次列表请求的完整条件。</summary>
public sealed class SjQuery
{
    public int Sid { get; set; }
    public int Grade { get; set; }
    public int Term { get; set; }
    public int Region { get; set; }
    public int Year { get; set; }
    public int Level { get; set; } = -1;
    public int Version { get; set; }
    public int Type { get; set; }
    public string Sort { get; set; } = "comprehensive";
    public string Keyword { get; set; } = string.Empty;
    public int Page { get; set; } = 1;
}

/// <summary>
/// 试卷大全（sjdaquan.com）筛选映射，参数值 2026-11-17 从 /list 页面实测提取。
/// 站点是服务端渲染，筛选全部走 GET 查询串，无需登录或签名。
/// </summary>
public static class ShijuanDaquanCatalog
{
    public const string BaseUrl = "https://www.sjdaquan.com";

    public static readonly SjOption[] Subjects =
    {
        new("全部", 0), new("语文", 1), new("数学", 2), new("英语", 3), new("物理", 4),
        new("化学", 5), new("生物", 6), new("政治", 7), new("历史", 8), new("地理", 9),
        new("科学", 10), new("信息技术", 11)
    };

    public static readonly SjOption[] Grades =
    {
        new("全部", 0), new("小学一年级", 12), new("小学二年级", 13), new("小学三年级", 14),
        new("小学四年级", 15), new("小学五年级", 16), new("小学六年级", 17),
        new("初一", 18), new("初二", 19), new("初三", 20),
        new("高一", 21), new("高二", 22), new("高三", 23)
    };

    public static readonly SjOption[] Terms =
    {
        new("全部", 0), new("上学期", 83), new("下学期", 84), new("上下学期通用", 87)
    };

    public static readonly SjOption[] Regions =
    {
        new("全部地区", 0), new("全国（含未标注）", 43), new("北京", 44), new("上海", 45),
        new("天津", 46), new("重庆", 47), new("广东", 48), new("江苏", 49), new("浙江", 50),
        new("山东", 51), new("河南", 52), new("四川", 53), new("湖北", 54), new("湖南", 55),
        new("福建", 56), new("河北", 57), new("陕西", 58), new("辽宁", 59), new("山西", 62),
        new("内蒙古", 63), new("吉林", 65), new("黑龙江", 66), new("安徽", 67), new("江西", 68),
        new("广西", 69), new("海南", 70), new("贵州", 71), new("云南", 72), new("西藏", 73),
        new("甘肃", 75), new("青海", 76), new("宁夏", 77), new("新疆", 78), new("其他省份", 60)
    };

    public static readonly SjOption[] Years =
    {
        new("全部年份", 0), new("近一年", 114), new("近5年", 115), new("5年以前", 116)
    };

    /// <summary>「全部试卷」在站点上是不带 level 参数，用 -1 表示。</summary>
    public static readonly SjOption[] Levels =
    {
        new("全部试卷", -1), new("基础试卷", 0), new("普通试卷", 1), new("精品试卷", 2)
    };

    public static readonly SjOption[] Versions =
    {
        new("全部", 0), new("人教版", 24), new("北师大版", 25), new("苏教版", 26),
        new("沪教版", 27), new("外研版", 28), new("湘教版", 29), new("浙教版", 30),
        new("粤教版", 31), new("通用版", 32), new("其他版本", 33)
    };

    public static readonly SjOption[] Types =
    {
        new("全部", 0), new("单元测试", 34), new("月考", 35), new("期中考试", 36),
        new("期末考试", 37), new("会考", 38), new("中考真题", 39), new("高考真题", 40),
        new("竞赛试卷", 41), new("模拟试卷", 42)
    };

    public static readonly SjSortOption[] Sorts =
    {
        new("综合排序", "comprehensive"), new("评分最高", "rating"),
        new("最新上传", "newest"), new("下载最多", "downloads")
    };

    /// <summary>
    /// 关键词走站点全文搜索 /search，该页面不接受 /list 的筛选参数，
    /// 因此有关键词时按站点能力只带 q + page，并在界面状态栏说明。
    /// </summary>
    public static string BuildUrl(SjQuery q)
    {
        var sb = new StringBuilder();
        if (!string.IsNullOrWhiteSpace(q.Keyword))
        {
            sb.Append(BaseUrl).Append("/search?q=").Append(Uri.EscapeDataString(q.Keyword.Trim()));
            if (q.Page > 1)
            {
                sb.Append("&page=").Append(q.Page);
            }

            return sb.ToString();
        }

        sb.Append(BaseUrl).Append("/list?sid=").Append(q.Sid)
            .Append("&g=").Append(q.Grade)
            .Append("&term=").Append(q.Term)
            .Append("&v=").Append(q.Version)
            .Append("&t=").Append(q.Type)
            .Append("&r=").Append(q.Region)
            .Append("&year=").Append(q.Year);
        if (q.Level >= 0)
        {
            sb.Append("&level=").Append(q.Level);
        }

        if (!string.IsNullOrWhiteSpace(q.Sort) && q.Sort != "comprehensive")
        {
            sb.Append("&sort=").Append(Uri.EscapeDataString(q.Sort));
        }

        if (q.Page > 1)
        {
            sb.Append("&page=").Append(q.Page);
        }

        return sb.ToString();
    }

    public static string DetailUrl(int id) => $"{BaseUrl}/shijuan/{id}.html";

    public static string DownloadUrl(int id) => $"{BaseUrl}/download/{id}.html";
}
