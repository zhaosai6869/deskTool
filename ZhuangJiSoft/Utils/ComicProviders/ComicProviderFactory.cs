using System;
using System.Collections.Generic;
using System.Linq;

namespace DeskApp.Utils.ComicProviders;

/// <summary>
/// 内置站点注册表：固定 4 家，各自独立解析逻辑，不支持用户增删改。
/// </summary>
public static class ComicProviderFactory
{
    private static readonly IComicProvider[] Items =
    [
        new BilibiliComicProvider(),
        new TencentComicProvider(),
        new KuaikanComicProvider(),
        new ZymkComicProvider()
    ];

    public static IReadOnlyList<IComicProvider> All => Items;

    /// <summary>按网址匹配站点；不支持时返回 null。</summary>
    public static IComicProvider? Resolve(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri)
            ? Items.FirstOrDefault(p => p.Matches(uri))
            : null;

    public static IComicProvider? ByKey(string key) =>
        Items.FirstOrDefault(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
}
