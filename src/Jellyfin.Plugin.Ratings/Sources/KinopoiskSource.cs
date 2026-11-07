using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ratings.Api;
using Jellyfin.Plugin.Ratings.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings.Sources;

/// <summary>Kinopoisk（俄语区评分），按标题 + 年份检索后取第一条匹配类型的记录。</summary>
public sealed class KinopoiskSource : IRatingSource
{
    private const string Endpoint = "https://kinopoiskapiunofficial.tech/api/v2.2/films";

    public string Key => "kinopoisk";

    public int Order => 110;

    public bool IsEnabled(PluginConfiguration config) => config.EnableKinopoisk;

    public async Task<SourceResult> FetchAsync(RatingQuery query, SourceContext context, CancellationToken cancellationToken)
    {
        var apiKey = context.Config.KinopoiskApiKey?.Trim();
        if (string.IsNullOrEmpty(apiKey))
        {
            return SourceResult.Failed("kinopoisk: 未配置 API Key");
        }

        if (string.IsNullOrWhiteSpace(query.Title) || query.Year is null or <= 0)
        {
            return SourceResult.Empty();
        }

        var url = $"{Endpoint}?keyword={Uri.EscapeDataString(query.Title!)}&yearFrom={query.Year}&yearTo={query.Year}";
        var headers = new Dictionary<string, string> { ["X-API-KEY"] = apiKey };

        var response = await context.Http.GetAsync(url, context.Config, context.Logger, headers, cancellationToken)
            .ConfigureAwait(false);

        if (!response.Ok)
        {
            return SourceResult.Failed($"kinopoisk: HTTP {response.Status} {response.Error}".Trim());
        }

        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            if (!doc.RootElement.TryGetProperty("items", out var items)
                || items.ValueKind != JsonValueKind.Array
                || items.GetArrayLength() == 0)
            {
                return SourceResult.Empty();
            }

            var desiredType = query.MediaType == "show" ? "TV_SERIES" : "FILM";
            JsonElement? match = null;

            foreach (var item in items.EnumerateArray())
            {
                var type = item.TryGetProperty("type", out var typeProperty) ? typeProperty.GetString() : null;
                if (string.Equals(type, desiredType, StringComparison.OrdinalIgnoreCase))
                {
                    match = item;
                    break;
                }
            }

            match ??= items[0];

            if (!match.Value.TryGetProperty("ratingKinopoisk", out var ratingProperty)
                || ratingProperty.ValueKind != JsonValueKind.Number
                || !ratingProperty.TryGetDouble(out var rating)
                || rating <= 0)
            {
                return SourceResult.Empty();
            }

            var result = new SourceResult();
            result.Ratings.Add(RatingEntry.Create("Kinopoisk", Logos.Kinopoisk, rating, 10, null, 110));
            return result;
        }
        catch (JsonException ex)
        {
            context.Logger.LogDebug(ex, "[Ratings] Kinopoisk 响应解析失败");
            return SourceResult.Failed("kinopoisk: 响应解析失败");
        }
    }
}
