using System.Collections.Concurrent;
using System.Reflection;
using System.Text.Json;
using Jellyfin.Plugin.Ratings.Api;
using Jellyfin.Plugin.Ratings.Configuration;
using Jellyfin.Plugin.Ratings.Core;
using Jellyfin.Plugin.Ratings.Sources;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Ratings.Tests;

/// <summary>缓存、聚合器兜底路径与 JSON 契约，全部离线执行。</summary>
public class AggregationTests
{
    [Fact]
    public async Task PersistentCacheSurvivesRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "jfr-test-" + Guid.NewGuid().ToString("N"));
        var paths = new FakeApplicationPaths(directory);

        try
        {
            using (var cache = new RatingCache(paths, NullLogger<RatingCache>.Instance))
            {
                cache.Set("movie|550|-|-|-", new RatingsResponse { Title = "Fight Club", TmdbId = "550" }, 720);
                // 故意不调用 Flush：Dispose 必须把脏数据刷盘，这是之前的真实缺陷
            }

            // 磁盘缓存在后台加载，等待就绪后再读（超时也算失败，说明加载逻辑没跑起来）
            using var reopened = new RatingCache(paths, NullLogger<RatingCache>.Instance);
            await reopened.EnsureLoadedAsync(TimeSpan.FromSeconds(5), CancellationToken.None);

            Assert.True(reopened.TryGet("movie|550|-|-|-", out var restored));
            Assert.Equal("Fight Club", restored!.Title);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }

    [Fact]
    public void LogTextStripsControlCharactersAndTruncates()
    {
        Assert.Equal("-", LogText.Clean(null));
        Assert.Equal("-", LogText.Clean("   "));
        Assert.Equal("8.5", LogText.Clean("8.5"));

        // \r 与 \n 各变成一个空格，日志行不会被伪造
        Assert.Equal("a  b", LogText.Clean("a\r\nb"));

        var cleaned = LogText.Clean(new string('x', 500));
        Assert.Equal(201, cleaned.Length);
        Assert.EndsWith("…", cleaned, StringComparison.Ordinal);
    }

    [Fact]
    public void ThrottleBlocksAfterLimitWithinWindow()
    {
        var throttle = new RequestThrottle();

        for (var i = 0; i < 3; i++)
        {
            Assert.True(throttle.TryAcquire("u:alice", 3, out _));
        }

        Assert.False(throttle.TryAcquire("u:alice", 3, out var retryAfter));
        Assert.True(retryAfter >= 1);

        // 其他客户端不受影响
        Assert.True(throttle.TryAcquire("u:bob", 3, out _));
    }

    [Fact]
    public void ThrottleIsDisabledWhenLimitIsZero()    {
        var throttle = new RequestThrottle();

        for (var i = 0; i < 500; i++)
        {
            Assert.True(throttle.TryAcquire("u:alice", 0, out _));
        }

        Assert.Equal(0, throttle.TrackedClients);
    }

    [Theory]
    [InlineData("550", "550")]
    [InlineData("tt0111161", "tt0111161")]
    [InlineData(" 1399 ", "1399")]
    [InlineData("550/../../evil", null)]
    [InlineData("1?apikey=x", null)]
    [InlineData("a\"b", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void ExternalIdsAreSanitizedBeforeUrlBuilding(string? raw, string? expected)
    {
        Assert.Equal(expected, RatingQuery.SanitizeId(raw));
    }

    [Fact]
    public void SanitizeIdRejectsOverlongValues()
    {
        Assert.Null(RatingQuery.SanitizeId(new string('1', 33)));
    }

    [Fact]
    public void ExpiredEntryIsServedAsStaleForBackgroundRefresh()
    {
        using var cache = new RatingCache(null, NullLogger<RatingCache>.Instance);
        cache.Set("k", new RatingsResponse { Title = "old" }, 720);

        // 直接把条目改成「已过期但仍在保留期」，模拟 CacheMinutes 到期
        var field = typeof(RatingCache).GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var items = (ConcurrentDictionary<string, RatingCacheEntry>)field.GetValue(cache)!;
        var now = DateTimeOffset.UtcNow;
        items["k"] = new RatingCacheEntry
        {
            Value = new RatingsResponse { Title = "old" },
            FetchedAt = now.AddDays(-1),
            ExpiresAt = now.AddMinutes(-5)
        };

        Assert.False(cache.TryGet("k", out _));
        Assert.True(cache.TryGetStale("k", out var stale, out _));
        Assert.Equal("old", stale!.Title);
    }

    [Fact]
    public void StaleEntryBeyondRetentionIsNotServed()
    {
        using var cache = new RatingCache(null, NullLogger<RatingCache>.Instance);
        var field = typeof(RatingCache).GetField("_items", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var items = (ConcurrentDictionary<string, RatingCacheEntry>)field.GetValue(cache)!;
        var now = DateTimeOffset.UtcNow;
        items["k"] = new RatingCacheEntry
        {
            Value = new RatingsResponse { Title = "ancient" },
            FetchedAt = now.AddDays(-30),
            ExpiresAt = now.AddDays(-29)
        };

        Assert.False(cache.TryGetStale("k", out _, out _));
        Assert.Equal(1, cache.Prune());
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void CacheStoresAndReturnsSameInstance()
    {
        var cache = new RatingCache(null, NullLogger<RatingCache>.Instance);
        var response = new RatingsResponse { Title = "Sample", MediaType = "movie", TmdbId = "550" };

        cache.Set("movie|550|-|-|-", response, 5);

        Assert.True(cache.TryGet("movie|550|-|-|-", out var cached));
        Assert.Same(response, cached);
        Assert.False(cache.TryGet("movie|999|-|-|-", out _));
    }

    [Fact]
    public void CacheIgnoresNonPositiveTtl()
    {
        var cache = new RatingCache(null, NullLogger<RatingCache>.Instance);
        cache.Set("k", new RatingsResponse(), 0);

        Assert.False(cache.TryGet("k", out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void CacheDoesNotRegressToOlderData()
    {
        using var cache = new RatingCache(null, NullLogger<RatingCache>.Instance);
        var now = DateTimeOffset.UtcNow;

        cache.Set("k", new RatingsResponse { Title = "new", FetchedAt = now }, 10);

        // 后台补全/过期刷新可能晚到：抓取时间更早的数据不能覆盖已有的新数据
        cache.Set("k", new RatingsResponse { Title = "old", FetchedAt = now.AddMinutes(-5) }, 10);

        Assert.True(cache.TryGet("k", out var kept));
        Assert.Equal("new", kept!.Title);
    }

    [Fact]
    public async Task AggregatorExplainsMissingProviderIds()
    {
        using var http = new RatingsHttp(NullLogger<RatingsHttp>.Instance);
        var aggregator = new RatingAggregator(
            http,
            new RatingCache(null, NullLogger<RatingCache>.Instance),
            new SourceHealth(),
            NullLogger<RatingAggregator>.Instance);

        var response = await aggregator.GetAsync(
            new RatingQuery { MediaType = "movie" },
            new PluginConfiguration(),
            CancellationToken.None);

        Assert.Contains("Provider ID", response.Message);
        Assert.Empty(response.Ratings);
    }

    [Theory]
    [InlineData("movie", "550", null, null, "movie|550|-|-|-")]
    [InlineData("show", "1399", "2", "5", "show|1399|-|2|5")]
    public void CacheKeyDistinguishesTypeSeasonEpisode(
        string mediaType,
        string tmdbId,
        string? season,
        string? episode,
        string expected)
    {
        var query = new RatingQuery
        {
            MediaType = mediaType,
            TmdbId = tmdbId,
            Season = season is null ? null : int.Parse(season),
            Episode = episode is null ? null : int.Parse(episode)
        };

        // 前缀是插件版本（升级即让旧缓存失效），这里只校验可区分的那一段
        Assert.EndsWith("|" + expected, query.CacheKey(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(86, 8.6)]
    [InlineData(9, 0.9)]
    [InlineData(100, 10)]
    public void MdbListTmdbValueIsPercentScale(double raw, double expected)
    {
        var entry = Assert.IsType<RatingEntry>(MapMdbList("tmdb", raw));

        Assert.Equal(expected, entry.Value!.Value, 3);
        Assert.Equal(10, entry.Scale);
        Assert.Equal("show", entry.Scope);
    }

    [Fact]
    public void MdbListMapsTomatoesAudienceAlias()
    {
        var entry = Assert.IsType<RatingEntry>(MapMdbList("tomatoesaudience", 72));

        Assert.Equal("Popcornmeter", entry.Source);
        Assert.Equal(72, entry.Value);
    }

    [Fact]
    public void ShowLevelTmdbIsStrippedButEpisodeRatingSurvives()
    {
        var response = new RatingsResponse();
        response.Ratings.Add(RatingEntry.Create("IMDb", "IMDb", 8.1, 10, null, 10));
        response.Ratings.Add(RatingEntry.Create("TMDb", "TMDB", 8.6, 10, null, 15, "show"));
        response.Ratings.Add(RatingEntry.Create("TMDb (Episode)", "TMDB", 8.2, 10, null, 15, "episode"));

        RatingAggregator.StripShowLevelRatings(response);

        // 整剧分必须让位；该集分、其它来源不受影响
        var tmdb = Assert.Single(response.Ratings, entry => entry.Logo == "TMDB");
        Assert.Equal("TMDb (Episode)", tmdb.Source);
        Assert.Equal(2, response.Ratings.Count);
    }

    [Fact]
    public void ShowLevelTmdbIsStrippedEvenWithoutEpisodeRating()
    {
        var response = new RatingsResponse();
        response.Ratings.Add(RatingEntry.Create("IMDb", "IMDb", 8.1, 10, null, 10));
        response.Ratings.Add(RatingEntry.Create("TMDb", "TMDB", 8.6, 10, null, 15, "show"));

        RatingAggregator.StripShowLevelRatings(response);

        Assert.DoesNotContain(response.Ratings, entry => entry.Logo == "TMDB");
        Assert.Single(response.Ratings);
    }

    private static object? MapMdbList(string source, double value)
    {
        var map = typeof(MdbListSource).GetMethod("Map", BindingFlags.NonPublic | BindingFlags.Static)!;
        return map.Invoke(null, new object[] { source, value });
    }

    [Fact]
    public void RatingEntryFormatsDisplayPerScale()
    {
        Assert.Equal("8.1", RatingEntry.Create("IMDb", "IMDb", 8.12, 10).Display);
        Assert.Equal("79", RatingEntry.Create("RT", "Rotten_Tomatoes", 79, 100).Display);
    }

    [Fact]
    public void ResponseSerializesToCamelCase()
    {
        var response = new RatingsResponse();
        response.Ratings.Add(RatingEntry.Create("IMDb", "IMDb", 8.1, 10));

        var json = JsonSerializer.Serialize(response, RatingsController.JsonOptions);

        Assert.Contains("\"ratings\"", json);
        Assert.Contains("\"source\":\"IMDb\"", json);
        Assert.Contains("\"display\":\"8.1\"", json);
    }

    [Fact]
    public void SourceHealthCoolsDownAfterThreshold()
    {
        var health = new SourceHealth();
        var logger = NullLogger.Instance;

        Assert.True(health.IsAvailable("douban"));

        for (var i = 0; i < 3; i++)
        {
            health.Report("douban", success: false, threshold: 3, cooldownMinutes: 10, logger);
        }

        Assert.False(health.IsAvailable("douban"));
        Assert.True(health.Snapshot().ContainsKey("douban"));

        // 一次成功即恢复
        health.Report("douban", success: true, threshold: 3, cooldownMinutes: 10, logger);
        Assert.True(health.IsAvailable("douban"));
    }
}
