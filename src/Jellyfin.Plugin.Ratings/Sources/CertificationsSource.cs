using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ratings.Api;
using Jellyfin.Plugin.Ratings.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings.Sources;

/// <summary>
/// 认证角标：烂番茄「新鲜认证 / 观众好评认证」与 Metacritic「必看」标记。
/// 这三项不产生数值评分，只输出 badges，由前端替换对应 logo 图标。
/// </summary>
public sealed partial class CertificationsSource : IRatingSource
{
    public string Key => "certifications";

    public int Order => 200;

    public bool IsEnabled(PluginConfiguration config) => config.EnableCertifications;

    public async Task<SourceResult> FetchAsync(RatingQuery query, SourceContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.ImdbId))
        {
            return SourceResult.Empty();
        }

        var result = new SourceResult();
        var imdbId = query.ImdbId!;

        await AddRottenTomatoesBadgesAsync(imdbId, result, context, cancellationToken).ConfigureAwait(false);
        await AddMetacriticBadgeAsync(imdbId, result, context, cancellationToken).ConfigureAwait(false);

        return result;
    }

    private async Task AddRottenTomatoesBadgesAsync(
        string imdbId,
        SourceResult result,
        SourceContext context,
        CancellationToken ct)
    {
        var rtId = await context.Wikidata.GetRottenTomatoesIdAsync(imdbId, context.Config, context.Logger, ct)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(rtId))
        {
            return;
        }

        var url = rtId!.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? rtId
            : "https://www.rottentomatoes.com/" + rtId.TrimStart('/');

        var page = await context.Http.GetAsync(url, context.Config, context.Logger, null, ct).ConfigureAwait(false);
        if (!page.Ok)
        {
            return;
        }

        var script = ScorecardRegex().Match(page.Body);
        if (!script.Success)
        {
            return;
        }

        var json = script.Groups[1].Value;

        if (json.Contains("\"certified\":true", StringComparison.Ordinal)
            && json.Contains("criticsScore", StringComparison.Ordinal))
        {
            result.Badges.Add("tomatoes_certified");
        }

        if (json.Contains("POSITIVE\",\"certified\":true", StringComparison.Ordinal))
        {
            result.Badges.Add("audience_certified");
        }
    }

    private async Task AddMetacriticBadgeAsync(
        string imdbId,
        SourceResult result,
        SourceContext context,
        CancellationToken ct)
    {
        var url = $"https://www.imdb.com/title/{imdbId}/criticreviews";
        var page = await context.Http.GetAsync(url, context.Config, context.Logger, null, ct).ConfigureAwait(false);
        if (!page.Ok)
        {
            return;
        }

        // 原脚本用 DOMParser 取 critic-reviews-title 下的两个数字：Metascore 与影评数
        var block = CriticBlockRegex().Match(page.Body);
        if (!block.Success)
        {
            return;
        }

        var numbers = NumberRegex().Matches(block.Groups[1].Value);
        if (numbers.Count < 2)
        {
            return;
        }

        var metascore = ParseInt(numbers[0].Value);
        var reviewCount = ParseInt(numbers[1].Value);

        if (metascore > 80 && reviewCount > 14)
        {
            result.Badges.Add("metacritic_must_see");
        }
    }

    private static int ParseInt(string raw)
        => int.TryParse(new string(Array.FindAll(raw.ToCharArray(), char.IsDigit)), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value)
            ? value
            : 0;

    [GeneratedRegex("<script\\s+id=\"media-scorecard-json\"[^>]*>([\\s\\S]*?)</script>", RegexOptions.Compiled)]
    private static partial Regex ScorecardRegex();

    [GeneratedRegex("data-testid=\"critic-reviews-title\"([\\s\\S]{0,600})", RegexOptions.Compiled)]
    private static partial Regex CriticBlockRegex();

    [GeneratedRegex("\\d[\\d,]*(?:\\.\\d+)?", RegexOptions.Compiled)]
    private static partial Regex NumberRegex();
}
