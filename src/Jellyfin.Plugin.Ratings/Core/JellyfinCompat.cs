using System;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Model.Entities;

namespace Jellyfin.Plugin.Ratings.Core;

/// <summary>
/// 宿主信息与 ProviderId 读取辅助。插件仅目标 Jellyfin 12（net10.0），
/// 不再保留多版本分支；这里只收敛「读版本号」与「安全读 ProviderId」两件事，
/// 业务代码统一走封装，避免在多处重复 try/catch。
/// </summary>
public static class JellyfinCompat
{
    private static Version _serverVersion = new(12, 0, 0);

    /// <summary>Gets 当前宿主版本，初始化失败时退化为 12.0.0。</summary>
    public static Version ServerVersion => _serverVersion;

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
    /// 安全读取 ProviderId：空值、缺失、异常一律返回 null，
    /// 调用方无需再做 null/try-catch 处理。
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
