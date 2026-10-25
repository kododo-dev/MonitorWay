using Kododo.MonitorWay.Core.Model;
using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.Memory;

/// <summary>
/// Entries of one retention pool, ordered by id. Appends go to the tail and evictions remove from the
/// head, in segments of <see cref="SegmentSize"/> entries so the storage shrinks after a burst.
/// Not thread-safe: <see cref="MemoryLogBuffer"/> serializes all access under its lock.
/// </summary>
internal sealed class RetentionPool
{
    private const int SegmentShift = 10;
    private const int SegmentSize = 1 << SegmentShift;
    private const int SegmentMask = SegmentSize - 1;

    private readonly List<Slot[]> _segments = [];
    private int _head;

    public RetentionPool(IReadOnlyList<LogLevel> levels, int? maxEntries, long? maxBytes, TimeSpan? maxAge)
    {
        Levels = levels;
        LowestLevel = levels.Min();
        MaxEntries = maxEntries;
        MaxBytes = maxBytes;
        MaxAge = maxAge;
    }

    public IReadOnlyList<LogLevel> Levels { get; }
    public LogLevel LowestLevel { get; }
    public int? MaxEntries { get; }
    public long? MaxBytes { get; }
    public TimeSpan? MaxAge { get; }

    public int Count { get; private set; }
    public long Bytes { get; private set; }
    public long Evicted { get; private set; }
    public int[] LevelCounts { get; } = new int[(int)LogLevel.None];

    public LogEntry Oldest => At(0).Entry;

    public void Append(LogEntry entry, int size)
    {
        var position = _head + Count;
        if (position >> SegmentShift == _segments.Count)
            _segments.Add(new Slot[SegmentSize]);
        _segments[position >> SegmentShift][position & SegmentMask] = new Slot(entry, size);

        Count++;
        Bytes += size;
        LevelCounts[(int)entry.Level]++;
    }

    /// <summary>Removes the oldest entry and returns its size.</summary>
    public int RemoveOldest()
    {
        ref var slot = ref _segments[0][_head];
        var size = slot.Size;
        LevelCounts[(int)slot.Entry.Level]--;
        slot = default;

        Count--;
        Bytes -= size;
        Evicted++;

        if (++_head == SegmentSize)
        {
            _segments.RemoveAt(0);
            _head = 0;
        }
        else if (Count == 0)
        {
            _head = 0;
        }

        return size;
    }

    /// <summary>Whether the pool is over its entry or byte limit.</summary>
    public bool IsOverLimit =>
        Count > 0 && (Count > MaxEntries || Bytes > MaxBytes);

    /// <summary>Whether the entry is older than <see cref="MaxAge"/> at <paramref name="now"/>.</summary>
    public bool IsExpired(LogEntry entry, DateTimeOffset now) =>
        MaxAge is { } maxAge && entry.Timestamp < now - maxAge;

    /// <summary>Copies up to <paramref name="max"/> entries with an id lower than <paramref name="beforeId"/>, newest first.</summary>
    public void CopyBefore(long beforeId, int max, DateTimeOffset now, List<LogEntry> destination)
    {
        for (var i = LowerBound(beforeId) - 1; i >= 0 && max > 0; i--, max--)
        {
            var entry = At(i).Entry;
            if (IsExpired(entry, now))
                break;
            destination.Add(entry);
        }
    }

    /// <summary>Copies up to <paramref name="max"/> entries with an id higher than <paramref name="afterId"/>, oldest first.</summary>
    public void CopyAfter(long afterId, int max, DateTimeOffset now, List<LogEntry> destination)
    {
        for (var i = LowerBound(afterId + 1); i < Count && max > 0; i++)
        {
            var entry = At(i).Entry;
            if (IsExpired(entry, now))
                continue;
            destination.Add(entry);
            max--;
        }
    }

    public void ForEach(Action<LogEntry> action)
    {
        for (var i = 0; i < Count; i++)
            action(At(i).Entry);
    }

    public LogEntry? Find(long id)
    {
        var index = LowerBound(id);
        return index < Count && At(index).Entry.Id == id ? At(index).Entry : null;
    }

    /// <summary>Index of the first entry with an id greater than or equal to <paramref name="id"/>.</summary>
    private int LowerBound(long id)
    {
        int low = 0, high = Count;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            if (At(mid).Entry.Id < id)
                low = mid + 1;
            else
                high = mid;
        }
        return low;
    }

    private ref Slot At(int index)
    {
        var position = _head + index;
        return ref _segments[position >> SegmentShift][position & SegmentMask];
    }

    private record struct Slot(LogEntry Entry, int Size);
}
