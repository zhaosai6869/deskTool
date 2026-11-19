using System;

namespace DeskApp.Models;

/// <summary>漫画网站快捷入口（名称 + 首页网址）。</summary>
public sealed class ComicSite
{
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    public override string ToString() => Name;

    /// <summary>内置默认站点（2026-11 核实仍为各站官方首页），用户可自由增删。</summary>
    public static ComicSite[] Defaults =>
    [
        new() { Name = "哔哩哔哩漫画", Url = "https://manga.bilibili.com" },
        new() { Name = "腾讯动漫", Url = "https://ac.qq.com" },
        new() { Name = "快看漫画", Url = "https://www.kuaikanmanhua.com" },
        new() { Name = "动漫之家", Url = "https://www.dmzj.com" },
        new() { Name = "漫画DB", Url = "https://www.manhuadb.com" },
        new() { Name = "拷贝漫画", Url = "https://copymanga.com" },
        new() { Name = "WEBTOON", Url = "https://www.webtoons.com/zh-cn" },
        new() { Name = "知音漫客", Url = "https://www.zymk.cn" }
    ];

    public static ComicSite[] CloneDefaults() =>
        Array.ConvertAll(Defaults, s => new ComicSite { Name = s.Name, Url = s.Url });
}
