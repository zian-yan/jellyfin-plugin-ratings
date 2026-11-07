using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ratings.Configuration;
using Jellyfin.Plugin.Ratings.Core;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings.Sources;

/// <summary>
/// Wikidata SPARQL 封装。原脚本用它对 IMDb ID 反查 AniList / RottenTomatoes /
/// AlloCiné / 豆瓣 的外部 ID，这里保留同样的策略，但由服务端发起，无需 CORS 代理。
/// </summary>
public sealed class WikidataHelper
{
    private const string Endpoint = "https://query.wikidata.org/sparql?format=json&query=";

    private readonly RatingsHttp _http;

    public WikidataHelper(RatingsHttp http)
    {
        _http = http;
    }

    /// <summary>
    /// 执行 SPARQL 并返回第一行第一个非空绑定值。
    /// </summary>
    public async Task<string?> QueryFirstValueAsync(
        string sparql,
        PluginConfiguration config,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var url = Endpoint + Uri.EscapeDataString(sparql);
        var result = await _http.GetAsync(
            url,
            config,
            logger,
            new System.Collections.Generic.Dictionary<string, string> { ["Accept"] = "application/sparql-results+json" },
            cancellationToken).ConfigureAwait(false);

        if (!result.Ok)
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(result.Body);
            if (!doc.RootElement.TryGetProperty("results", out var results)
                || !results.TryGetProperty("bindings", out var bindings)
                || bindings.ValueKind != JsonValueKind.Array
                || bindings.GetArrayLength() == 0)
            {
                return null;
            }

            foreach (var binding in bindings[0].EnumerateObject())
            {
                if (binding.Value.TryGetProperty("value", out var value)
                    && value.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(value.GetString()))
                {
                    return value.GetString();
                }
            }
        }
        catch (JsonException ex)
        {
            logger.LogDebug(ex, "[Ratings] Wikidata 响应解析失败");
        }

        return null;
    }

    public Task<string?> GetAniListIdAsync(string imdbId, PluginConfiguration config, ILogger logger, CancellationToken ct)
        => QueryFirstValueAsync(
            $@"SELECT ?anilist WHERE {{ ?item wdt:P345 ""{Escape(imdbId)}"" . ?item wdt:P8729 ?anilist . }} LIMIT 1",
            config, logger, ct);

    public Task<string?> GetRottenTomatoesIdAsync(string imdbId, PluginConfiguration config, ILogger logger, CancellationToken ct)
        => QueryFirstValueAsync(
            $@"SELECT ?rtid WHERE {{ ?item wdt:P345 ""{Escape(imdbId)}"" . ?item wdt:P1258 ?rtid . }} LIMIT 1",
            config, logger, ct);

    public Task<string?> GetDoubanIdAsync(string imdbId, PluginConfiguration config, ILogger logger, CancellationToken ct)
        => QueryFirstValueAsync(
            $@"SELECT ?douban WHERE {{ ?item wdt:P345 ""{Escape(imdbId)}"" . ?item wdt:P4529 ?douban . }} LIMIT 1",
            config, logger, ct);

    /// <summary>返回 (id, isFilm)。</summary>
    public async Task<(string? Id, bool IsFilm)> GetAllocineIdAsync(
        string imdbId,
        PluginConfiguration config,
        ILogger logger,
        CancellationToken ct)
    {
        var sparql =
            $@"SELECT ?film ?serie WHERE {{ ?item wdt:P345 ""{Escape(imdbId)}"" . "
            + "OPTIONAL { ?item wdt:P1265 ?film. } OPTIONAL { ?item wdt:P1267 ?serie. } } LIMIT 1";

        var url = Endpoint + Uri.EscapeDataString(sparql);
        var result = await _http.GetAsync(
            url,
            config,
            logger,
            new System.Collections.Generic.Dictionary<string, string> { ["Accept"] = "application/sparql-results+json" },
            ct).ConfigureAwait(false);

        if (!result.Ok)
        {
            return (null, true);
        }

        try
        {
            using var doc = JsonDocument.Parse(result.Body);
            var binding = doc.RootElement.GetProperty("results").GetProperty("bindings");
            if (binding.GetArrayLength() == 0)
            {
                return (null, true);
            }

            var row = binding[0];
            if (row.TryGetProperty("film", out var film) && film.TryGetProperty("value", out var filmValue))
            {
                return (StripAllocineId(filmValue.GetString()), true);
            }

            if (row.TryGetProperty("serie", out var serie) && serie.TryGetProperty("value", out var serieValue))
            {
                return (StripAllocineId(serieValue.GetString()), false);
            }
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            logger.LogDebug(ex, "[Ratings] AlloCiné SPARQL 解析失败");
        }

        return (null, true);
    }

    private static string Escape(string raw) => raw.Replace("\"", string.Empty, StringComparison.Ordinal);

    private static string? StripAllocineId(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        // P1265 可能是纯数字，也可能是 ...gen_cfilm=12345.html 形式
        var match = System.Text.RegularExpressions.Regex.Match(raw, @"=(\d+)");
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        var digits = new string(Array.FindAll(raw.ToCharArray(), char.IsDigit));
        return digits.Length > 0 ? digits : null;
    }
}
