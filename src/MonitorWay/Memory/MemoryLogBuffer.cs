using System.Threading.Channels;
using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Querying;
using Kododo.MonitorWay.Sources;
using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.Memory;

/// <summary>
/// Recent log entries of the application, kept in retention pools.
/// </summary>
/// <remarks>
/// Ids are assigned under the lock, so every pool stays sorted by id and a merged view of several
/// pools is a merge of sorted lists. Reads copy small batches under the lock and evaluate filters
/// outside of it, so a slow query does not block logging.
/// </remarks>
internal sealed class MemoryLogBuffer : IDisposable
{
    private const int ReadBatchSize = 1024;
    private static readonly TimeSpan MaxAgeCheckInterval = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private readonly object _subscribersLock = new();
    private readonly RetentionPool[] _pools;
    private readonly RetentionPool[] _poolByLevel;
    private readonly TimeProvider _time;
    private readonly ITimer? _expiryTimer;
    private long _lastId;
    private long _totalBytes;
    private Subscription[] _subscribers = [];

    public MemoryLogBuffer(MemoryBufferOptions options, TimeProvider time, IgnoredRequests? ignoredRequests = null)
    {
        _time = time;
        IgnoredRequests = ignoredRequests ?? new IgnoredRequests();
        MaxTotalBytes = options.MaxTotalBytes;
        _pools = BuildPools(options);
        _poolByLevel = new RetentionPool[(int)LogLevel.None];
        foreach (var pool in _pools)
        {
            foreach (var level in pool.Levels)
                _poolByLevel[(int)level] = pool;
        }

        var minAge = _pools.Min(p => p.MaxAge ?? TimeSpan.MaxValue);
        if (minAge != TimeSpan.MaxValue)
        {
            var interval = minAge < MaxAgeCheckInterval ? minAge : MaxAgeCheckInterval;
            _expiryTimer = time.CreateTimer(_ => RemoveExpired(), null, interval, interval);
        }
    }

    public long MaxTotalBytes { get; }

    /// <summary>Pools ordered by their lowest level, which is also the order of eviction under the global limit.</summary>
    public IReadOnlyList<RetentionPool> Pools => _pools;

    /// <summary>Requests whose entries are dropped; see <see cref="Memory.IgnoredRequests"/>.</summary>
    public IgnoredRequests IgnoredRequests { get; }

    /// <summary>
    /// Whether the OpenTelemetry SDK exports to the buffer. A host that only reads log files does not list the buffer,
    /// which would stay empty.
    /// </summary>
    public bool IsExportedTo { get; set; }

    /// <returns>The stored entry, or <c>null</c> when it belongs to an ignored request.</returns>
    public LogEntry? Append(in PendingEntry pending)
    {
        if (IgnoredRequests.Matches(pending.Properties))
            return null;

        var pool = _poolByLevel[(int)pending.Level];
        var now = _time.GetUtcNow();
        LogEntry entry;

        lock (_lock)
        {
            entry = new LogEntry(
                ++_lastId, pending.Timestamp, pending.Level, pending.Category, pending.Message,
                pending.MessageTemplate, pending.Exception, pending.Properties, pending.TraceId, pending.SpanId,
                pending.ExceptionType, pending.ServiceName);

            pool.Append(entry, pending.Size);
            _totalBytes += pending.Size;

            while (pool.IsOverLimit || pool.Count > 0 && pool.IsExpired(pool.Oldest, now))
                _totalBytes -= pool.RemoveOldest();

            while (_totalBytes > MaxTotalBytes && Array.Find(_pools, p => p.Count > 0) is { } victim)
                _totalBytes -= victim.RemoveOldest();
        }

        foreach (var subscription in Volatile.Read(ref _subscribers))
            subscription.Publish(entry);

        return entry;
    }

    public void RemoveExpired()
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            foreach (var pool in _pools)
            {
                while (pool.Count > 0 && pool.IsExpired(pool.Oldest, now))
                    _totalBytes -= pool.RemoveOldest();
            }
        }
    }

    /// <summary>
    /// Returns up to <paramref name="count"/> matching entries older than <paramref name="beforeId"/>
    /// (the newest ones when it is <c>null</c>), in ascending order.
    /// </summary>
    public IReadOnlyList<LogEntry> ReadBefore(long? beforeId, int count, LogQueryMatcher matcher, out bool hasMore)
    {
        var cursors = _pools
            .Where(p => p.Levels.Any(l => !matcher.ExcludesLevel(l)))
            .Select(p => new Cursor(this, p, beforeId ?? long.MaxValue, backward: true))
            .ToArray();

        var result = Merge(cursors, count, matcher, pickNewest: true, out hasMore);
        result.Reverse();
        return result;
    }

    /// <summary>
    /// Returns up to <paramref name="count"/> matching entries newer than <paramref name="afterId"/>, in ascending order.
    /// </summary>
    public IReadOnlyList<LogEntry> ReadAfter(long afterId, int count, LogQueryMatcher matcher, out bool hasMore)
    {
        var cursors = _pools
            .Where(p => p.Levels.Any(l => !matcher.ExcludesLevel(l)))
            .Select(p => new Cursor(this, p, afterId, backward: false))
            .ToArray();

        return Merge(cursors, count, matcher, pickNewest: false, out hasMore);
    }

    public LogEntry? Find(long id)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            foreach (var pool in _pools)
            {
                if (pool.Find(id) is { } entry && !pool.IsExpired(entry, now))
                    return entry;
            }
        }
        return null;
    }

    public BufferStats GetStats()
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            var pools = _pools
                .Select(p => new PoolStats(
                    p.Levels, p.Count, p.Bytes, p.Count > 0 ? p.Oldest.Timestamp : null,
                    p.Evicted, p.MaxEntries, p.MaxBytes, p.MaxAge))
                .ToArray();
            var levelCounts = new int[(int)LogLevel.None];
            foreach (var pool in _pools)
            {
                for (var level = 0; level < levelCounts.Length; level++)
                    levelCounts[level] += pool.LevelCounts[level];
            }
            return new BufferStats(pools, levelCounts, _totalBytes, MaxTotalBytes, _lastId, now);
        }
    }

    /// <summary>Entries per category among the kept entries.</summary>
    public Dictionary<string, long> GetCategoryCounts()
    {
        var now = _time.GetUtcNow();
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        lock (_lock)
        {
            foreach (var pool in _pools)
            {
                pool.ForEach(entry =>
                {
                    if (entry.Category is { } category && !pool.IsExpired(entry, now))
                        counts[category] = counts.GetValueOrDefault(category) + 1;
                });
            }
        }
        return counts;
    }

    /// <summary>
    /// Entry counts in <paramref name="buckets"/> equal ranges of ids, from the oldest kept entry to the newest.
    /// </summary>
    public Overview GetOverview(int buckets)
    {
        var now = _time.GetUtcNow();
        lock (_lock)
        {
            var start = _pools.Where(p => p.Count > 0).Select(p => p.Oldest.Id).DefaultIfEmpty(_lastId + 1).Min();
            var end = _lastId + 1;
            var result = new OverviewBucket[buckets];
            var width = Math.Max(1.0, (double)(end - start) / buckets);
            foreach (var pool in _pools)
            {
                pool.ForEach(entry =>
                {
                    if (pool.IsExpired(entry, now))
                        return;
                    var bucket = Math.Min(buckets - 1, (int)((entry.Id - start) / width));
                    result[bucket] = result[bucket].Add(entry.Level, 1);
                });
            }
            return new Overview(start, end, result);
        }
    }

    /// <summary>
    /// Subscribes to entries appended from now on. A subscriber that does not keep up loses entries
    /// instead of slowing down logging; <see cref="Subscription.TakeDropped"/> reports how many.
    /// </summary>
    public Subscription Subscribe(int capacity = 1000)
    {
        var subscription = new Subscription(this, capacity);
        lock (_subscribersLock)
            _subscribers = [.. _subscribers, subscription];
        return subscription;
    }

    public void Dispose()
    {
        _expiryTimer?.Dispose();
        foreach (var subscription in Volatile.Read(ref _subscribers))
            subscription.Complete();
    }

    private void Unsubscribe(Subscription subscription)
    {
        lock (_subscribersLock)
            _subscribers = Array.FindAll(_subscribers, s => s != subscription);
    }

    private static List<LogEntry> Merge(Cursor[] cursors, int count, LogQueryMatcher matcher, bool pickNewest, out bool hasMore)
    {
        var result = new List<LogEntry>(Math.Min(count, ReadBatchSize));
        while (true)
        {
            Cursor? best = null;
            LogEntry? bestEntry = null;
            foreach (var cursor in cursors)
            {
                if (cursor.Peek() is { } entry &&
                    (bestEntry is null || (pickNewest ? entry.Id > bestEntry.Id : entry.Id < bestEntry.Id)))
                {
                    best = cursor;
                    bestEntry = entry;
                }
            }

            if (best is null)
            {
                hasMore = false;
                return result;
            }

            best.Advance();
            if (!matcher.Matches(bestEntry!))
                continue;
            if (result.Count == count)
            {
                hasMore = true;
                return result;
            }
            result.Add(bestEntry!);
        }
    }

    /// <summary>Throws <see cref="InvalidOperationException"/> when the options are not valid.</summary>
    public static void Validate(MemoryBufferOptions options) => BuildPools(options);

    private static RetentionPool[] BuildPools(MemoryBufferOptions options)
    {
        if (options.MaxTotalBytes <= 0)
            throw new InvalidOperationException($"MonitorWay: {nameof(MemoryBufferOptions.MaxTotalBytes)} must be greater than zero.");

        var configured = options.Pools.Count > 0
            ? options.Pools
            : [new RetentionPoolOptions { Levels = { LogLevel.Error, LogLevel.Critical }, MaxEntries = 1_000 }];

        var assigned = new HashSet<LogLevel>();
        var pools = new List<RetentionPool>();
        foreach (var pool in configured)
        {
            if (pool.Levels.Count == 0)
                throw new InvalidOperationException("MonitorWay: a retention pool must have at least one level.");
            foreach (var level in pool.Levels)
            {
                if (level is < LogLevel.Trace or > LogLevel.Critical)
                    throw new InvalidOperationException($"MonitorWay: {level} cannot be stored in a retention pool.");
                if (!assigned.Add(level))
                    throw new InvalidOperationException($"MonitorWay: level {level} is assigned to more than one retention pool.");
            }
            pools.Add(CreatePool(pool.Levels.Order().ToArray(), pool));
        }

        var unassigned = Enum.GetValues<LogLevel>()
            .Where(l => l is >= LogLevel.Trace and <= LogLevel.Critical && !assigned.Contains(l))
            .ToArray();
        if (unassigned.Length > 0)
            pools.Add(CreatePool(unassigned, options.DefaultPool));

        return pools.OrderBy(p => p.LowestLevel).ToArray();
    }

    private static RetentionPool CreatePool(LogLevel[] levels, RetentionPoolOptions options)
    {
        if (options.MaxEntries <= 0 || options.MaxBytes <= 0 || options.MaxAge <= TimeSpan.Zero)
            throw new InvalidOperationException($"MonitorWay: limits of the retention pool for {string.Join(", ", levels)} must be greater than zero.");
        return new RetentionPool(levels, options.MaxEntries, options.MaxBytes, options.MaxAge);
    }

    /// <summary>Walks one pool in batches, re-locating its position by id so concurrent evictions are harmless.</summary>
    private sealed class Cursor(MemoryLogBuffer buffer, RetentionPool pool, long startId, bool backward)
    {
        private readonly List<LogEntry> _batch = [];
        private int _index;
        private long _position = startId;
        private bool _exhausted;

        public LogEntry? Peek()
        {
            if (_index == _batch.Count)
            {
                if (_exhausted)
                    return null;
                Fill();
                if (_batch.Count == 0)
                    return null;
            }
            return _batch[_index];
        }

        public void Advance() => _index++;

        private void Fill()
        {
            _batch.Clear();
            _index = 0;
            var now = buffer._time.GetUtcNow();
            lock (buffer._lock)
            {
                if (backward)
                    pool.CopyBefore(_position, ReadBatchSize, now, _batch);
                else
                    pool.CopyAfter(_position, ReadBatchSize, now, _batch);
            }
            if (_batch.Count < ReadBatchSize)
                _exhausted = true;
            if (_batch.Count > 0)
                _position = _batch[^1].Id;
        }
    }

    public sealed class Subscription : IDisposable
    {
        private readonly MemoryLogBuffer _buffer;
        private readonly Channel<LogEntry> _channel;
        private long _dropped;

        internal Subscription(MemoryLogBuffer buffer, int capacity)
        {
            _buffer = buffer;
            _channel = Channel.CreateBounded<LogEntry>(new BoundedChannelOptions(capacity)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
            });
        }

        public ChannelReader<LogEntry> Reader => _channel.Reader;

        /// <summary>Returns the number of entries lost since the previous call and resets it.</summary>
        public long TakeDropped() => Interlocked.Exchange(ref _dropped, 0);

        internal void Publish(LogEntry entry)
        {
            if (!_channel.Writer.TryWrite(entry))
                Interlocked.Increment(ref _dropped);
        }

        internal void Complete() => _channel.Writer.TryComplete();

        public void Dispose()
        {
            _buffer.Unsubscribe(this);
            Complete();
        }
    }
}

/// <summary>An entry captured by the logger, waiting for its id.</summary>
internal readonly record struct PendingEntry(
    DateTimeOffset Timestamp,
    LogLevel Level,
    string Category,
    string Message,
    string? MessageTemplate,
    string? Exception,
    IReadOnlyList<KeyValuePair<string, string?>> Properties,
    string? TraceId,
    string? SpanId,
    int Size,
    string? ExceptionType = null,
    string? ServiceName = null);

internal sealed record PoolStats(
    IReadOnlyList<LogLevel> Levels,
    int Count,
    long Bytes,
    DateTimeOffset? Oldest,
    long Evicted,
    int? MaxEntries,
    long? MaxBytes,
    TimeSpan? MaxAge);

internal sealed record BufferStats(
    IReadOnlyList<PoolStats> Pools,
    IReadOnlyList<int> LevelCounts,
    long TotalBytes,
    long MaxTotalBytes,
    long LastId,
    DateTimeOffset Now);
