using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Jellyfin.Plugin.Ratings.Api;

/// <summary>单个来源的评分。</summary>
public sealed class RatingEntry
{
    /// <summary>Gets or sets 内部来源标识（imdb / tomatoes / metacritic ...）。</summary>
    public string Source { get; set; } = string.Empty;

    /// <summary>Gets or sets logo 资源名（对应 logo/*.png 文件名）。</summary>
    public string Logo { get; set; } = string.Empty;

    /// <summary>Gets or sets 原始分值。</summary>
    public double? Value { get; set; }

    /// <summary>Gets or sets 分值上限（10 / 100 / 5），前端据此格式化。</summary>
    public int? Scale { get; set; }

    /// <summary>Gets or sets 展示文本，保持与来源一致（如 "8.1"、"92"）。</summary>
    public string Display { get; set; } = string.Empty;

    /// <summary>Gets or sets 详情链接。</summary>
    public string? Url { get; set; }

    /// <summary>Gets or sets 展示顺序，越小越靠前。</summary>
    public int Order { get; set; } = 100;

    public static RatingEntry Create(string source, string logo, double? value, int? scale, string? url = null, int order = 100)
    {
        return new RatingEntry
        {
            Source = source,
            Logo = logo,
            Value = value,
            Scale = scale,
            Display = value is null
                ? string.Empty
                : value.Value.ToString(scale is 100 or 5 ? "0.#" : "0.0", CultureInfo.InvariantCulture),
            Url = url,
            Order = order
        };
    }
}

/// <summary>聚合后的评分响应。</summary>
public sealed class RatingsResponse
{
    public Guid? ItemId { get; set; }

    public string MediaType { get; set; } = "movie";

    public string? TmdbId { get; set; }

    public string? ImdbId { get; set; }

    public string? Title { get; set; }

    public int? Year { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public List<RatingEntry> Ratings { get; set; } = new();

    /// <summary>Gets or sets 认证角标，如 tomatoes_certified。</summary>
    public List<string> Badges { get; set; } = new();

    public bool FromCache { get; set; }

    public DateTimeOffset FetchedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Gets or sets 各数据源的失败摘要，便于排障（可用 ExposeErrors 关闭）。</summary>
    public List<string> Errors { get; set; } = new();

    /// <summary>Gets or sets 无数据时的说明，例如未配置 API Key。</summary>
    public string? Message { get; set; }
}

/// <summary>一次评分查询的入参。</summary>
public sealed class RatingQuery
{
    public Guid? ItemId { get; set; }

    /// <summary>Gets or sets movie 或 show。</summary>
    public string MediaType { get; set; } = "movie";

    public string? TmdbId { get; set; }

    public string? ImdbId { get; set; }

    public string? Title { get; set; }

    public int? Year { get; set; }

    public int? Season { get; set; }

    public int? Episode { get; set; }

    public bool IsEpisode => Episode.HasValue && Season.HasValue;

    public bool IsSeason => !IsEpisode && Season.HasValue;

    public bool HasAnyId => !string.IsNullOrWhiteSpace(TmdbId) || !string.IsNullOrWhiteSpace(ImdbId);

    public string CacheKey()
    {
        var sb = new StringBuilder();
        sb.Append(MediaType).Append('|');
        sb.Append(TmdbId ?? "-").Append('|');
        sb.Append(ImdbId ?? "-").Append('|');
        sb.Append(Season?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('|');
        sb.Append(Episode?.ToString(CultureInfo.InvariantCulture) ?? "-");
        return sb.ToString();
    }
}

/// <summary>前端展示配置（轻量，不含任何密钥）。</summary>
public sealed class ClientConfigDto
{
    public bool HideBuiltInRatings { get; set; } = true;

    public bool ShowSourceTooltip { get; set; } = true;

    public int MaxRatingsPerItem { get; set; } = 6;

    public string PluginVersion { get; set; } = "1.0.0";

    public bool WebInjectionActive { get; set; }

    public string[] Sources { get; set; } = Array.Empty<string>();
}
