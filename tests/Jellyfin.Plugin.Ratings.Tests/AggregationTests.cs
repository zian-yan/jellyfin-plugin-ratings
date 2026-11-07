using System.Text.Json;
using Jellyfin.Plugin.Ratings.Api;
using Jellyfin.Plugin.Ratings.Configuration;
using Jellyfin.Plugin.Ratings.Core;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Jellyfin.Plugin.Ratings.Tests;

/// <summary>缓存、聚合器兜底路径与 JSON 契约，全部离线执行。</summary>
public class AggregationTests
{
    [Fact]
    public void CacheStoresAndReturnsSameInstance()
    {
        var cache = new RatingCache(NullLogger<RatingCache>.Instance);
        var response = new RatingsResponse { Title = "Sample", MediaType = "movie", TmdbId = "550" };

        cache.Set("movie|550|-|-|-", response, 5);

        Assert.True(cache.TryGet("movie|550|-|-|-", out var cached));
        Assert.Same(response, cached);
        Assert.False(cache.TryGet("movie|999|-|-|-", out _));
    }

    [Fact]
    public void CacheIgnoresNonPositiveTtl()
    {
        var cache = new RatingCache(NullLogger<RatingCache>.Instance);
        cache.Set("k", new RatingsResponse(), 0);

        Assert.False(cache.TryGet("k", out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public async Task AggregatorExplainsMissingProviderIds()
    {
        using var http = new RatingsHttp(NullLogger<RatingsHttp>.Instance);
        var aggregator = new RatingAggregator(
            http,
            new RatingCache(NullLogger<RatingCache>.Instance),
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

        Assert.Equal(expected, query.CacheKey());
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
