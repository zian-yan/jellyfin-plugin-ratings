using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ratings.Api;
using Jellyfin.Plugin.Ratings.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings.Sources;

/// <summary>
/// AniList（番剧评分）。优先用 Wikidata P8729 精确反查，失败再按标题+年份检索，
/// 并要求标题完全一致，避免串到同名作品上（与原脚本策略一致）。
/// </summary>
public sealed class AniListSource : IRatingSource
{
    private const string Endpoint = "https://graphql.anilist.co";

    public string Key => "anilist";

    public int Order => 100;

    public bool IsEnabled(PluginConfiguration config) => config.EnableAniList;

    public async Task<SourceResult> FetchAsync(RatingQuery query, SourceContext context, CancellationToken cancellationToken)
    {
        var mediaId = await ResolveByWikidataAsync(query, context, cancellationToken).ConfigureAwait(false);

        if (mediaId.HasValue)
        {
            return await QueryByIdAsync(mediaId.Value, context, cancellationToken).ConfigureAwait(false);
        }

        var title = query.Title;
        if (string.IsNullOrWhiteSpace(title) && !string.IsNullOrWhiteSpace(query.ImdbId))
        {
            title = null; // 无标题且无 Wikidata 关联时不做模糊匹配
        }

        if (!string.IsNullOrWhiteSpace(title) && query.Year is > 0)
        {
            return await QueryBySearchAsync(title!, query.Year!.Value, context, cancellationToken).ConfigureAwait(false);
        }

        return SourceResult.Empty();
    }

    private async Task<int?> ResolveByWikidataAsync(RatingQuery query, SourceContext context, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(query.ImdbId))
        {
            return null;
        }

        var raw = await context.Wikidata.GetAniListIdAsync(query.ImdbId!, context.Config, context.Logger, ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var digits = new string(Array.FindAll(raw!.ToCharArray(), char.IsDigit));
        return int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    private async Task<SourceResult> QueryByIdAsync(int id, SourceContext context, CancellationToken ct)
    {
        const string Query = "query($id:Int){ Media(id:$id,type:ANIME){ id meanScore } }";
        return await PostAsync(Query, new { id }, true, null, context, ct).ConfigureAwait(false);
    }

    private async Task<SourceResult> QueryBySearchAsync(string title, int year, SourceContext context, CancellationToken ct)
    {
        const string Query = @"query($search:String,$startDate:FuzzyDateInt,$endDate:FuzzyDateInt){
            Media(search:$search,type:ANIME,startDate_greater:$startDate,startDate_lesser:$endDate){
              id meanScore title { romaji english native } startDate { year }
            }
          }";

        var variables = new
        {
            search = title,
            startDate = year * 10000 + 101,
            endDate = (year + 1) * 10000 + 101
        };

        return await PostAsync(Query, variables, false, (title, year), context, ct).ConfigureAwait(false);
    }

    private async Task<SourceResult> PostAsync(
        string query,
        object variables,
        bool trustResult,
        (string Title, int Year)? exactMatch,
        SourceContext context,
        CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { query, variables });
        var response = await context.Http
            .PostJsonAsync(Endpoint, body, context.Config, context.Logger, null, ct)
            .ConfigureAwait(false);

        if (!response.Ok)
        {
            return SourceResult.Failed($"anilist: HTTP {response.Status} {response.Error}".Trim());
        }

        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            if (!doc.RootElement.TryGetProperty("data", out var data)
                || !data.TryGetProperty("Media", out var media)
                || media.ValueKind != JsonValueKind.Object)
            {
                return SourceResult.Empty();
            }

            if (!media.TryGetProperty("meanScore", out var scoreProperty)
                || !scoreProperty.TryGetDouble(out var score)
                || score <= 0)
            {
                return SourceResult.Empty();
            }

            if (!trustResult && exactMatch is { } match)
            {
                if (!media.TryGetProperty("startDate", out var startDate)
                    || !startDate.TryGetProperty("year", out var yearProperty)
                    || !yearProperty.TryGetInt32(out var foundYear)
                    || foundYear != match.Year)
                {
                    return SourceResult.Empty();
                }

                if (!TitleMatches(media, match.Title))
                {
                    return SourceResult.Empty();
                }
            }

            var result = new SourceResult();
            result.Ratings.Add(RatingEntry.Create("AniList", Logos.AniList, score, 10, null, 100));
            return result;
        }
        catch (JsonException ex)
        {
            context.Logger.LogDebug(ex, "[Ratings] AniList 响应解析失败");
            return SourceResult.Failed("anilist: 响应解析失败");
        }
    }

    private static bool TitleMatches(JsonElement media, string title)
    {
        if (!media.TryGetProperty("title", out var titleNode) || titleNode.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var normalized = title.Trim().ToLowerInvariant();
        foreach (var field in new[] { "romaji", "english", "native" })
        {
            if (titleNode.TryGetProperty(field, out var value)
                && value.ValueKind == JsonValueKind.String
                && string.Equals(value.GetString()?.Trim().ToLowerInvariant(), normalized, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
