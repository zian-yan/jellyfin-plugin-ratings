using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ratings.Api;
using Jellyfin.Plugin.Ratings.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings.Sources;

/// <summary>
/// AlloCiné（法国）影评人/观众评分。Wikidata 反查 film/serie id 后抓取详情页
/// 的 .stareval-note（5 分制），换算成百分制展示。
/// </summary>
public sealed partial class AllocineSource : IRatingSource
{
    public string Key => "allocine";

    public int Order => 130;

    public bool IsEnabled(PluginConfiguration config) => config.EnableAllocine;

    public async Task<SourceResult> FetchAsync(RatingQuery query, SourceContext context, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(query.ImdbId))
        {
            return SourceResult.Empty();
        }

        var (id, isFilm) = await context.Wikidata
            .GetAllocineIdAsync(query.ImdbId!, context.Config, context.Logger, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(id))
        {
            return SourceResult.Empty();
        }

        var path = isFilm ? $"film/fichefilm_gen_cfilm={id}" : $"series/ficheserie_gen_cserie={id}";
        var url = $"https://www.allocine.fr/{path}.html";

        var page = await context.Http
            .GetAsync(url, context.Config, context.Logger, new Dictionary<string, string> { ["Accept-Language"] = "fr-FR,fr;q=0.9" }, cancellationToken)
            .ConfigureAwait(false);

        if (!page.Ok)
        {
            return SourceResult.Failed($"allocine: HTTP {page.Status} {page.Error}".Trim());
        }

        var notes = NoteRegex().Matches(page.Body);
        if (notes.Count == 0)
        {
            return SourceResult.Empty();
        }

        var result = new SourceResult();

        Add(result, notes[0].Groups[1].Value, "AlloCiné 影评人", Logos.AllocineCritics, 131);
        if (notes.Count > 1)
        {
            Add(result, notes[1].Groups[1].Value, "AlloCiné 观众", Logos.AllocineUsers, 132);
        }

        return result;

        static void Add(SourceResult target, string raw, string label, string logo, int order)
        {
            var value = SourceParse.ToDouble(raw);
            if (value is null or <= 0)
            {
                return;
            }

            var percent = Math.Round(value.Value * 20);
            var entry = RatingEntry.Create(label, logo, percent, 100, null, order);
            target.Ratings.Add(entry);
        }
    }

    [GeneratedRegex(@"stareval-note[^>]*>\s*([\d,.]+)\s*<", RegexOptions.Compiled)]
    private static partial Regex NoteRegex();
}
