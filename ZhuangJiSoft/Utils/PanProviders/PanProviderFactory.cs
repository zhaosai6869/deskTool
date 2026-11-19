using DeskApp.Models;

namespace DeskApp.Utils.PanProviders;

/// <summary>按网盘站点创建对应 Provider。</summary>
public static class PanProviderFactory
{
    public static IPanProvider Create(PanSite site) => site switch
    {
        PanSite.Baidu => new BaiduProvider(),
        PanSite.Aliyun => new AliyunProvider(),
        PanSite.Quark => new QuarkProvider(isUc: false),
        PanSite.UC => new QuarkProvider(isUc: true),
        PanSite.Tianyi => new TianyiProvider(),
        PanSite.Xunlei => new XunleiProvider(),
        PanSite.Mobile => new MobileProvider(),
        PanSite.Pan123 => new Pan123Provider(),
        _ => throw new System.ArgumentOutOfRangeException(nameof(site))
    };
}
