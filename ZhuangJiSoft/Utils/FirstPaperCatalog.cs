namespace DeskApp.Utils;

/// <summary>第一试卷网学科定义。</summary>
public sealed class FirstPaperSubject
{
    public FirstPaperSubject(string key, string name, string prefix, string[] grades)
    {
        Key = key;
        Name = name;
        Prefix = prefix;
        Grades = grades;
    }

    public string Key { get; }

    public string Name { get; }

    /// <summary>栏目前缀，如语文 sjyw、数学 sjsx。</summary>
    public string Prefix { get; }

    /// <summary>该学科实际存在的年级（严格按站点栏目页，不使用全量年级）。</summary>
    public string[] Grades { get; }

    public override string ToString() => Name;
}

/// <summary>
/// 第一试卷网学科 / 年级映射。
/// 年级清单来自站点各学科栏目页侧边栏，与需求图片一致：
/// 语数英 1-9 + 中考 + 高一~高三 + 高考；物理 8-9 起；化学 9 起；政史地生 7-9 起。
/// </summary>
public static class FirstPaperCatalog
{
    private static readonly string[] PrimaryAndMiddle =
    {
        "一年级", "二年级", "三年级", "四年级", "五年级", "六年级",
        "七年级", "八年级", "九年级", "中考试卷", "高一", "高二", "高三", "高考试卷"
    };

    private static readonly string[] Physics =
    {
        "八年级", "九年级", "中考试卷", "高一", "高二", "高三", "高考试卷"
    };

    private static readonly string[] Chemistry =
    {
        "九年级", "中考试卷", "高一", "高二", "高三", "高考试卷"
    };

    private static readonly string[] JuniorSeven =
    {
        "七年级", "八年级", "九年级", "中考试卷", "高一", "高二", "高三", "高考试卷"
    };

    public static IReadOnlyList<FirstPaperSubject> Subjects { get; } = new[]
    {
        new FirstPaperSubject("sjyw", "语文试卷", "sjyw", PrimaryAndMiddle),
        new FirstPaperSubject("sjsx", "数学试卷", "sjsx", PrimaryAndMiddle),
        new FirstPaperSubject("sjyy", "英语试卷", "sjyy", PrimaryAndMiddle),
        new FirstPaperSubject("sjwl", "物理试卷", "sjwl", Physics),
        new FirstPaperSubject("sjhx", "化学试卷", "sjhx", Chemistry),
        new FirstPaperSubject("sjzz", "政治试卷", "sjzz", JuniorSeven),
        new FirstPaperSubject("sjls", "历史试卷", "sjls", JuniorSeven),
        new FirstPaperSubject("sjdl", "地理试卷", "sjdl", JuniorSeven),
        new FirstPaperSubject("sjsw", "生物试卷", "sjsw", JuniorSeven)
    };

    public static FirstPaperSubject GetSubject(string key) =>
        Subjects.FirstOrDefault(s => s.Key == key) ?? Subjects[0];

    /// <summary>年级名 → 站点栏目后缀。</summary>
    public static string GradeCode(string grade) => grade switch
    {
        "一年级" => "1",
        "二年级" => "2",
        "三年级" => "3",
        "四年级" => "4",
        "五年级" => "5",
        "六年级" => "6",
        "七年级" => "7",
        "八年级" => "8",
        "九年级" => "9",
        "中考试卷" => "zk",
        "高一" => "g1",
        "高二" => "g2",
        "高三" => "g3",
        "高考试卷" => "gk",
        _ => "1"
    };

    /// <summary>栏目首页地址，例如 https://www.shijuan1.com/a/sjsx3/ 。</summary>
    public static string ListUrl(string subjectKey, string grade) =>
        $"https://www.shijuan1.com/a/{GetSubject(subjectKey).Prefix}{GradeCode(grade)}/";
}
