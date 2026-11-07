using System;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Ratings.Core;

/// <summary>
/// 版本兼容层。把 Jellyfin 10.11 与 12 之间存在差异的调用收敛到这一处，
/// 业务代码只依赖这里的封装，避免在多处散落 if (version) 分支。
/// </summary>
public static class JellyfinCompat
{
    private static Version _serverVersion = new(10, 11, 0);

    /// <summary>Gets 当前宿主版本，初始化失败时退化为 10.11。</summary>
    public static Version ServerVersion => _serverVersion;

    /// <summary>Gets 是否为 Jellyfin 12 及以上（net10.0 目标、新的 Provider 解析管线）。</summary>
    public static bool IsV12OrLater => _serverVersion.Major >= 12;

    /// <summary>Gets 目标框架标识，用于日志与诊断。</summary>
    public static string TargetFramework =>
#if NET10_0
        "net10.0";
#else
        "net9.0";
#endif

    internal static void Initialize(IServerApplicationHost host)
    {
        try
        {
            if (host?.ApplicationVersion is { } v && v.Major > 0)
            {
                _serverVersion = v;
            }
        }
        catch
        {
            // 版本读取失败不应阻断插件加载。
        }
    }

    /// <summary>
    /// 安全读取 ProviderId。10.11 与 12 的 <see cref="IHasProviderIds.ProviderIds"/>
    /// 都是 Dictionary，但 12 增加了 IsValidProviderId 等校验，统一走扩展方法可避免版本差异。
    /// </summary>
    public static string? GetProviderId(BaseItem? item, MetadataProvider provider)
    {
        if (item is null)
        {
            return null;
        }

        try
        {
            return item.TryGetProviderId(provider, out var id) && !string.IsNullOrWhiteSpace(id) ? id : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>按名称读取 ProviderId（如 "Tmdb"、"Imdb"）。</summary>
    public static string? GetProviderId(BaseItem? item, string name)
    {
        if (item is null)
        {
            return null;
        }

        try
        {
            return item.TryGetProviderId(name, out var id) && !string.IsNullOrWhiteSpace(id) ? id : null;
        }
        catch
        {
            return null;
        }
    }
}
