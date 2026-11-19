using System;

namespace DeskApp.Models;

/// <summary>支持的网盘站点（对齐 LinkSwift 八大网盘）。</summary>
public enum PanSite
{
    Baidu,      // 百度网盘
    Aliyun,     // 阿里云盘
    Quark,      // 夸克网盘
    UC,         // UC 网盘
    Tianyi,     // 天翼云盘
    Xunlei,     // 迅雷云盘
    Mobile,     // 中国移动云盘
    Pan123      // 123 云盘
}

/// <summary>网盘站点静态信息。</summary>
public static class PanSites
{
    public sealed record Info(PanSite Site, string Name, string Icon, string LoginUrl, string HomeUrl);

    public static readonly Info[] All =
    [
        new(PanSite.Baidu, "百度网盘", "📦", "https://pan.baidu.com", "https://pan.baidu.com/disk/home"),
        new(PanSite.Aliyun, "阿里云盘", "☁️", "https://www.alipan.com", "https://www.alipan.com"),
        new(PanSite.Quark, "夸克网盘", "⚡", "https://pan.quark.cn", "https://pan.quark.cn"),
        new(PanSite.UC, "UC 网盘", "🐦", "https://drive.uc.cn", "https://drive.uc.cn"),
        new(PanSite.Tianyi, "天翼云盘", "📡", "https://cloud.189.cn", "https://cloud.189.cn/web/"),
        new(PanSite.Xunlei, "迅雷云盘", "⚡", "https://pan.xunlei.com", "https://pan.xunlei.com"),
        new(PanSite.Mobile, "中国移动云盘", "📱", "https://yun.139.com", "https://yun.139.com/w/"),
        new(PanSite.Pan123, "123 云盘", "🔢", "https://www.123pan.com", "https://www.123pan.com")
    ];

    public static Info Get(PanSite site) => All.FirstOrDefault(x => x.Site == site) ?? All[0];

    public static string NameOf(PanSite site) => Get(site).Name;

    /// <summary>根据分享链接判断属于哪个网盘，无法判断返回 null。</summary>
    public static PanSite? Detect(string url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        url = url.ToLowerInvariant();
        if (url.Contains("pan.baidu.com") || url.Contains("yun.baidu.com")) return PanSite.Baidu;
        if (url.Contains("alipan.com") || url.Contains("aliyundrive.com")) return PanSite.Aliyun;
        if (url.Contains("pan.quark.cn")) return PanSite.Quark;
        if (url.Contains("drive.uc.cn")) return PanSite.UC;
        if (url.Contains("cloud.189.cn")) return PanSite.Tianyi;
        if (url.Contains("pan.xunlei.com")) return PanSite.Xunlei;
        if (url.Contains("yun.139.com") || url.Contains("caiyun.139.com")) return PanSite.Mobile;
        if (url.Contains("123pan.com") || url.Contains("123684.com") || url.Contains("123865.com")) return PanSite.Pan123;
        return null;
    }
}
