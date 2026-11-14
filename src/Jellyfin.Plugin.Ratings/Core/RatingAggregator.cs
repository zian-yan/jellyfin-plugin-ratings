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
/// 评分聚合器：并行抓取、单点缓存、单源失败隔离与健康度熔断。
/// 分三段推进——补 IMDb ID（Wave 0）、MDBList 主数据（Wave 1）、增强源（Wave 2，超出宽限期先返回已有结果）。
/// </summary>
public sealed class RatingAggregator
{
    /// <summary>宽限期超时后先返回的部分结果，缓存的存活时间（分钟）。</summary>
    private const int PartialCacheMinutes = 1;

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
        // 兜底白名单：Provider ID 会拼进外部 URL，非法值直接丢弃，避免路径注入
        query.TmdbId = RatingQuery.SanitizeId(query.TmdbId);
        query.ImdbId = RatingQuery.SanitizeId(query.ImdbId);

        // 磁盘缓存是后台加载的：最多等 300ms，等不到就按空缓存继续（最坏是多打一次外部接口），
        // 绝不把「读完整个缓存文件」的耗时压到请求线程上
        await _cache.EnsureLoadedAsync(TimeSpan.FromMilliseconds(300), cancellationToken).ConfigureAwait(false);

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

        // 过期旧值：先返回，再后台刷新。详情页不必等外部接口返回。
        if (_cache.TryGetStale(key, out var stale, out var fetchedAt) && stale is not null)
        {
            stale.FromCache = true;
            var ageMinutes = (int)(DateTimeOffset.UtcNow - fetchedAt).TotalMinutes;
            _logger.LogDebug("[Ratings] 命中过期缓存（{Age} 分钟前），后台刷新: {Key}", ageMinutes, key);
            _ = RevalidateAsync(query, config, key);
            return stale;
        }

        // 同一个条目被并发打开（多人同时浏览、前端重复扫描）时只发一轮外部请求。
        // 共享任务用 CancellationToken.None 跑：某个调用方取消或关页，不该把其他人的结果一起打断
        return await AwaitSharedAsync(query, config, key).WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>取得（或加入）该条目正在进行的那次抓取。</summary>
    private async Task<RatingsResponse> AwaitSharedAsync(RatingQuery query, PluginConfiguration config, string key)
    {
        var lazy = _inFlight.GetOrAdd(
            key,
            _ => new Lazy<Task<RatingsResponse>>(
                () => BuildAsync(query, config, key, CancellationToken.None),
                LazyThreadSafetyMode.ExecutionAndPublication));

        // 占位只在任务真正结束后摘除：等待方取消就摘，会让后来者以为没人抓过而重复请求
        _ = lazy.Value.ContinueWith(
            _ => ReleaseInFlight(key, lazy),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        return await lazy.Value.ConfigureAwait(false);
    }

    /// <summary>
    /// 后台刷新过期缓存。不占用调用方请求，失败也不影响已经返回的旧数据。
    /// </summary>
    private async Task RevalidateAsync(RatingQuery query, PluginConfiguration config, string key)
    {
        try
        {
            await AwaitSharedAsync(query, config, key).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Ratings] 后台刷新失败: {Key}", key);
        }
    }

    /// <summary>
    /// 释放去重占位。只有「字典里仍然是自己创建的那一个」时才移除——
    /// 无条件 TryRemove 会把后来者正在等待的任务一并删掉，导致同一部片被重复抓取。
    /// </summary>
    private void ReleaseInFlight(string key, Lazy<Task<RatingsResponse>> lazy)
    {
        ((ICollection<KeyValuePair<string, Lazy<Task<RatingsResponse>>>>)_inFlight)
            .Remove(new KeyValuePair<string, Lazy<Task<RatingsResponse>>>(key, lazy));
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
            // ---- 三路并发启动：补 IMDb ID、主数据源、单集 TMDb 互不依赖 ----
            Task<string?>? imdbTask = null;
            if (string.IsNullOrWhiteSpace(query.ImdbId)
                && config.EnableTmdb
                && !string.IsNullOrWhiteSpace(query.TmdbId))
            {
                imdbTask = ResolveImdbIdAsync(query, context, cancellationToken);
            }

            Task<SourceResult>? mainTask = null;
            if (_registry.MdbList.IsEnabled(config))
            {
                mainTask = RunAsync(_registry.MdbList, query, context, cancellationToken);
            }
            else
            {
                response.Errors.Add("mdblist: 已在配置中禁用");
            }

            var episodeOnly = (query.IsEpisode || query.IsSeason) && config.EnableEpisodeRatings;
            Task<SourceResult>? episodeTask = episodeOnly && config.EnableTmdb
                ? RunAsync(_registry.Tmdb, query, context, cancellationToken)
                : null;

            // ---- Wave 1：主数据 ----
            if (mainTask is not null)
            {
                var main = await mainTask.ConfigureAwait(false);
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

            // ---- 分集 / 分季：只展示该集(季) 的 TMDb 评分，与原脚本一致 ----
            if (episodeOnly)
            {
                if (episodeTask is not null)
                {
                    MergeResult(response, await episodeTask.ConfigureAwait(false), config);
                }

                // MDBList 的 TMDb 是整剧级：拿不到该集分数时它会冒充该集分数，一律剔除
                StripShowLevelRatings(response);

                if (response.Ratings.Count > 0)
                {
                    return Finish(response, config, cacheKey, stopwatch, persist: true);
                }
            }

            // ---- Wave 0 收口：Wave 2 需要 IMDb ID，此时它通常已经并行完成 ----
            if (imdbTask is not null)
            {
                var imdb = await imdbTask.ConfigureAwait(false);
                if (!string.IsNullOrWhiteSpace(imdb))
                {
                    query.ImdbId = imdb;
                    response.ImdbId = imdb;
                }
            }

            // ---- Wave 2：增强源并行，超出宽限期先返回已有结果 ----
            var enrichment = _registry.All
                .Where(source => source.Key is not ("tmdb" or "mdblist"))
                .Where(source => source.IsEnabled(config))
                .Select(source => RunAsync(source, query, context, cancellationToken))
                .ToArray();

            if (enrichment.Length > 0)
            {
                var all = Task.WhenAll(enrichment);
                var grace = Math.Clamp(config.EnrichmentGraceMs, 0, 15000);

                if (grace > 0)
                {
                    var first = await Task.WhenAny(all, Task.Delay(grace, cancellationToken)).ConfigureAwait(false);
                    if (!ReferenceEquals(first, all))
                    {
                        cancellationToken.ThrowIfCancellationRequested();

                        // 只取已完成的部分，剩余源在后台跑完并回填完整缓存
                        var partial = Clone(response);
                        foreach (var task in enrichment)
                        {
                            if (task.IsCompletedSuccessfully)
                            {
                                MergeResult(partial, task.Result, config);
                            }
                        }

                        // 先留一份未裁剪的快照给后台补全，Finish 会就地裁剪 Ratings
                        var backgroundSnapshot = Clone(partial);
                        CacheWhenComplete(all, backgroundSnapshot, config, cacheKey, stopwatch);

                        var finished = Finish(partial, config, cacheKey, stopwatch, persist: false, partial: true);

                        // 部分结果也写一个很短的 TTL：否则紧接着再有人打开同一部片，
                        // 会因为缓存未命中而把整套外部请求重打一遍
                        if (config.CacheMinutes > 0)
                        {
                            _cache.Set(cacheKey, finished, Math.Min(config.CacheMinutes, PartialCacheMinutes));
                        }

                        return finished;
                    }
                }

                foreach (var result in await all.ConfigureAwait(false))
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

            return Finish(response, config, cacheKey, stopwatch, persist: true);
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

    /// <summary>补齐 IMDb ID，失败时返回 null（不影响主流程）。</summary>
    private async Task<string?> ResolveImdbIdAsync(RatingQuery query, SourceContext context, CancellationToken cancellationToken)
    {
        try
        {
            return await _registry.Tmdb.ResolveImdbIdAsync(query, context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Ratings] 解析 IMDb ID 失败: {TmdbId}", query.TmdbId);
            return null;
        }
    }

    /// <summary>
    /// 宽限期超时后，等剩余增强源在后台完成，把完整结果写回缓存。
    /// 下一个访问者（或用户刷新）就能直接命中完整数据，不必再等。
    /// </summary>
    private void CacheWhenComplete(
        Task<SourceResult[]> all,
        RatingsResponse partialSnapshot,
        PluginConfiguration config,
        string cacheKey,
        Stopwatch stopwatch)
    {
        _ = all.ContinueWith(
            completed =>
            {
                try
                {
                    if (completed.Status != TaskStatus.RanToCompletion)
                    {
                        return;
                    }

                    var snapshot = Clone(partialSnapshot);
                    foreach (var result in completed.Result)
                    {
                        MergeResult(snapshot, result, config);
                    }

                    Finish(snapshot, config, cacheKey, stopwatch, persist: true);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[Ratings] 后台回填缓存失败: {Key}", cacheKey);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// 剔除整剧级评分（目前只有 MDBList 的 TMDb 会带这个标记）。
    /// 分集页只认该集分数：整剧分既可能与该集分重复，也可能在拿不到集分时冒充它。
    /// </summary>
    public static void StripShowLevelRatings(RatingsResponse response)
    {
        response.Ratings.RemoveAll(entry =>
            string.Equals(entry.Logo, Logos.Tmdb, StringComparison.Ordinal)
            && string.Equals(entry.Scope, "show", StringComparison.Ordinal));
    }

    private RatingsResponse Finish(
        RatingsResponse response,
        PluginConfiguration config,
        string cacheKey,
        Stopwatch stopwatch,
        bool persist,
        bool partial = false)
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

        if (persist)
        {
            _cache.Set(cacheKey, response, config.CacheMinutes);
        }

        _logger.LogInformation(
            "[Ratings] 聚合完成{Lag} {Title} ({Year}) 来源 {Count} 个，错误 {Errors} 条，耗时 {Elapsed}ms",
            partial ? "（部分，其余在后台继续）" : string.Empty,
            LogText.Clean(response.Title),
            response.Year?.ToString() ?? "-",
            response.Ratings.Count,
            response.Errors.Count,
            stopwatch.ElapsedMilliseconds);

        return response;
    }

    private static RatingsResponse Clone(RatingsResponse source) => new()
    {
        ItemId = source.ItemId,
        MediaType = source.MediaType,
        TmdbId = source.TmdbId,
        ImdbId = source.ImdbId,
        Title = source.Title,
        Year = source.Year,
        Season = source.Season,
        Episode = source.Episode,
        Ratings = new List<RatingEntry>(source.Ratings),
        Badges = new List<string>(source.Badges),
        Errors = new List<string>(source.Errors),
        Message = source.Message,
        FetchedAt = source.FetchedAt
    };

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

            // 429 是配额限流，不是源坏了：计入熔断会让一次集体刷新就把整个源冷却十分钟
            if (result.HttpStatus != 429)
            {
                _health.Report(source.Key, result.Success, context.Config.FailureThreshold, context.Config.CooldownMinutes, _logger);
            }

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
