using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ratings.Api;
using Jellyfin.Plugin.Ratings.Configuration;
using Jellyfin.Plugin.Ratings.Core;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings.Sources;

/// <summary>
/// 豆瓣评分。按「IMDb ID / 原始标题 → 豆瓣 subject id → 详情页 rating_num」的顺序解析，
/// 每个环节失败都退回下一策略，全部失败则静默跳过（与原脚本行为一致）。
/// </summary>
public sealed partial class DoubanSource : IRatingSource
{
    private static readonly Dictionary<string, string> DoubanHeaders = new()
    {
        ["Referer"] = "https://movie.douban.com/"
    };

    public string Key => "douban";

    public int Order => 120;

    public bool IsEnabled(PluginConfiguration config) => config.EnableDouban;

    public async Task<SourceResult> FetchAsync(RatingQuery query, SourceContext context, CancellationToken cancellationToken)
    {
        var subjectId = await ResolveSubjectIdAsync(query, context, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(subjectId))
        {
            return SourceResult.Empty();
        }

        var url = $"https://movie.douban.com/subject/{subjectId}/";
        var page = await context.Http.GetAsync(url, context.Config, context.Logger, DoubanHeaders, cancellationToken)
            .ConfigureAwait(false);

        if (!page.Ok)
        {
            return SourceResult.Failed($"douban: HTTP {page.Status} {page.Error}".Trim());
        }

        var match = RatingNumRegex().Match(page.Body);
        if (!match.Success)
        {
            return SourceResult.Empty();
        }

        var score = SourceParse.ToDouble(match.Groups[1].Value);
        if (score is null or <= 0)
        {
            return SourceResult.Empty();
        }

        // 豆瓣是 10 分制，原脚本乘 10 变成 100 分制展示，这里保持一致
        var result = new SourceResult();
        var entry = RatingEntry.Create("Douban", Logos.Douban, score.Value * 10, 100, url, 120);
        entry.Display = (score.Value * 10).ToString("0", System.Globalization.CultureInfo.InvariantCulture);
        result.Ratings.Add(entry);
        return result;
    }

    private async Task<string?> ResolveSubjectIdAsync(RatingQuery query, SourceContext context, CancellationToken ct)
    {
        // 策略 1：豆瓣 subject_suggest（最快）
        if (!string.IsNullOrWhiteSpace(query.Title))
        {
            var suggestUrl = "https://movie.douban.com/j/subject_suggest?q=" + Uri.EscapeDataString(query.Title!);
            var suggest = await context.Http.GetAsync(suggestUrl, context.Config, context.Logger, DoubanHeaders, ct)
                .ConfigureAwait(false);

            if (suggest.Ok)
            {
                try
                {
                    using var doc = JsonDocument.Parse(suggest.Body);
                    if (doc.RootElement.ValueKind == JsonValueKind.Array && doc.RootElement.GetArrayLength() > 0)
                    {
                        var first = doc.RootElement[0];
                        if (first.TryGetProperty("url", out var urlProperty)
                            && urlProperty.GetString() is { } link)
                        {
                            var m = SubjectUrlRegex().Match(link);
                            if (m.Success)
                            {
                                return m.Groups[1].Value;
                            }
                        }
                    }
                }
                catch (JsonException ex)
                {
                    context.Logger.LogDebug(ex, "[Ratings] 豆瓣 suggest 解析失败");
                }
            }
        }

        // 策略 2：Wikidata P4529（最准）
        if (!string.IsNullOrWhiteSpace(query.ImdbId))
        {
            var raw = await context.Wikidata.GetDoubanIdAsync(query.ImdbId!, context.Config, context.Logger, ct)
                .ConfigureAwait(false);
            if (!string.IsNullOrWhiteSpace(raw))
            {
                var m = SubjectUrlRegex().Match(raw!);
                return m.Success ? m.Groups[1].Value : new string(Array.FindAll(raw!.ToCharArray(), char.IsDigit));
            }
        }

        // 策略 3：站内搜索页
        if (!string.IsNullOrWhiteSpace(query.Title))
        {
            var searchUrl = "https://movie.douban.com/subject_search?search_text="
                + Uri.EscapeDataString(query.Title!);
            var search = await context.Http.GetAsync(searchUrl, context.Config, context.Logger, DoubanHeaders, ct)
                .ConfigureAwait(false);

            if (search.Ok)
            {
                var m = SubjectUrlRegex().Match(search.Body);
                if (m.Success)
                {
                    return m.Groups[1].Value;
                }
            }
        }

        return null;
    }

    [GeneratedRegex(@"class=""rating_num""[^>]*>\s*([\d.]+)", RegexOptions.Compiled)]
    private static partial Regex RatingNumRegex();

    [GeneratedRegex(@"subject/(\d+)", RegexOptions.Compiled)]
    private static partial Regex SubjectUrlRegex();
}
