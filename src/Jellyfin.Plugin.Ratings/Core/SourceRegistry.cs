using System.Collections.Generic;
using System.Linq;
using Jellyfin.Plugin.Ratings.Configuration;
using Jellyfin.Plugin.Ratings.Sources;

namespace Jellyfin.Plugin.Ratings.Core;

/// <summary>数据源注册表：新增来源只改这里一处。</summary>
public sealed class SourceRegistry
{
    public SourceRegistry()
    {
        Tmdb = new TmdbSource();
        MdbList = new MdbListSource();
        All = new IRatingSource[]
        {
            Tmdb,
            MdbList,
            new AniListSource(),
            new KinopoiskSource(),
            new DoubanSource(),
            new AllocineSource(),
            new CertificationsSource()
        };
    }

    public TmdbSource Tmdb { get; }

    public MdbListSource MdbList { get; }

    public IReadOnlyList<IRatingSource> All { get; }
}

/// <summary>统一的配置读取入口，兼容插件实例尚未构造的极早期调用。</summary>
public static class ConfigProvider
{
    private static readonly PluginConfiguration Fallback = new();

    public static PluginConfiguration Current => Plugin.Instance?.Configuration ?? Fallback;
}
