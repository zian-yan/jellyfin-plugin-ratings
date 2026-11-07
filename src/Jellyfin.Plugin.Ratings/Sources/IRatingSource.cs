using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ratings.Api;
using Jellyfin.Plugin.Ratings.Configuration;
using Jellyfin.Plugin.Ratings.Core;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings.Sources;

/// <summary>数据源上下文，避免每个源都重复注入依赖。</summary>
public sealed class SourceContext
{
    public required PluginConfiguration Config { get; init; }

    public required RatingsHttp Http { get; init; }

    public required ILogger Logger { get; init; }

    /// <summary>Gets 统一的 Wikidata SPARQL 查询入口（部分源需要先查外链 ID）。</summary>
    public required WikidataHelper Wikidata { get; init; }
}

/// <summary>单个数据源的抓取结果。</summary>
public sealed class SourceResult
{
    public List<RatingEntry> Ratings { get; } = new();

    public List<string> Badges { get; } = new();

    /// <summary>Gets or sets 原始标题（豆瓣/kinopoisk 需要按标题检索）。</summary>
    public string? Title { get; set; }

    public int? Year { get; set; }

    public string? Error { get; set; }

    public bool Success { get; set; } = true;

    public static SourceResult Failed(string error) => new() { Success = false, Error = error };

    public static SourceResult Empty() => new();
}

/// <summary>
/// 数据源契约。新增来源只需实现本接口并在 <see cref="SourceRegistry"/> 注册，
/// 聚合器会自动纳入并行调度、缓存、健康度与错误隔离。
/// </summary>
public interface IRatingSource
{
    string Key { get; }

    int Order { get; }

    bool IsEnabled(PluginConfiguration config);

    Task<SourceResult> FetchAsync(RatingQuery query, SourceContext context, CancellationToken cancellationToken);
}

internal static class SourceParse
{
    public static double? ToDouble(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        raw = raw.Trim().Replace(',', '.');
        return double.TryParse(raw, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
