using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ratings.Api;
using Jellyfin.Plugin.Ratings.Configuration;
using Jellyfin.Plugin.Ratings.Sources;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings.Core;

/// <summary>
/// 评分聚合器。替代原脚本中散落在浏览器端的 fetch + 代理逻辑：
/// 统一调度、并行抓取、单点缓存、单源失败隔离与健康度熔断都在这里完成。
///
/// 依赖顺序分两波：
///   Wave 1 - MDBList（主数据）与 TMDb external_ids（补 IMDb ID）
///   Wave 2 - 需要 IMDb ID / 标题年份的增强源（AniList、豆瓣、AlloCiné、Kinopoisk、认证角标）
/// </summary>
public sealed class RatingAggregator
{
    private readonly RatingsHttp _http;
    private readonly RatingCache _cache;
    private readonly SourceHealth _health;
    private readonly SourceRegistry _registry = new();
    private readonly ILogger<RatingAggregator> _logger;
    private readonly ConcurrentDictionary<string, Lazy<Task<RatingsResponse>>> _inFlight = new(StringComparer.Ordinal);

    public RatingAggregator(
        RatingsHttp http,
        RatingCache cache,
        SourceHealth health,
        ILogger<RatingAggregator> logger)
    {
        _http = http;
        _cache = cache;
        _health = health;
        _logger = logger;
    }

    public SourceHealth Health => _health;

    public RatingCache Cache => _cache;

    public async Task<RatingsResponse> GetAsync(RatingQuery query, PluginConfiguration config, CancellationToken cancellationToken)
    {
        if (!query.HasAnyId)
        {
            return new RatingsResponse
            {
                ItemId = query.ItemId,
                MediaType = query.MediaType,
                Title = query.Title,
                Message = "该条目没有 TMDb / IMDb Provider ID，无法获取外部评分"
            };
        }

        var key = query.CacheKey();

        if (_cache.TryGet(key, out var cached) && cached is not null)
        {
            cached.FromCache = true;
            return cached;
        }

        // 同一个条目被并发打开（多人同时浏览、前端重复扫描）时只发一轮外部请求
        var lazy = _inFlight.GetOrAdd(
            key,
            _ => new Lazy<Task<RatingsResponse>>(
                () => BuildAsync(query, config, key, cancellationToken),
                LazyThreadSafetyMode.ExecutionAndPublication));

        try
        {
            return await lazy.Value.ConfigureAwait(false);
        }
        finally
        {
            _inFlight.TryRemove(key, out _);
        }
    }

    private async Task<RatingsResponse> BuildAsync(RatingQuery query, PluginConfiguration config, string cacheKey, CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();
        var context = new SourceContext
        {
            Config = config,
            Http = _http,
            Logger = _logger,
            Wikidata = new WikidataHelper(_http)
        };

        var response = new RatingsResponse
        {
            ItemId = query.ItemId,
            MediaType = query.MediaType,
            TmdbId = query.TmdbId,
            ImdbId = query.ImdbId,
            Title = query.Title,
            Year = query.Year,
            Season = query.Season,
            Episode = query.Episode
        };

        try
        {
            // ---- Wave 0: 补齐 IMDb ID ----
            if (string.IsNullOrWhiteSpace(query.ImdbId)
                && config.EnableTmdb
                && !string.IsNullOrWhiteSpace(query.TmdbId))
            {
                var imdb = await _registry.Tmdb.ResolveImdbIdAsync(query, context, cancellationToken).ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(imdb))
                {
                    query.ImdbId = imdb;
                    response.ImdbId = imdb;
                }
            }

            // ---- Wave 1: 主数据源 ----
            if (_registry.MdbList.IsEnabled(config))
            {
                var main = await RunAsync(_registry.MdbList, query, context, cancellationToken).ConfigureAwait(false);
                MergeResult(response, main, config);

                if (!string.IsNullOrWhiteSpace(main.Title))
                {
                    query.Title = main.Title;
                    response.Title ??= main.Title;
                }

                if (main.Year is > 0)
                {
                    query.Year ??= main.Year;
                    response.Year ??= main.Year;
                }
            }
            else
            {
                response.Errors.Add("mdblist: 已在配置中禁用");
            }

            // ---- 单集 / 单季：只展示该集(季) TMDb 评分，与原脚本一致 ----
            var episodeOnly = (query.IsEpisode || query.IsSeason) && config.EnableEpisodeRatings;
            if (episodeOnly)
            {
                if (config.EnableTmdb)
                {
                    var tmdb = await RunAsync(_registry.Tmdb, query, context, cancellationToken).ConfigureAwait(false);
                    MergeResult(response, tmdb, config);
                }

                if (response.Ratings.Count > 0)
                {
                    return Finish(response, config, cacheKey, stopwatch);
                }
            }

            // ---- Wave 2: 增强源并行 ----
            var enrichment = _registry.All
                .Where(source => source.Key is not ("tmdb" or "mdblist"))
                .Where(source => source.IsEnabled(config))
                .Select(source => RunAsync(source, query, context, cancellationToken))
                .ToArray();

            if (enrichment.Length > 0)
            {
                var results = await Task.WhenAll(enrichment).ConfigureAwait(false);
                foreach (var result in results)
                {
                    MergeResult(response, result, config);
                }
            }

            if (response.Ratings.Count == 0 && response.Message is null)
            {
                response.Message = string.IsNullOrWhiteSpace(config.MdbListApiKey) && config.EnableMdbList
                    ? "未配置 MDBList API Key，无法获取多来源评分"
                    : "未获取到评分数据";
            }

            return Finish(response, config, cacheKey, stopwatch);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug("[Ratings] 请求被取消: {Key}", query.CacheKey());
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Ratings] 聚合评分失败: {Key}", query.CacheKey());
            response.Errors.Add("internal: " + ex.Message);
            return response;
        }
    }

    private RatingsResponse Finish(RatingsResponse response, PluginConfiguration config, string cacheKey, Stopwatch stopwatch)
    {
        response.Ratings = response.Ratings
            .GroupBy(entry => entry.Source, StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(entry => entry.Order)
            .ThenByDescending(entry => entry.Value)
            .Take(Math.Clamp(config.MaxRatingsPerItem, 1, 20))
            .ToList();

        if (!config.ExposeErrors)
        {
            response.Errors.Clear();
        }

        _cache.Set(cacheKey, response, config.CacheMinutes);

        _logger.LogInformation(
            "[Ratings] 聚合完成 {Title} ({Year}) 来源 {Count} 个，错误 {Errors} 条，耗时 {Elapsed}ms",
            response.Title ?? "-",
            response.Year?.ToString() ?? "-",
            response.Ratings.Count,
            response.Errors.Count,
            stopwatch.ElapsedMilliseconds);

        return response;
    }

    private void MergeResult(RatingsResponse target, SourceResult result, PluginConfiguration config)
    {
        if (result.Ratings.Count > 0)
        {
            target.Ratings.AddRange(result.Ratings);
        }

        if (result.Badges.Count > 0)
        {
            foreach (var badge in result.Badges)
            {
                if (!target.Badges.Contains(badge))
                {
                    target.Badges.Add(badge);
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(result.Error) && config.ExposeErrors)
        {
            target.Errors.Add(result.Error!);
        }
    }

    private async Task<SourceResult> RunAsync(
        IRatingSource source,
        RatingQuery query,
        SourceContext context,
        CancellationToken cancellationToken)
    {
        if (!_health.IsAvailable(source.Key))
        {
            _logger.LogDebug("[Ratings] {Source} 处于冷却期，跳过", source.Key);
            return SourceResult.Failed($"{source.Key}: 冷却中");
        }

        var stopwatch = Stopwatch.StartNew();
        try
        {
            var result = await source.FetchAsync(query, context, cancellationToken).ConfigureAwait(false);
            _health.Report(source.Key, result.Success, context.Config.FailureThreshold, context.Config.CooldownMinutes, _logger);

            _logger.LogDebug(
                "[Ratings] {Source} 完成 {Count} 条，{Elapsed}ms，error={Error}",
                source.Key, result.Ratings.Count, stopwatch.ElapsedMilliseconds, result.Error ?? "-");

            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _health.Report(source.Key, false, context.Config.FailureThreshold, context.Config.CooldownMinutes, _logger);
            _logger.LogWarning(ex, "[Ratings] 数据源 {Source} 抛出异常", source.Key);
            return SourceResult.Failed($"{source.Key}: {ex.GetType().Name}");
        }
    }
}
