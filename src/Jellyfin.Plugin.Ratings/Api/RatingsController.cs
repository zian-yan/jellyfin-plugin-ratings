using System;
using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ratings.Core;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Entities.TV;
using MediaBrowser.Controller.Library;
using MediaBrowser.Model.Entities;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings.Api;

/// <summary>
/// 对外 REST 接口。全部使用 [ApiController] + ControllerBase（Microsoft.AspNetCore.Mvc），
/// 不继承 Jellyfin.Api 内的基类 —— 该基类在 10.11 与 12 之间发生过移动，
/// 这是本插件能做到「一次编译、双版本可装」的关键。
/// </summary>
[ApiController]
[Route("Ratings")]
public class RatingsController : ControllerBase
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private static readonly ConcurrentDictionary<string, byte[]> LogoCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly ILibraryManager _libraryManager;
    private readonly RatingAggregator _aggregator;
    private readonly ILogger<RatingsController> _logger;

    public RatingsController(ILibraryManager libraryManager, RatingAggregator aggregator, ILogger<RatingsController> logger)
    {
        _libraryManager = libraryManager;
        _aggregator = aggregator;
        _logger = logger;
    }

    /// <summary>按 Jellyfin 条目 ID 取评分（客户端脚本默认入口）。</summary>
    /// <param name="itemId">条目 ID。</param>
    /// <param name="season">可选，覆盖季号。</param>
    /// <param name="episode">可选，覆盖集号。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    [HttpGet("v1/item/{itemId:guid}")]
    [AllowAnonymous]
    public async Task<IActionResult> GetForItem(
        [FromRoute] Guid itemId,
        [FromQuery] int? season,
        [FromQuery] int? episode,
        CancellationToken cancellationToken)
    {
        var config = ConfigProvider.Current;
        if (!IsAllowed(config))
        {
            return Unauthorized(new { error = "authentication required" });
        }

        BaseItem? item;
        try
        {
            item = _libraryManager.GetItemById(itemId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Ratings] 读取条目失败 {ItemId}", itemId);
            return StatusCode(500, new { error = "item lookup failed" });
        }

        if (item is null)
        {
            return NotFound(new { error = "item not found", itemId });
        }

        var query = BuildQuery(item, season, episode);
        var response = await _aggregator.GetAsync(query, config, cancellationToken).ConfigureAwait(false);
        return Content(JsonSerializer.Serialize(response, JsonOptions), "application/json");
    }

    /// <summary>直接按 Provider ID 查询，适合无媒体库条目或第三方集成。</summary>
    [HttpGet("v1/lookup")]
    [AllowAnonymous]
    public async Task<IActionResult> Lookup(
        [FromQuery] string? type,
        [FromQuery] string? tmdbId,
        [FromQuery] string? imdbId,
        [FromQuery] int? season,
        [FromQuery] int? episode,
        [FromQuery] string? title,
        [FromQuery] int? year,
        CancellationToken cancellationToken)
    {
        var config = ConfigProvider.Current;
        if (!IsAllowed(config))
        {
            return Unauthorized(new { error = "authentication required" });
        }

        var query = new RatingQuery
        {
            MediaType = string.Equals(type, "show", StringComparison.OrdinalIgnoreCase) ? "show" : "movie",
            TmdbId = tmdbId,
            ImdbId = imdbId,
            Season = season,
            Episode = episode,
            Title = title,
            Year = year
        };

        var response = await _aggregator.GetAsync(query, config, cancellationToken).ConfigureAwait(false);
        return Content(JsonSerializer.Serialize(response, JsonOptions), "application/json");
    }

    /// <summary>前端脚本用的展示配置（不含任何密钥）。</summary>
    [HttpGet("v1/config")]
    [AllowAnonymous]
    public IActionResult GetClientConfig()
    {
        var config = ConfigProvider.Current;

        var dto = new ClientConfigDto
        {
            HideBuiltInRatings = config.HideBuiltInRatings,
            ShowSourceTooltip = config.ShowSourceTooltip,
            MaxRatingsPerItem = config.MaxRatingsPerItem,
            PluginVersion = typeof(Plugin).Assembly.GetName().Version?.ToString() ?? "1.0.0",
            WebInjectionActive = RatingsRuntimeState.InjectionActive,
            Sources = BuildEnabledSources(config)
        };

        return Content(JsonSerializer.Serialize(dto, JsonOptions), "application/json");
    }

    /// <summary>前端脚本本体（嵌入资源），供自动注入或手动 <c>&lt;script&gt;</c> 引用。</summary>
    [HttpGet("v1/client.js")]
    [AllowAnonymous]
    public IActionResult GetClientScript()
    {
        var assembly = typeof(Plugin).Assembly;
        const string ResourceName = "Jellyfin.Plugin.Ratings.Web.client.ratings-client.js";

        using var stream = assembly.GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            _logger.LogError("[Ratings] 客户端脚本资源缺失: {Resource}", ResourceName);
            return NotFound("client script not packaged");
        }

        using var reader = new StreamReader(stream);
        Response.Headers["Cache-Control"] = "public, max-age=300";
        return Content(reader.ReadToEnd(), "application/javascript; charset=utf-8");
    }

    /// <summary>来源 logo（嵌入资源，离线可用，不依赖 CDN）。</summary>
    [HttpGet("v1/logo/{name}")]
    [AllowAnonymous]
    public IActionResult GetLogo([FromRoute] string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 64
            || name.IndexOfAny(new[] { '/', '\\', '.', ':' }) >= 0)
        {
            return BadRequest("invalid logo name");
        }

        var bytes = LogoCache.GetOrAdd(name, key =>
        {
            var assembly = typeof(Plugin).Assembly;
            var resource = $"Jellyfin.Plugin.Ratings.Web.logos.{key}.png";
            using var stream = assembly.GetManifestResourceStream(resource);
            if (stream is null)
            {
                return Array.Empty<byte>();
            }

            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            return memory.ToArray();
        });

        if (bytes.Length == 0)
        {
            return NotFound("logo not found");
        }

        Response.Headers["Cache-Control"] = "public, max-age=604800, immutable";
        return File(bytes, "image/png");
    }

    /// <summary>诊断接口：版本、注入状态、缓存与数据源健康度。</summary>
    [HttpGet("v1/health")]
    [AllowAnonymous]
    public IActionResult Health()
    {
        var config = ConfigProvider.Current;
        if (!IsAllowed(config))
        {
            return Unauthorized(new { error = "authentication required" });
        }

        var payload = new
        {
            plugin = "Jellyfin.Plugin.Ratings",
            version = typeof(Plugin).Assembly.GetName().Version?.ToString(),
            serverVersion = JellyfinCompat.ServerVersion.ToString(),
            isV12 = JellyfinCompat.IsV12OrLater,
            targetFramework = JellyfinCompat.TargetFramework,
            injectionActive = RatingsRuntimeState.InjectionActive,
            webInjectionEnabled = config.EnableWebInjection,
            cacheEntries = _aggregator.Cache.Count,
            sources = new
            {
                mdblist = new { enabled = config.EnableMdbList, keyConfigured = !string.IsNullOrWhiteSpace(config.MdbListApiKey) },
                tmdb = new { enabled = config.EnableTmdb, keyConfigured = !string.IsNullOrWhiteSpace(config.TmdbApiKey) },
                kinopoisk = new { enabled = config.EnableKinopoisk, keyConfigured = !string.IsNullOrWhiteSpace(config.KinopoiskApiKey) },
                anilist = new { enabled = config.EnableAniList },
                douban = new { enabled = config.EnableDouban },
                allocine = new { enabled = config.EnableAllocine },
                certifications = new { enabled = config.EnableCertifications }
            },
            health = _aggregator.Health.Snapshot()
        };

        return Content(JsonSerializer.Serialize(payload, JsonOptions), "application/json");
    }

    private static string[] BuildEnabledSources(Configuration.PluginConfiguration config)
    {
        var sources = new System.Collections.Generic.List<string>(7);
        if (config.EnableTmdb) { sources.Add("tmdb"); }
        if (config.EnableMdbList) { sources.Add("mdblist"); }
        if (config.EnableAniList) { sources.Add("anilist"); }
        if (config.EnableKinopoisk) { sources.Add("kinopoisk"); }
        if (config.EnableDouban) { sources.Add("douban"); }
        if (config.EnableAllocine) { sources.Add("allocine"); }
        if (config.EnableCertifications) { sources.Add("certifications"); }
        return sources.ToArray();
    }

    private bool IsAllowed(Configuration.PluginConfiguration config)
    {
        if (!config.RequireAuthentication)
        {
            return true;
        }

        var authenticated = User?.Identity?.IsAuthenticated ?? false;
        if (!authenticated)
        {
            _logger.LogDebug("[Ratings] 未认证请求被拒绝: {Path}", Request.Path);
        }

        return authenticated;
    }

    /// <summary>
    /// 从媒体库条目推导查询参数。剧集/分集需要回溯到 Series 上取 TMDb ID，
    /// 再用 IndexNumber/ParentIndexNumber 拼出 TMDb 的分集地址。
    /// </summary>
    private RatingQuery BuildQuery(BaseItem item, int? seasonOverride, int? episodeOverride)
    {
        var mediaType = item is Series or Season or Episode ? "show" : "movie";
        int? season = seasonOverride;
        int? episode = episodeOverride;

        BaseItem providerSource = item;

        if (item is Episode episodeItem)
        {
            season ??= episodeItem.ParentIndexNumber;
            episode ??= episodeItem.IndexNumber;
            providerSource = ResolveSeries(episodeItem.SeriesId) ?? item;
        }
        else if (item is Season seasonItem)
        {
            season ??= seasonItem.IndexNumber;
            providerSource = ResolveSeries(seasonItem.SeriesId) ?? item;
        }

        var tmdbId = JellyfinCompat.GetProviderId(providerSource, MediaBrowser.Model.Entities.MetadataProvider.Tmdb)
                     ?? JellyfinCompat.GetProviderId(item, MediaBrowser.Model.Entities.MetadataProvider.Tmdb);
        var imdbId = JellyfinCompat.GetProviderId(providerSource, MediaBrowser.Model.Entities.MetadataProvider.Imdb)
                     ?? JellyfinCompat.GetProviderId(item, MediaBrowser.Model.Entities.MetadataProvider.Imdb);

        return new RatingQuery
        {
            ItemId = item.Id,
            MediaType = mediaType,
            TmdbId = tmdbId,
            ImdbId = imdbId,
            Title = item.Name,
            Year = item.ProductionYear,
            Season = mediaType == "show" ? season : null,
            Episode = episode
        };
    }

    private BaseItem? ResolveSeries(Guid seriesId)
    {
        if (seriesId == Guid.Empty)
        {
            return null;
        }

        try
        {
            return _libraryManager.GetItemById(seriesId);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "[Ratings] 回溯剧集失败 {SeriesId}", seriesId);
            return null;
        }
    }
}

/// <summary>运行期状态标记，供健康检查判断注入是否真的生效。</summary>
public static class RatingsRuntimeState
{
    private static int _injectionActive;

    public static bool InjectionActive => System.Threading.Volatile.Read(ref _injectionActive) == 1;

    internal static void MarkInjectionActive() => System.Threading.Interlocked.Exchange(ref _injectionActive, 1);
}
