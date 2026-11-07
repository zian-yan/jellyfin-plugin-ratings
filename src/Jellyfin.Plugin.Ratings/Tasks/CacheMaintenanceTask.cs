using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ratings.Core;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings.Tasks;

/// <summary>
/// 缓存维护任务：定期清理过期评分，避免长期运行时内存缓存无限膨胀。
/// Jellyfin 会自动发现插件程序集内的 IScheduledTask 实现。
/// </summary>
public sealed class CacheMaintenanceTask : IScheduledTask
{
    private readonly RatingCache _cache;
    private readonly ILogger<CacheMaintenanceTask> _logger;

    public CacheMaintenanceTask(RatingCache cache, ILogger<CacheMaintenanceTask> logger)
    {
        _cache = cache;
        _logger = logger;
    }

    public string Name => "评分缓存维护";

    public string Key => "JellyfinRatingsCacheMaintenance";

    public string Description => "清理 Jellyfin Ratings 插件中超过保留期（14 天未刷新）的评分缓存；刚过期但仍在保留期内的条目会保留，用于详情页秒开与后台刷新。";

    public string Category => "Ratings";

    public Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var removed = _cache.Prune();
        _logger.LogInformation("[Ratings] 缓存维护完成，清理 {Removed} 条，剩余 {Remaining} 条", removed, _cache.Count);
        progress?.Report(100);
        return Task.CompletedTask;
    }

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers()
    {
        yield return new TaskTriggerInfo
        {
            Type = TaskTriggerInfoType.IntervalTrigger,
            IntervalTicks = TimeSpan.FromHours(6).Ticks
        };
    }
}
