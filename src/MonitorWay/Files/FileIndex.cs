using System.Buffers;
using Kododo.MonitorWay.Files.Formats;
using Kododo.MonitorWay.Querying;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;

namespace Kododo.MonitorWay.Files;

/// <summary>
/// A run of whole lines of a file, with a summary that lets a query skip it without reading it.
/// </summary>
/// <param name="HasUnknownTime">Some line has no timestamp, so the time range cannot be used for skipping.</param>
internal readonly record struct ChunkInfo(
    long Offset,
    int Length,
    long FirstEntry,
    int EntryCount,
    long MinTicks,
    long MaxTicks,
    bool HasUnknownTime,
    byte LevelMask,
    int Warnings = 0,
    int Errors = 0)
{
    public long End => Offset + Length;

    public static byte LevelBit(LogLevel level) => (byte)(1 << (int)level);

    public bool CanSkip(LogQueryMatcher matcher)
    {
        var levelsIncluded = false;
        for (var level = LogLevel.Trace; level <= LogLevel.None; level++)
        {
            if ((LevelMask & LevelBit(level)) != 0 && !matcher.ExcludesLevel(level))
            {
                levelsIncluded = true;
                break;
            }
        }
        if (!levelsIncluded)
            return true;
        if (HasUnknownTime || EntryCount == 0)
            return false;
        return matcher.From is { } from && MaxTicks < from.UtcTicks
            || matcher.To is { } to && MinTicks > to.UtcTicks;
    }
}

/// <summary>
/// Chunks of ~64 KB covering a prefix of a file. Built in the background from the start of the file and
/// extended as the file grows; reads use it to skip chunks and fall back to plain reading past its end.
/// </summary>
/// <remarks>
/// Ids are byte offsets, not ordinals, so the index is never needed to address an entry, only to read
/// fewer bytes. A file that shrinks or whose first bytes change was truncated or replaced: the index starts
/// over and <see cref="Version"/> changes, which tells clients their ids are stale.
/// </remarks>
internal sealed class FileIndex
{
    public const int TargetChunkSize = 64 * 1024;
    private const int ReadBlockSize = 1024 * 1024;
    private const int HeadSignatureSize = 256;
    private const long ParallelThreshold = 32L * 1024 * 1024;
    private const long SegmentSize = 8L * 1024 * 1024;

    private readonly object _lock = new();
    private ChunkInfo[] _chunks = new ChunkInfo[64];
    private int _count;
    private long _indexedEnd;
    private readonly long[] _levelCounts = new long[(int)LogLevel.None + 1];
    private readonly long[] _pendingCounts = new long[(int)LogLevel.None + 1];
    private readonly Dictionary<string, long> _categoryCounts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _pendingCategories = new(StringComparer.Ordinal);
    private readonly CategoryNames _categoryNames = new();
    private byte[]? _head;

    // The last chunk stays open while it is smaller than the target, so a growing file does not end up in tiny chunks.
    private Builder _open;
    private bool _openPublished;

    public long Version { get; private set; }

    public long IndexedEnd
    {
        get { lock (_lock) return _indexedEnd; }
    }

    public int ChunkCount
    {
        get { lock (_lock) return _count; }
    }

    public long[] LevelCounts
    {
        get { lock (_lock) return (long[])_levelCounts.Clone(); }
    }

    /// <summary>Entries per category in the indexed part, for formats that have categories.</summary>
    public Dictionary<string, long> CategoryCounts
    {
        get { lock (_lock) return new Dictionary<string, long>(_categoryCounts, StringComparer.Ordinal); }
    }

    /// <summary>Approximate memory held by the chunks.</summary>
    public long MemoryBytes
    {
        get { lock (_lock) return (long)_chunks.Length * System.Runtime.CompilerServices.Unsafe.SizeOf<ChunkInfo>(); }
    }

    /// <summary>
    /// Drops the chunks to free memory. Ids stay valid (they are byte offsets), so <see cref="Version"/> does not
    /// change; the index is simply built again when the file is read.
    /// </summary>
    public void Clear()
    {
        lock (_lock)
        {
            _chunks = new ChunkInfo[64];
            _count = 0;
            _indexedEnd = 0;
            Array.Clear(_levelCounts);
            Array.Clear(_pendingCounts);
            _categoryCounts.Clear();
            _pendingCategories.Clear();
            _open = default;
            _openPublished = false;
            _head = null;
        }
    }

    /// <summary>A copy of the chunks, for summaries such as the overview of a file.</summary>
    public ChunkInfo[] Chunks
    {
        get { lock (_lock) return _chunks[.._count]; }
    }

    /// <summary>The chunk that contains <paramref name="position"/>, if that part of the file is indexed.</summary>
    public bool TryGetChunk(long position, out ChunkInfo chunk)
    {
        lock (_lock)
        {
            if (position < 0 || position >= _indexedEnd)
            {
                chunk = default;
                return false;
            }

            int low = 0, high = _count - 1;
            while (low < high)
            {
                var mid = (low + high + 1) >>> 1;
                if (_chunks[mid].Offset <= position)
                    low = mid;
                else
                    high = mid - 1;
            }
            chunk = _chunks[low];
            return true;
        }
    }

    /// <summary>
    /// Indexes the lines added since the last call. Detects truncation and replacement first.
    /// </summary>
    /// <returns><c>true</c> when the file was truncated or replaced and the index started over.</returns>
    public bool Update(SafeFileHandle file, ILogFormat format, CancellationToken cancellationToken)
    {
        var length = RandomAccess.GetLength(file);
        var reset = CheckReplaced(file, length);

        if (_indexedEnd == 0 && _open.Length == 0 && length >= ParallelThreshold)
            IndexInParallel(file, format, length, cancellationToken);
        IndexBlocks(file, format, length, cancellationToken);
        return reset;
    }

    /// <summary>
    /// Indexes most of a large file at once on several threads: it is split into segments that start on entry
    /// boundaries, and each segment is indexed separately. Segments are appended in file order as soon as all
    /// segments before them are done, so the indexed part keeps growing from the start of the file. The rest of
    /// the file, from the last entry start found near its end, is left to the sequential indexing that follows.
    /// </summary>
    private void IndexInParallel(SafeFileHandle file, ILogFormat format, long length, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(64 * 1024);
        long last;
        try
        {
            var block = EntryScanner.ReadRange(file, Math.Max(0, length - 64 * 1024), length, ref buffer);
            var readable = length - block.Length + block.LastIndexOf((byte)'\n') + 1;
            last = EntryScanner.EntryStartBefore(file, format, readable, 0, ref buffer);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
        if (last <= SegmentSize)
            return;

        var bounds = new List<long> { 0 };
        for (var position = SegmentSize; position < last; position += SegmentSize)
        {
            var bound = EntryScanner.EntryStartAtOrAfter(file, format, position, last);
            if (bound > bounds[^1] && bound < last)
                bounds.Add(bound);
        }
        bounds.Add(last);

        var done = new FileIndex?[bounds.Count - 1];
        var next = 0;
        var gate = new object();
        var options = new ParallelOptions
        {
            // The host application keeps the other half of the processor.
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            CancellationToken = cancellationToken,
        };
        Parallel.For(0, done.Length, options, format.ForThread, (i, _, threadFormat) =>
        {
            var segment = new FileIndex { _open = new Builder(bounds[i], 0) };
            segment.IndexBlocks(file, threadFormat, bounds[i + 1], cancellationToken);
            lock (gate)
            {
                done[i] = segment;
                while (next < done.Length && done[next] is { } ready)
                {
                    Append(ready);
                    done[next++] = null;
                }
            }
            return threadFormat;
        }, _ => { });

        lock (_lock)
        {
            var entries = _count == 0 ? 0 : _chunks[_count - 1].FirstEntry + _chunks[_count - 1].EntryCount;
            _open = new Builder(last, entries);
            _openPublished = false;
        }
    }

    /// <summary>Adds the chunks of a segment that starts where this index ends.</summary>
    private void Append(FileIndex segment)
    {
        lock (_lock)
        {
            var firstEntry = _count == 0 ? 0 : _chunks[_count - 1].FirstEntry + _chunks[_count - 1].EntryCount;
            for (var i = 0; i < segment._count; i++)
            {
                if (_count == _chunks.Length)
                    Array.Resize(ref _chunks, _chunks.Length * 2);
                _chunks[_count++] = segment._chunks[i] with { FirstEntry = segment._chunks[i].FirstEntry + firstEntry };
            }
            for (var level = 0; level < _levelCounts.Length; level++)
                _levelCounts[level] += segment._levelCounts[level];
            foreach (var (category, count) in segment._categoryCounts)
                _categoryCounts[category] = _categoryCounts.GetValueOrDefault(category) + count;
            _indexedEnd = segment._indexedEnd;
        }
    }

    /// <summary>Indexes complete lines from where the index ends up to <paramref name="end"/>.</summary>
    private void IndexBlocks(SafeFileHandle file, ILogFormat format, long end, CancellationToken cancellationToken)
    {
        var length = end;
        var position = _open.Offset + _open.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(ReadBlockSize);
        try
        {
            while (position < length)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = RandomAccess.Read(file, buffer.AsSpan(0, (int)Math.Min(buffer.Length, length - position)), position);
                if (read == 0)
                    break;

                var block = buffer.AsSpan(0, read);
                var lastNewLine = block.LastIndexOf((byte)'\n');
                if (lastNewLine < 0)
                {
                    if (position + read >= length)
                        break; // The last line is still being written.
                    var larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                    continue;
                }

                IndexLines(block[..(lastNewLine + 1)], position, format);
                position += lastNewLine + 1;
                Publish();
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    /// <summary>
    /// Counts entries by their first line; continuation lines only extend the current entry. A chunk is closed
    /// right before an entry start, so every closed chunk ends where the next entry begins.
    /// </summary>
    private void IndexLines(ReadOnlySpan<byte> block, long blockOffset, ILogFormat format)
    {
        var offset = 0;
        while (offset < block.Length)
        {
            var length = block[offset..].IndexOf((byte)'\n') + 1;
            var lineOffset = blockOffset + offset;
            var line = LineContent(block.Slice(offset, length), lineOffset);

            var level = LogLevel.None;
            DateTimeOffset? timestamp = null;
            var start = format.TryReadEntryStart(line, out level, out timestamp);
            if (!start && lineOffset == 0)
            {
                // The first line always starts an entry, recognized or not.
                format.ReadHeader(line, out level, out timestamp);
                start = true;
            }
            if (start)
            {
                if (_open.Length >= TargetChunkSize)
                {
                    Publish();
                    _open = new Builder(lineOffset, _open.FirstEntry + _open.EntryCount);
                    _openPublished = false;
                }
                _open.Add(level, timestamp);
                _pendingCounts[(int)level]++;
                if (format.ReadCategory(line, _categoryNames) is { } category)
                    _pendingCategories[category] = _pendingCategories.GetValueOrDefault(category) + 1;
            }
            _open.Length += length;
            offset += length;
        }
    }

    /// <summary>A line without its line break, and without the UTF-8 byte order mark at the start of the file.</summary>
    internal static ReadOnlySpan<byte> LineContent(ReadOnlySpan<byte> line, long offset)
    {
        line = line.TrimEnd("\r\n"u8);
        return offset == 0 && line.StartsWith(FormatDetector.Bom) ? line[FormatDetector.Bom.Length..] : line;
    }

    /// <summary>
    /// End of the part of the file whose chunks can be trusted to end on entry boundaries. The last chunk is
    /// excluded while the file has more complete lines than the index covers, because its last entry may
    /// continue past the indexed end.
    /// </summary>
    public long ReliableEnd(long readableEnd)
    {
        lock (_lock)
        {
            if (_indexedEnd >= readableEnd || _count == 0)
                return Math.Min(_indexedEnd, readableEnd);
            return _chunks[_count - 1].Offset;
        }
    }

    private void Publish()
    {
        if (_open.Length == 0)
            return;
        lock (_lock)
        {
            for (var level = 0; level < _levelCounts.Length; level++)
                _levelCounts[level] += _pendingCounts[level];
            Array.Clear(_pendingCounts);
            foreach (var (category, count) in _pendingCategories)
                _categoryCounts[category] = _categoryCounts.GetValueOrDefault(category) + count;
            _pendingCategories.Clear();

            if (_openPublished)
            {
                _chunks[_count - 1] = _open.ToChunk();
            }
            else
            {
                if (_count == _chunks.Length)
                    Array.Resize(ref _chunks, _chunks.Length * 2);
                _chunks[_count++] = _open.ToChunk();
                _openPublished = true;
            }
            _indexedEnd = _open.Offset + _open.Length;
        }
    }

    private bool CheckReplaced(SafeFileHandle file, long length)
    {
        Span<byte> head = stackalloc byte[HeadSignatureSize];
        head = head[..RandomAccess.Read(file, head[..(int)Math.Min(HeadSignatureSize, length)], 0)];

        var replaced = length < _indexedEnd || _head is not null && !head.StartsWith(_head.AsSpan(0, Math.Min(_head.Length, head.Length)));
        if (replaced)
        {
            lock (_lock)
            {
                _count = 0;
                _indexedEnd = 0;
                Array.Clear(_levelCounts);
                Array.Clear(_pendingCounts);
                _categoryCounts.Clear();
                _pendingCategories.Clear();
                _open = default;
                _openPublished = false;
                Version++;
            }
        }
        if (_head is null || head.Length > _head.Length || replaced)
            _head = head.ToArray();
        return replaced;
    }

    private struct Builder(long offset, long firstEntry)
    {
        public readonly long Offset = offset;
        public readonly long FirstEntry = firstEntry;
        public int Length;
        public int EntryCount;
        private long _minTicks;
        private long _maxTicks;
        private bool _hasTime;
        private bool _hasUnknownTime;
        private byte _levelMask;
        private int _warnings;
        private int _errors;

        public void Add(LogLevel level, DateTimeOffset? timestamp)
        {
            EntryCount++;
            _levelMask |= ChunkInfo.LevelBit(level);
            if (level == LogLevel.Warning)
                _warnings++;
            else if (level is LogLevel.Error or LogLevel.Critical)
                _errors++;
            if (timestamp is { } t)
            {
                var ticks = t.UtcTicks;
                _minTicks = _hasTime ? Math.Min(_minTicks, ticks) : ticks;
                _maxTicks = _hasTime ? Math.Max(_maxTicks, ticks) : ticks;
                _hasTime = true;
            }
            else
            {
                _hasUnknownTime = true;
            }
        }

        public readonly ChunkInfo ToChunk() =>
            new(Offset, Length, FirstEntry, EntryCount, _minTicks, _maxTicks, _hasUnknownTime, _levelMask, _warnings, _errors);
    }
}
