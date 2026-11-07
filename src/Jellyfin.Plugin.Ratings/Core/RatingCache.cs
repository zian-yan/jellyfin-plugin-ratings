using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using Jellyfin.Plugin.Ratings.Api;
using MediaBrowser.Common.Configuration;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.Ratings.Core;

/// <summary>
/// 一条缓存的落盘形态。单独定义（而非 RatingCache 的私有嵌套类型）便于 JSON 反序列化。
/// </summary>
public sealed class RatingCacheEntry
{
    public RatingsResponse Value { get; set; } = new();

    /// <summary>Gets or sets 数据实际抓取时间，用于淘汰与 stale 判定。</summary>
    public DateTimeOffset FetchedAt { get; set; }

    /// <summary>Gets or sets 新鲜期截止时间，超过即为 stale（仍可先渲染，等后台刷新）。</summary>
    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>
/// 双层评分缓存：内存热数据 + 磁盘持久化。
///
/// 设计要点（针对「首次打开详情页评分出现慢」）：
///   1. 落盘 → Jellyfin 重启后不必重新打外部接口，第一眼就有数据；
///   2. 过期不删 → 过期项仍可先返回（stale），同时后台刷新，详情页永远秒开；
///   3. 写盘异步 + 防抖 + 原子替换 → 不阻塞请求线程，也不会写坏文件。
///
/// 加载策略：构造时立即在后台读盘（不阻塞 DI 解析与首个请求），
/// 数据接口用 <see cref="EnsureLoadedAsync"/> 等一小会儿；等不到就按空缓存继续，
/// 而不是把「等磁盘」的代价硬塞给第一个用户。
/// </summary>
public sealed class RatingCache : IDisposable
{
    private const string CacheFileName = "ratings-cache.json";

    /// <summary>内存条目上限，超出后按抓取时间淘汰最旧的一批，避免长期运行无界增长。</summary>
    private const int MaxEntries = 5000;

    /// <summary>stale 数据的保留上限：过期后再留这么久，供「先渲染旧值」使用。</summary>
    private static readonly TimeSpan StaleRetention = TimeSpan.FromDays(14);

    /// <summary>写盘间隔。全量序列化有成本，60 秒一次足以把「意外掉电」的损失控制在 1 分钟内。</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(60);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false
    };

    private readonly ConcurrentDictionary<string, RatingCacheEntry> _items = new(StringComparer.Ordinal);
    private readonly ILogger<RatingCache> _logger;
    private readonly string? _filePath;
    private readonly Timer? _flushTimer;
    private readonly SemaphoreSlim _flushGate = new(1, 1);
    private readonly TaskCompletionSource<bool> _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _dirty;
    private int _disposed;
    private int _loaded;

    public RatingCache(IApplicationPaths? applicationPaths, ILogger<RatingCache> logger)
    {
        _logger = logger;

        // 落盘位置：{DataPath}/ratings/ratings-cache.json 。DataPath 在容器里通常是 /config/data。
        // applicationPaths 为 null（单元测试）时退化为纯内存缓存。
        try
        {
            if (applicationPaths is null)
            {
                _filePath = null;
            }
            else
            {
                var directory = Path.Combine(applicationPaths.DataPath, "ratings");
                Directory.CreateDirectory(directory);
                _filePath = Path.Combine(directory, CacheFileName);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Ratings] 无法创建缓存目录，将只使用内存缓存");
            _filePath = null;
        }

        if (_filePath is null)
        {
            Interlocked.Exchange(ref _loaded, 1);
            _ready.TrySetResult(true);
            return;
        }

        // 后台加载：构造发生在 DI 解析期，不能在这里同步读一个可能几 MB 的文件
        _ = Task.Run(LoadAsync);

        _flushTimer = new Timer(
            _ =>
            {
                if (Volatile.Read(ref _disposed) == 0)
                {
                    // FlushAsync 内部已 try/catch，不会产生未观察异常
                    _ = FlushAsync(force: false);
                }
            },
            null,
            FlushInterval,
            FlushInterval);
    }

    public int Count => _items.Count;

    /// <summary>Gets 磁盘缓存是否启用（目录不可写时为 false）。</summary>
    public bool Persistent => _filePath is not null;

    /// <summary>Gets 磁盘缓存文件完整路径，供健康检查展示。</summary>
    public string? FilePath => _filePath;

    /// <summary>Gets 磁盘缓存加载完成信号。</summary>
    public Task Ready => _ready.Task;

    /// <summary>
    /// 等待磁盘缓存加载完成，最多等 <paramref name="timeout"/>。
    /// 超时不抛异常：调用方直接用当前内存里的数据继续即可（最坏就是走一次外部请求）。
    /// </summary>
    public async Task EnsureLoadedAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _loaded) != 0)
        {
            return;
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(timeout);

        try
        {
            await _ready.Task.WaitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // 只是等超时了，不是请求被取消
            _logger.LogDebug("[Ratings] 磁盘缓存加载超过 {Timeout}ms，先按当前数据继续", timeout.TotalMilliseconds);
        }
    }

    /// <summary>取新鲜（未过期）缓存。</summary>
    public bool TryGet(string key, out RatingsResponse? value)
    {
        if (_items.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTimeOffset.UtcNow)
        {
            value = entry.Value;
            return true;
        }

        value = null;
        return false;
    }

    /// <summary>
    /// 取过期但仍在保留期内的缓存。命中说明「有旧值可立刻渲染」，
    /// 调用方应同时触发一次后台刷新，不要阻塞当前请求。
    /// </summary>
    public bool TryGetStale(string key, out RatingsResponse? value, out DateTimeOffset fetchedAt)
    {
        if (_items.TryGetValue(key, out var entry)
            && entry.ExpiresAt <= DateTimeOffset.UtcNow
            && entry.FetchedAt > DateTimeOffset.UtcNow - StaleRetention)
        {
            value = entry.Value;
            fetchedAt = entry.FetchedAt;
            return true;
        }

        value = null;
        fetchedAt = default;
        return false;
    }

    public void Set(string key, RatingsResponse response, int minutes)
    {
        if (minutes <= 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        _items[key] = new RatingCacheEntry
        {
            Value = response,
            FetchedAt = now,
            ExpiresAt = now.AddMinutes(minutes)
        };

        TrimIfNeeded();
        Interlocked.Exchange(ref _dirty, 1);
    }

    public void Clear()
    {
        var count = _items.Count;
        _items.Clear();
        Interlocked.Exchange(ref _dirty, 1);
        _logger.LogInformation("[Ratings] 缓存已清空，共 {Count} 条", count);
    }

    /// <summary>
    /// 清理「过期且超过 stale 保留期」的条目，由定时任务周期调用。
    /// 注意：仅过期未超保留期的条目会被留下，用于详情页秒开。
    /// </summary>
    public int Prune()
    {
        var threshold = DateTimeOffset.UtcNow - StaleRetention;
        var removed = 0;
        foreach (var kv in _items.ToArray())
        {
            if (kv.Value.FetchedAt <= threshold && _items.TryRemove(kv.Key, out _))
            {
                removed++;
            }
        }

        if (removed > 0)
        {
            Interlocked.Exchange(ref _dirty, 1);
        }

        return removed;
    }

    /// <summary>同步刷盘。仅用于 Dispose 与需要「落盘后再继续」的场景。</summary>
    public void Flush(bool force = true) => FlushAsync(force).GetAwaiter().GetResult();

    /// <summary>
    /// 异步刷盘：序列化与写文件都不占用请求线程。
    /// </summary>
    /// <param name="force">true 表示即使没有脏标记也写一次。</param>
    public async Task FlushAsync(bool force = true)
    {
        // 注意：这里不能判断 _disposed —— Dispose 路径本身就要靠这个调用落盘。
        // 但必须等磁盘数据加载完，否则会用「空的内存」覆盖掉磁盘上的缓存。
        if (_filePath is null || Volatile.Read(ref _loaded) == 0)
        {
            return;
        }

        // 先清脏标记再写盘：写盘期间若有新的 Set，会重新置脏，下一轮再写
        var wasDirty = Interlocked.Exchange(ref _dirty, 0) == 1;
        if (!wasDirty && !force)
        {
            return;
        }

        await _flushGate.WaitAsync().ConfigureAwait(false);
        try
        {
            var snapshot = _items.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
            var temp = _filePath + ".tmp";

            await using (var stream = new FileStream(
                temp,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.Asynchronous))
            {
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions).ConfigureAwait(false);
            }

            File.Move(temp, _filePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Interlocked.Exchange(ref _dirty, 1);
            _logger.LogWarning(ex, "[Ratings] 缓存写盘失败");
        }
        finally
        {
            _flushGate.Release();
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        _flushTimer?.Dispose();

        // 加载还没结束就要退出：先等它把磁盘数据读进来。
        // 否则 Flush 会因为「未加载」直接跳过（丢新数据），或者用空内存覆盖磁盘。
        if (_filePath is not null && Volatile.Read(ref _loaded) == 0)
        {
            try
            {
                _ready.Task.Wait(TimeSpan.FromSeconds(2));
            }
            catch (AggregateException)
            {
                // LoadAsync 内部已兜底，这里只是别让关闭流程抛异常
            }
        }

        Flush(force: true);

        // 不 Dispose _flushGate：可能仍有挂起的 FlushAsync，SemaphoreSlim 无原生句柄，交给 GC 即可
    }

    private async Task LoadAsync()
    {
        try
        {
            if (!File.Exists(_filePath!))
            {
                return;
            }

            var json = await File.ReadAllTextAsync(_filePath!).ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(json))
            {
                return;
            }

            var loaded = JsonSerializer.Deserialize<Dictionary<string, RatingCacheEntry>>(json, JsonOptions);
            if (loaded is null)
            {
                return;
            }

            var threshold = DateTimeOffset.UtcNow - StaleRetention;
            var restored = 0;
            foreach (var kv in loaded)
            {
                // 缓存文件可能被改坏/被旧版本写过：结构不全的条目直接丢弃，不要让 null 流进业务层
                if (kv.Value?.Value?.Ratings is null || kv.Value.FetchedAt <= threshold)
                {
                    continue;
                }

                // 用 TryAdd 而不是索引赋值：加载期间如果已经有新数据进来，不能被磁盘旧值覆盖
                if (_items.TryAdd(kv.Key, kv.Value))
                {
                    restored++;
                }
            }

            _logger.LogInformation("[Ratings] 已从磁盘恢复 {Restored} 条评分缓存", restored);
        }
        catch (Exception ex)
        {
            // 文件损坏/格式变更不应影响插件启动，直接忽略并按空缓存运行
            _logger.LogWarning(ex, "[Ratings] 读取磁盘缓存失败，将以空缓存启动");
        }
        finally
        {
            Interlocked.Exchange(ref _loaded, 1);
            _ready.TrySetResult(true);
        }
    }

    private void TrimIfNeeded()
    {
        var overflow = _items.Count - MaxEntries;
        if (overflow <= 0)
        {
            return;
        }

        foreach (var kv in _items.OrderBy(pair => pair.Value.FetchedAt).Take(overflow).ToArray())
        {
            _items.TryRemove(kv.Key, out _);
        }
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
