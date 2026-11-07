using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Jellyfin.Plugin.Ratings.Core;

/// <summary>
/// 极轻量的固定窗口限流器（按客户端 key 计数，60 秒一个窗口）。
///
/// 存在的理由：<c>/Ratings/v1/lookup</c> 接受任意 Provider ID 与标题，等价于
/// 一个「帮我打 MDBList / TMDb / 豆瓣」的代理入口。即使要求登录，单个账号被脚本刷
/// 也能把 API Key 配额耗尽，所以这一层必须有。
///
/// 设计约束：
///   - key 只能用不可伪造的来源（已认证用户 ID / TCP 对端地址），不能用 X-Forwarded-For；
///   - 字典必须能自清理，否则按 IP 计数本身就是一个新的内存泄漏点。
/// </summary>
public sealed class RequestThrottle
{
    private const int WindowMs = 60_000;

    /// <summary>每 N 次调用做一次过期扫描，避免每次请求都遍历字典。</summary>
    private const int SweepIntervalCalls = 512;

    /// <summary>窗口结束后再保留这么久，覆盖「刚好在窗口边界」的并发请求。</summary>
    private const int BucketTtlMs = 180_000;

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private int _callsSinceSweep;

    private sealed class Bucket
    {
        public long WindowStart;
        public int Count;
        public long LastSeen;
    }

    /// <summary>Gets 当前跟踪的客户端数，供健康检查观察。</summary>
    public int TrackedClients => _buckets.Count;

    /// <summary>
    /// 尝试获取一次配额。<paramref name="limitPerMinute"/> ≤ 0 表示不限流。
    /// </summary>
    /// <param name="key">客户端标识（用户 ID 或对端地址）。</param>
    /// <param name="limitPerMinute">每个窗口允许的请求数。</param>
    /// <param name="retryAfterSeconds">被拒绝时建议的重试间隔。</param>
    /// <returns>true 表示放行。</returns>
    public bool TryAcquire(string key, int limitPerMinute, out int retryAfterSeconds)
    {
        retryAfterSeconds = 0;
        if (limitPerMinute <= 0)
        {
            return true;
        }

        var now = Environment.TickCount64;
        var bucket = _buckets.GetOrAdd(key, _ => new Bucket { WindowStart = now, LastSeen = now });

        lock (bucket)
        {
            if (now - bucket.WindowStart >= WindowMs)
            {
                bucket.WindowStart = now;
                bucket.Count = 0;
            }

            bucket.LastSeen = now;
            bucket.Count++;

            if (bucket.Count > limitPerMinute)
            {
                var remainingMs = WindowMs - (now - bucket.WindowStart);
                retryAfterSeconds = (int)Math.Max(1, remainingMs / 1000 + 1);
                return false;
            }
        }

        SweepIfNeeded(now);
        return true;
    }

    private void SweepIfNeeded(long now)
    {
        if (Interlocked.Increment(ref _callsSinceSweep) < SweepIntervalCalls)
        {
            return;
        }

        Interlocked.Exchange(ref _callsSinceSweep, 0);

        foreach (var kv in _buckets)
        {
            if (now - Volatile.Read(ref kv.Value.LastSeen) > BucketTtlMs)
            {
                _buckets.TryRemove(kv.Key, out _);
            }
        }
    }
}
