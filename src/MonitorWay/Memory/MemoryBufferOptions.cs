using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.Memory;

/// <summary>
/// Retention settings of the in-memory log buffer.
/// </summary>
/// <remarks>
/// Entries are kept in retention pools. Each pool covers one or more levels and has its own limits,
/// so a flood of debug entries cannot push errors out of memory. Levels that are not assigned to any
/// pool go to <see cref="DefaultPool"/>. When no pools are configured, errors and critical entries
/// get a pool of their own (1 000 entries) and the remaining levels share the default pool.
/// <see cref="MaxTotalBytes"/> always applies on top of the pool limits.
/// </remarks>
public sealed class MemoryBufferOptions
{
    /// <summary>Default value of <see cref="MaxTotalBytes"/>: 64 MB.</summary>
    public const long DefaultMaxTotalBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Hard limit of the approximate memory used by all pools together. When it is exceeded, the oldest
    /// entries of the pool with the lowest level are removed first, so errors are removed last.
    /// </summary>
    public long MaxTotalBytes { get; set; } = DefaultMaxTotalBytes;

    /// <summary>Pools with their own limits. A level may belong to at most one pool.</summary>
    public IList<RetentionPoolOptions> Pools { get; } = new List<RetentionPoolOptions>();

    /// <summary>Limits of the pool that holds the levels not assigned to any of <see cref="Pools"/>.</summary>
    public RetentionPoolOptions DefaultPool { get; } = new RetentionPoolOptions().KeepEntries(50_000);

    /// <summary>
    /// Adds a pool for the given levels and returns it for configuring its limits.
    /// </summary>
    /// <example>
    /// <code>
    /// m.Pool(LogLevel.Error, LogLevel.Critical).KeepEntries(5_000).KeepFor(TimeSpan.FromDays(7));
    /// </code>
    /// </example>
    public RetentionPoolOptions Pool(params LogLevel[] levels)
    {
        var pool = new RetentionPoolOptions();
        foreach (var level in levels)
            pool.Levels.Add(level);
        Pools.Add(pool);
        return pool;
    }
}

/// <summary>
/// Limits of a single retention pool. An entry is removed as soon as any of the limits is exceeded;
/// a pool without limits is bounded only by <see cref="MemoryBufferOptions.MaxTotalBytes"/>.
/// </summary>
public sealed class RetentionPoolOptions
{
    /// <summary>Levels stored in this pool.</summary>
    public IList<LogLevel> Levels { get; } = new List<LogLevel>();

    /// <summary>Maximum number of entries kept in the pool.</summary>
    public int? MaxEntries { get; set; }

    /// <summary>Maximum approximate memory used by the pool, in bytes.</summary>
    public long? MaxBytes { get; set; }

    /// <summary>Maximum age of entries kept in the pool.</summary>
    public TimeSpan? MaxAge { get; set; }

    /// <summary>Sets <see cref="MaxEntries"/>.</summary>
    public RetentionPoolOptions KeepEntries(int maxEntries)
    {
        MaxEntries = maxEntries;
        return this;
    }

    /// <summary>Sets <see cref="MaxBytes"/>.</summary>
    public RetentionPoolOptions KeepBytes(long maxBytes)
    {
        MaxBytes = maxBytes;
        return this;
    }

    /// <summary>Sets <see cref="MaxAge"/>.</summary>
    public RetentionPoolOptions KeepFor(TimeSpan maxAge)
    {
        MaxAge = maxAge;
        return this;
    }
}
