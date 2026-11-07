using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ratings.Api;
using Jellyfin.Plugin.Ratings.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings.Sources;

/// <summary>logo 资源名常量，与仓库 logo/*.png 一一对应。</summary>
public static class Logos
{
    public const string Imdb = "IMDb";
    public const string Tmdb = "TMDB";
    public const string Tomatoes = "Rotten_Tomatoes";
    public const string TomatoesRotten = "Rotten_Tomatoes_rotten";
    public const string Audience = "Rotten_Tomatoes_positive_audience";
    public const string AudienceRotten = "Rotten_Tomatoes_negative_audience";
    public const string Metacritic = "Metacritic";
    public const string MetacriticUser = "mus2";
    public const string MetacriticMustSee = "metacriticms";
    public const string RogerEbert = "Roger_Ebert";
    public const string Trakt = "Trakt";
    public const string Letterboxd = "letterboxd";
    public const string Mal = "mal";
    public const string AniList = "anilist";
    public const string Kinopoisk = "kinopoisk";
    public const string Douban = "douban";
    public const string AllocineCritics = "allocine_crit";
    public const string AllocineUsers = "allocine_user";
    public const string TomatoesCertified = "rotten-tomatoes-certified";
    public const string AudienceCertified = "roten_tomatoes_ver";
}

/// <summary>
/// MDBList 数据源（核心）。原脚本的 8 个评分来源绝大多数来自这一个接口，
/// 因此未配置它时插件只能提供 TMDb 单集/整剧评分。
/// </summary>
public sealed class MdbListSource : IRatingSource
{
    public string Key => "mdblist";

    public int Order => 10;

    public bool IsEnabled(PluginConfiguration config) => config.EnableMdbList;

    public async Task<SourceResult> FetchAsync(RatingQuery query, SourceContext context, CancellationToken cancellationToken)
    {
        var apiKey = context.Config.MdbListApiKey?.Trim();
        if (string.IsNullOrEmpty(apiKey))
        {
            return SourceResult.Failed("mdblist: 未配置 API Key");
        }

        string url;
        if (!string.IsNullOrWhiteSpace(query.TmdbId))
        {
            url = $"https://api.mdblist.com/tmdb/{query.MediaType}/{query.TmdbId}?apikey={Uri.EscapeDataString(apiKey)}";
        }
        else if (!string.IsNullOrWhiteSpace(query.ImdbId))
        {
            url = $"https://api.mdblist.com/imdb/{query.MediaType}/{query.ImdbId}?apikey={Uri.EscapeDataString(apiKey)}";
        }
        else
        {
            return SourceResult.Failed("mdblist: 缺少 TMDb/IMDb ID");
        }

        var response = await context.Http.GetAsync(url, context.Config, context.Logger, null, cancellationToken)
            .ConfigureAwait(false);

        if (!response.Ok)
        {
            return SourceResult.Failed($"mdblist: HTTP {response.Status} {response.Error}".Trim());
        }

        try
        {
            using var doc = JsonDocument.Parse(response.Body);
            var root = doc.RootElement;
            var result = new SourceResult
            {
                Title = ReadString(root, "title") ?? ReadString(root, "original_title"),
                Year = ReadInt(root, "year")
            };

            if (root.TryGetProperty("ratings", out var ratings) && ratings.ValueKind == JsonValueKind.Array)
            {
                foreach (var rating in ratings.EnumerateArray())
                {
                    var sourceName = ReadString(rating, "source");
                    var value = ReadDouble(rating, "value");
                    if (string.IsNullOrWhiteSpace(sourceName) || value is null or <= 0)
                    {
                        continue;
                    }

                    var entry = Map(sourceName!, value.Value);
                    if (entry is not null)
                    {
                        result.Ratings.Add(entry);
                    }
                }
            }

            result.Ratings.Sort((a, b) => a.Order.CompareTo(b.Order));
            return result;
        }
        catch (JsonException ex)
        {
            context.Logger.LogDebug(ex, "[Ratings] MDBList 响应解析失败");
            return SourceResult.Failed("mdblist: 响应解析失败");
        }
    }

    /// <summary>
    /// 与用户脚本保持一致的来源 → logo/量纲映射。
    /// </summary>
    private static RatingEntry? Map(string sourceName, double value)
    {
        var key = sourceName.ToLowerInvariant().Replace(' ', '_');

        return key switch
        {
            "imdb" => RatingEntry.Create("IMDb", Logos.Imdb, value, 10, "https://www.imdb.com/title/", 10),
            "tomatoes" => value < 60
                ? RatingEntry.Create("Rotten Tomatoes", Logos.TomatoesRotten, value, 100, null, 20)
                : RatingEntry.Create("Rotten Tomatoes", Logos.Tomatoes, value, 100, null, 20),
            "popcorn" or "popcornmeter" => value < 60
                ? RatingEntry.Create("Popcornmeter", Logos.AudienceRotten, value, 100, null, 30)
                : RatingEntry.Create("Popcornmeter", Logos.Audience, value, 100, null, 30),
            "metacritic" => RatingEntry.Create("Metacritic", Logos.Metacritic, value, 100, null, 40),
            "metacriticuser" or "metacritic_user" => RatingEntry.Create("Metacritic User", Logos.MetacriticUser, value, 10, null, 50),
            "tmdb" => RatingEntry.Create("TMDb", Logos.Tmdb, NormalizeTmdb(value), 10, null, 15),
            "rogerebert" or "roger_ebert" => RatingEntry.Create("Roger Ebert", Logos.RogerEbert, value, 4, null, 60),
            "trakt" => RatingEntry.Create("Trakt", Logos.Trakt, value, 100, null, 70),
            "letterboxd" => RatingEntry.Create("Letterboxd", Logos.Letterboxd, value, 5, null, 80),
            "myanimelist" => RatingEntry.Create("MyAnimeList", Logos.Mal, value, 10, null, 90),
            _ => null
        };
    }

    /// <summary>
    /// MDBList 的 tmdb 评分按百分制返回（8.6 分 → 86），其余来源多是原始量纲，
    /// 因此只有大于 10 的值才折算回 10 分制，避免把 0-10 的值误除。
    /// </summary>
    private static double NormalizeTmdb(double value) => value > 10 ? value / 10 : value;

    private static string? ReadString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }

        return property.ValueKind switch
        {
            JsonValueKind.String => property.GetString(),
            JsonValueKind.Number => property.ToString(),
            _ => null
        };
    }

    private static int? ReadInt(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetInt32(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String && int.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static double? ReadDouble(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var property))
        {
            return null;
        }

        if (property.ValueKind == JsonValueKind.Number && property.TryGetDouble(out var number))
        {
            return number;
        }

        if (property.ValueKind == JsonValueKind.String && double.TryParse(property.GetString(), out var parsed))
        {
            return parsed;
        }

        return null;
    }
}
