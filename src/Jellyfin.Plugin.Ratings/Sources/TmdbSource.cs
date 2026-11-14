using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ratings.Api;
using Jellyfin.Plugin.Ratings.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings.Sources;

/// <summary>
/// TMDb 数据源：负责单集 / 单季评分（依赖 Jellyfin 12 提供的分集元数据），
/// 同时承担 IMDb ID 补齐（MDBList 用 IMDb 兜底、以及各类外链反查都需要它）。
/// </summary>
public sealed class TmdbSource : IRatingSource
{
    private const string BaseUrl = "https://api.themoviedb.org/3";

    public string Key => "tmdb";

    public int Order => 5;

    public bool IsEnabled(PluginConfiguration config) => config.EnableTmdb;

    public async Task<SourceResult> FetchAsync(RatingQuery query, SourceContext context, CancellationToken cancellationToken)
    {
        var apiKey = context.Config.TmdbApiKey?.Trim();
        if (string.IsNullOrEmpty(apiKey))
        {
            return SourceResult.Failed("tmdb: 未配置 API Key");
        }

        if (string.IsNullOrWhiteSpace(query.TmdbId))
        {
            return SourceResult.Empty();
        }

        string? url = null;
        string label = string.Empty;

        if (query.IsEpisode && query.MediaType == "show")
        {
            url = $"{BaseUrl}/tv/{query.TmdbId}/season/{query.Season}/episode/{query.Episode}?api_key={Uri.EscapeDataString(apiKey)}";
            label = "Episode";
        }
        else if (query.IsSeason && query.MediaType == "show")
        {
            url = $"{BaseUrl}/tv/{query.TmdbId}/season/{query.Season}?api_key={Uri.EscapeDataString(apiKey)}";
            label = "Season";
        }

        if (url is null)
        {
            return SourceResult.Empty();
        }

        var response = await context.Http.GetAsync(url, context.Config, context.Logger, null, cancellationToken)
            .ConfigureAwait(false);

        if (!response.Ok)
        {
            return SourceResult.Failed($"tmdb: HTTP {response.Status} {response.Error}".Trim(), response.Status);
        }

        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            var root = doc.RootElement;

            if (!root.TryGetProperty("vote_average", out var avgProperty)
                || !avgProperty.TryGetDouble(out var average)
                || average <= 0)
            {
                return SourceResult.Empty();
            }

            var votes = root.TryGetProperty("vote_count", out var countProperty) && countProperty.TryGetDouble(out var c)
                ? c
                : 0;

            if (votes <= 0)
            {
                // 没有票数时不展示，避免出现 0.0 这种脏数据（也避免前端误隐藏内置星级）
                return SourceResult.Empty();
            }

            var entry = RatingEntry.Create($"TMDb ({label})", Logos.Tmdb, average, 10, null, 15, label.ToLowerInvariant());
            entry.Display = average.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

            var result = new SourceResult();
            result.Ratings.Add(entry);
            return result;
        }
        catch (JsonException ex)
        {
            context.Logger.LogDebug(ex, "[Ratings] TMDb 响应解析失败");
            return SourceResult.Failed("tmdb: 响应解析失败");
        }
    }

    /// <summary>
    /// 通过 TMDb external_ids 反查 IMDb ID。Jellyfin 里部分条目只有 TMDb ID，
    /// 而 MDBList / Wikidata 反查都需要 IMDb ID。
    /// </summary>
    public async Task<string?> ResolveImdbIdAsync(
        RatingQuery query,
        SourceContext context,
        CancellationToken cancellationToken)
    {
        var apiKey = context.Config.TmdbApiKey?.Trim();
        if (string.IsNullOrEmpty(apiKey) || string.IsNullOrWhiteSpace(query.TmdbId))
        {
            return null;
        }

        var kind = query.MediaType == "show" ? "tv" : "movie";
        var url = $"{BaseUrl}/{kind}/{query.TmdbId}/external_ids?api_key={Uri.EscapeDataString(apiKey)}";

        var response = await context.Http.GetAsync(url, context.Config, context.Logger, null, cancellationToken)
            .ConfigureAwait(false);

        if (!response.Ok)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            if (doc.RootElement.TryGetProperty("imdb_id", out var imdb)
                && imdb.ValueKind == JsonValueKind.String)
            {
                var value = imdb.GetString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
        }
        catch (JsonException ex)
        {
            context.Logger.LogDebug(ex, "[Ratings] TMDb external_ids 解析失败");
        }

        return null;
    }
}
