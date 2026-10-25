using Kododo.MonitorWay.Core.Model;

namespace Kododo.MonitorWay.Memory;

/// <summary>
/// Traces of one retention pool, ordered by id. Appends go to the tail and evictions remove from the head; a trace
/// stored again with late spans leaves a gap where it was. Not thread-safe: <see cref="MemoryTraceStore"/> serializes
/// all access under its lock.
/// </summary>
internal sealed class TracePool(string name, TracePoolOptions options)
{
    private const int CompactAfter = 1024;

    private readonly List<Slot> _slots = [];
    private int _head;

    public string Name { get; } = name;
    public int? MaxTraces { get; } = options.MaxTraces;
    public long? MaxBytes { get; } = options.MaxBytes;
    public TimeSpan? MaxAge { get; } = options.MaxAge;

    public int Count { get; private set; }
    public long Bytes { get; private set; }
    public long Evicted { get; private set; }

    /// <summary>When the oldest trace was stored, or <c>null</c> when the pool is empty.</summary>
    public DateTimeOffset? OldestStored
    {
        get
        {
            SkipGaps();
            return Count > 0 ? _slots[_head].Stored : null;
        }
    }

    public bool IsOverLimit => Count > 0 && (Count > MaxTraces || Bytes > MaxBytes);

    public bool IsOldestExpired(DateTimeOffset now) =>
        MaxAge is { } maxAge && OldestStored is { } stored && stored < now - maxAge;

    public void Append(TraceEntry entry, int size, DateTimeOffset now)
    {
        _slots.Add(new Slot(entry.Id, entry, size, now));
        Count++;
        Bytes += size;
    }

    /// <summary>Removes the oldest trace and returns it with its size.</summary>
    public (TraceEntry Entry, int Size) RemoveOldest()
    {
        SkipGaps();
        var slot = _slots[_head];
        _slots[_head++] = default;
        Count--;
        Bytes -= slot.Size;
        Evicted++;
        Compact();
        return (slot.Entry!, slot.Size);
    }

    /// <summary>Removes the trace with the id, which is about to be stored again; returns its size.</summary>
    public int Remove(long id)
    {
        int low = _head, high = _slots.Count;
        while (low < high)
        {
            var mid = (low + high) >>> 1;
            if (_slots[mid].Id < id)
                low = mid + 1;
            else
                high = mid;
        }
        if (low == _slots.Count || _slots[low].Id != id || _slots[low].Entry is null)
            return 0;

        var size = _slots[low].Size;
        _slots[low] = _slots[low] with { Entry = null };
        Count--;
        Bytes -= size;
        return size;
    }

    /// <summary>The traces, oldest first.</summary>
    public TraceEntry[] Snapshot()
    {
        var entries = new TraceEntry[Count];
        var i = 0;
        for (var position = _head; position < _slots.Count; position++)
        {
            if (_slots[position].Entry is { } entry)
                entries[i++] = entry;
        }
        return entries;
    }

    private void SkipGaps()
    {
        while (_head < _slots.Count && _slots[_head].Entry is null)
            _head++;
        Compact();
    }

    private void Compact()
    {
        if (_head == _slots.Count)
        {
            _slots.Clear();
            _head = 0;
        }
        else if (_head >= CompactAfter && _head * 2 >= _slots.Count)
        {
            _slots.RemoveRange(0, _head);
            _head = 0;
        }
    }

    private record struct Slot(long Id, TraceEntry? Entry, int Size, DateTimeOffset Stored);
}
