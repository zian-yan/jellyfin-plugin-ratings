using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ratings.Api;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings.Core;

/// <summary>
/// 进程内评分缓存。原脚本每次进详情页都要打 5~7 个外部接口，
/// 服务端集中缓存后，同一部片子的后续浏览全部命中本地。
/// </summary>
public sealed class RatingCache
{
    private readonly ConcurrentDictionary<string, Entry> _items = new(StringComparer.Ordinal);
    private readonly ILogger<RatingCache> _logger;

    public RatingCache(ILogger<RatingCache> logger)
    {
        _logger = logger;
    }

    private sealed record Entry(RatingsResponse Value, DateTimeOffset ExpiresAt);

    public int Count => _items.Count;

    public bool TryGet(string key, out RatingsResponse? value)
    {
        if (_items.TryGetValue(key, out var entry))
        {
            if (entry.ExpiresAt > DateTimeOffset.UtcNow)
            {
                value = entry.Value;
                return true;
            }

            _items.TryRemove(key, out _);
        }

        value = null;
        return false;
    }

    public void Set(string key, RatingsResponse response, int minutes)
    {
        if (minutes <= 0)
        {
            return;
        }

        _items[key] = new Entry(response, DateTimeOffset.UtcNow.AddMinutes(minutes));
    }

    public void Clear()
    {
        var count = _items.Count;
        _items.Clear();
        _logger.LogInformation("[Ratings] 缓存已清空，共 {Count} 条", count);
    }

    /// <summary>清理过期项，由定时任务周期调用。</summary>
    public int Prune()
    {
        var now = DateTimeOffset.UtcNow;
        var removed = 0;
        foreach (var kv in _items.ToArray())
        {
            if (kv.Value.ExpiresAt <= now && _items.TryRemove(kv.Key, out _))
            {
                removed++;
            }
        }

        return removed;
    }
}

/// <summary>
/// 数据源健康度：连续失败超过阈值后进入冷却期，避免拖慢每个详情页的响应。
/// </summary>
public sealed class SourceHealth
{
    private readonly ConcurrentDictionary<string, State> _states = new(StringComparer.OrdinalIgnoreCase);

    private sealed class State
    {
        public int Failures;
        public DateTimeOffset CooldownUntil = DateTimeOffset.MinValue;
    }

    public bool IsAvailable(string source)
    {
        return !_states.TryGetValue(source, out var state) || state.CooldownUntil <= DateTimeOffset.UtcNow;
    }

    public void Report(string source, bool success, int threshold, int cooldownMinutes, ILogger logger)
    {
        var state = _states.GetOrAdd(source, _ => new State());
        lock (state)
        {
            if (success)
            {
                if (state.Failures > 0)
                {
                    logger.LogInformation("[Ratings] {Source} 恢复正常", source);
                }

                state.Failures = 0;
                state.CooldownUntil = DateTimeOffset.MinValue;
                return;
            }

            state.Failures++;
            if (state.Failures >= Math.Max(1, threshold))
            {
                state.CooldownUntil = DateTimeOffset.UtcNow.AddMinutes(Math.Max(1, cooldownMinutes));
                logger.LogWarning(
                    "[Ratings] {Source} 连续失败 {Failures} 次，冷却 {Minutes} 分钟",
                    source, state.Failures, cooldownMinutes);
            }
        }
    }

    public IReadOnlyDictionary<string, string> Snapshot()
    {
        var now = DateTimeOffset.UtcNow;
        return _states.ToDictionary(
            kv => kv.Key,
            kv => kv.Value.CooldownUntil > now
                ? $"cooldown until {kv.Value.CooldownUntil:HH:mm:ss}"
                : $"{kv.Value.Failures} consecutive failures");
    }
}
