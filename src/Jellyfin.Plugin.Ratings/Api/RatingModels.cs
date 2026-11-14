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

    /// <summary>Gets or sets 评分归属层级（show / season / episode），用于分集页剔除整剧级同源评分。</summary>
    public string? Scope { get; set; }

    public static RatingEntry Create(
        string source,
        string logo,
        double? value,
        int? scale,
        string? url = null,
        int order = 100,
        string? scope = null)
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
            Order = order,
            Scope = scope
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

    /// <summary>
    /// 外部 ID 白名单：只允许字母、数字、连字符，长度上限 32。
    ///
    /// Provider ID 会直接拼进外部接口的 URL / SPARQL 语句，而它可能被 NFO 文件或
    /// 第三方元数据写入任意内容（例如 <c>550/../../x</c>、<c>1?apikey=</c>）。
    /// 所有拼串前都应经过本方法，非法值一律丢弃而不是透传。
    /// </summary>
    public static string? SanitizeId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        var trimmed = id.Trim();
        if (trimmed.Length > 32)
        {
            return null;
        }

        foreach (var ch in trimmed)
        {
            if (!char.IsAsciiLetterOrDigit(ch) && ch != '-')
            {
                return null;
            }
        }

        return trimmed;
    }

    /// <summary>
    /// 缓存键前缀带插件版本：升级后旧缓存的语义（例如「没有 TMDb 分」）自动失效，
    /// 不必等 TTL 到期，也不会让用户误以为修复没生效。
    /// </summary>
    private static readonly string CacheSchema =
        "v" + (typeof(RatingQuery).Assembly.GetName().Version?.ToString(3) ?? "0");

    public string CacheKey()
    {
        var sb = new StringBuilder();
        sb.Append(CacheSchema).Append('|');
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
