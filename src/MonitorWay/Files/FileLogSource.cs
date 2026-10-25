using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Files.Formats;
using Kododo.MonitorWay.Querying;
using Kododo.MonitorWay.Sources;
using Microsoft.Win32.SafeHandles;

namespace Kododo.MonitorWay.Files;

/// <summary>
/// A log file as a log source. An entry's id is the byte offset where its first line starts, plus one.
/// </summary>
/// <remarks>
/// <para>
/// Byte offsets need no index to be valid, so the newest entries of a large file can be shown right away
/// while <see cref="FileIndex"/> is still being built. The index then makes reads faster by letting them
/// skip chunks that cannot match. A partially written last line is ignored until its newline arrives.
/// </para>
/// <para>
/// An entry can span several lines (a stack trace after the message): it starts on a line the
/// <see cref="ILogFormat"/> recognizes and continues until the next such line. Reads therefore always start
/// and end on entry boundaries: chunk boundaries, or boundaries found by looking for entry starts.
/// </para>
/// </remarks>
internal sealed class FileLogSource : ILogSource
{
    private const int BlockSize = 64 * 1024;
    private const int SampleSize = 64 * 1024;

    private readonly FileIndex _index = new();
    private readonly FileSourceOptions _options;
    private readonly object _updateLock = new();
    private int _updating;
    private volatile bool _opened;
    private volatile ILogFormat? _format;
    private string? _error;

    public FileLogSource(string path, string name, FileSourceOptions? options = null, string? group = null)
    {
        Path = path;
        Name = name;
        Group = group;
        _options = options ?? new FileSourceOptions { Pattern = path };
        Id = "file-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(path)))[..12].ToLowerInvariant();
    }

    public string Path { get; }
    public string Id { get; }
    public string Name { get; }
    public string? Group { get; }
    public string Kind => "file";
    public long Version => _index.Version;

    /// <summary>
    /// How long a single read may scan before it returns what it found with a cursor to continue from. A sparse
    /// search through a large file then proceeds in steps the client repeats, instead of one long request.
    /// </summary>
    internal TimeSpan MaxScanTime { get; set; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Upper bound of the bytes a single read may scan, whatever the time.</summary>
    internal long MaxScanBytes { get; set; } = 1024L * 1024 * 1024;

    public FileIndex Index => _index;

    /// <summary>The detected format, once the file has a complete line.</summary>
    public ILogFormat? Format => _format;

    /// <summary>Set when the file cannot be read as a log.</summary>
    public string? Error => _error;

    public bool IsIndexing => Volatile.Read(ref _updating) == 1;

    /// <summary>
    /// Brings the index up to date with the file in the background. Files nobody has looked at yet are not indexed.
    /// </summary>
    public void Refresh()
    {
        if (_opened && _error is null && Interlocked.CompareExchange(ref _updating, 1, 0) == 0)
            _ = Task.Run(() => UpdateIndex(CancellationToken.None));
    }

    /// <summary>Indexes what was appended since the last update. Normally runs in the background via <see cref="Refresh"/>.</summary>
    internal void UpdateIndex(CancellationToken cancellationToken)
    {
        // Updates continue where the previous one stopped, so two at once would index the same lines twice.
        lock (_updateLock)
        {
            Volatile.Write(ref _updating, 1);
            try
            {
                using var file = Open();
                if (file is not null && FormatOf(file) is { } format && _index.Update(file, format, cancellationToken))
                {
                    // The file was replaced and may be written differently now.
                    _format = null;
                    _error = null;
                    if (FormatOf(file) is { } newFormat)
                        _index.Update(file, newFormat, cancellationToken);
                }
            }
            catch (IOException)
            {
                // The file is being rotated or is locked; the next refresh tries again.
            }
            finally
            {
                Volatile.Write(ref _updating, 0);
            }
        }
    }

    public FileStats GetStats()
    {
        EnsureOpened();
        long length = 0;
        try
        {
            length = new FileInfo(Path).Length;
        }
        catch (IOException)
        {
        }
        return new FileStats(Path, length, _index.IndexedEnd, IsIndexing, _index.LevelCounts, _format?.Name, _error, _index.Version);
    }

    public ReadResult ReadBefore(long? beforeId, int count, LogQueryMatcher matcher, CancellationToken cancellationToken)
    {
        EnsureOpened();
        using var file = Open();
        if (file is null || FormatOf(file) is not { } format)
            return ReadResult.Empty;

        var readable = ReadableEnd(file);
        var reliable = _index.ReliableEnd(readable);
        var position = beforeId is { } before ? Math.Clamp(before - 1, 0, readable) : readable;
        var result = new List<LogEntry>(Math.Min(count, 1024));
        var entries = new List<(int Start, int Length)>();
        var buffer = ArrayPool<byte>.Shared.Rent(BlockSize);
        long scanned = 0;
        var started = Stopwatch.GetTimestamp();
        try
        {
            while (position > 0)
            {
                cancellationToken.ThrowIfCancellationRequested();

                // A text search reads everything that is not skipped: large segments on several threads at once.
                if (matcher.HasText && position <= reliable)
                {
                    var segments = SegmentsBefore(position, matcher, out var next);
                    if (segments.Count > 0 || next < position)
                    {
                        var found = ScanInParallel(file, format, segments, matcher, cancellationToken);
                        for (var s = 0; s < segments.Count; s++)
                        {
                            for (var i = found[s].Count - 1; i >= 0; i--)
                            {
                                result.Add(found[s][i]);
                                if (result.Count == count)
                                {
                                    result.Reverse();
                                    return new ReadResult(result, true, found[s][i].Id);
                                }
                            }
                            scanned += segments[s].End - segments[s].Start;
                        }
                        position = next;
                        if (position > 0 && BudgetSpent(scanned, started))
                        {
                            result.Reverse();
                            return new ReadResult(result, true, position + 1);
                        }
                        continue;
                    }
                }

                long start;
                if (position <= reliable && _index.TryGetChunk(position - 1, out var chunk))
                {
                    start = chunk.Offset;
                    if (chunk.CanSkip(matcher))
                    {
                        position = start;
                        continue;
                    }
                }
                else
                {
                    start = EntryScanner.EntryStartBefore(file, format, position, reliable, ref buffer);
                }

                var range = EntryScanner.ReadRange(file, start, position, ref buffer);
                scanned += range.Length;
                SplitEntriesIfMayMatch(range, start, format, matcher, entries);
                for (var i = entries.Count - 1; i >= 0; i--)
                {
                    var (entryStart, length) = entries[i];
                    if (TryMatch(range.Slice(entryStart, length), start + entryStart, format, matcher) is not { } entry)
                        continue;
                    result.Add(entry);
                    if (result.Count == count)
                    {
                        result.Reverse();
                        return new ReadResult(result, true, entry.Id);
                    }
                }
                position = start;

                if (position > 0 && BudgetSpent(scanned, started))
                {
                    result.Reverse();
                    return new ReadResult(result, true, position + 1);
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        result.Reverse();
        return new ReadResult(result, false, 1);
    }

    public ReadResult ReadAfter(long afterId, int count, LogQueryMatcher matcher, CancellationToken cancellationToken)
    {
        EnsureOpened();
        using var file = Open();
        if (file is null || FormatOf(file) is not { } format)
            return ReadResult.Empty;

        var readable = ReadableEnd(file);
        var reliable = _index.ReliableEnd(readable);
        var position = EntryScanner.EntryStartAtOrAfter(file, format, Math.Max(afterId, 0), readable);
        var result = new List<LogEntry>(Math.Min(count, 1024));
        var entries = new List<(int Start, int Length)>();
        var buffer = ArrayPool<byte>.Shared.Rent(BlockSize);
        long scanned = 0;
        var started = Stopwatch.GetTimestamp();
        try
        {
            while (position < readable)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (matcher.HasText && position < reliable)
                {
                    var segments = SegmentsAfter(position, reliable, matcher, out var next);
                    if (segments.Count > 0 || next > position)
                    {
                        var found = ScanInParallel(file, format, segments, matcher, cancellationToken);
                        for (var s = 0; s < segments.Count; s++)
                        {
                            foreach (var entry in found[s])
                            {
                                result.Add(entry);
                                if (result.Count == count)
                                    return new ReadResult(result, true, entry.Id);
                            }
                            scanned += segments[s].End - segments[s].Start;
                        }
                        position = next;
                        if (position < readable && BudgetSpent(scanned, started))
                            return new ReadResult(result, true, position);
                        continue;
                    }
                }

                long end;
                if (position < reliable && _index.TryGetChunk(position, out var chunk) && chunk.End <= reliable)
                {
                    end = chunk.End;
                    if (chunk.CanSkip(matcher))
                    {
                        position = end;
                        continue;
                    }
                }
                else
                {
                    end = EntryScanner.EntryEndAfter(file, format, position, readable, ref buffer, firstEntryOnly: false);
                }

                var range = EntryScanner.ReadRange(file, position, end, ref buffer);
                scanned += range.Length;
                SplitEntriesIfMayMatch(range, position, format, matcher, entries);
                foreach (var (entryStart, length) in entries)
                {
                    if (TryMatch(range.Slice(entryStart, length), position + entryStart, format, matcher) is not { } entry)
                        continue;
                    result.Add(entry);
                    if (result.Count == count)
                        return new ReadResult(result, true, entry.Id);
                }
                position = end;

                if (position < readable && BudgetSpent(scanned, started))
                    return new ReadResult(result, true, position);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return new ReadResult(result, false, position);
    }

    private const long SegmentBytes = 4L * 1024 * 1024;
    private static readonly int ScanThreads = Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>
    /// Up to one segment per scanning thread before <paramref name="position"/>, newest first: runs of chunks that
    /// cannot be skipped, about <see cref="SegmentBytes"/> each. <paramref name="next"/> is where reading continues.
    /// </summary>
    private List<(long Start, long End)> SegmentsBefore(long position, LogQueryMatcher matcher, out long next)
    {
        var segments = new List<(long Start, long End)>();
        long start = position, end = position;
        while (start > 0 && segments.Count < ScanThreads && _index.TryGetChunk(start - 1, out var chunk))
        {
            if (chunk.CanSkip(matcher))
            {
                if (end > start)
                    segments.Add((start, end));
                start = end = chunk.Offset;
                continue;
            }
            start = chunk.Offset;
            if (end - start >= SegmentBytes)
            {
                segments.Add((start, end));
                end = start;
            }
        }
        if (end > start && segments.Count < ScanThreads)
        {
            segments.Add((start, end));
            end = start;
        }
        next = end;
        return segments;
    }

    /// <summary>Up to one segment per scanning thread from <paramref name="position"/>, oldest first, within the trusted chunks.</summary>
    private List<(long Start, long End)> SegmentsAfter(long position, long reliable, LogQueryMatcher matcher, out long next)
    {
        var segments = new List<(long Start, long End)>();
        long start = position, end = position;
        while (end < reliable && segments.Count < ScanThreads && _index.TryGetChunk(end, out var chunk) && chunk.End <= reliable)
        {
            if (chunk.CanSkip(matcher))
            {
                if (end > start)
                    segments.Add((start, end));
                start = end = chunk.End;
                continue;
            }
            end = chunk.End;
            if (end - start >= SegmentBytes)
            {
                segments.Add((start, end));
                start = end;
            }
        }
        if (end > start && segments.Count < ScanThreads)
        {
            segments.Add((start, end));
            start = end;
        }
        next = start;
        return segments;
    }

    /// <summary>The matching entries of each segment, in file order, read and parsed on several threads.</summary>
    private static List<LogEntry>[] ScanInParallel(SafeFileHandle file, ILogFormat format, List<(long Start, long End)> segments,
        LogQueryMatcher matcher, CancellationToken cancellationToken)
    {
        var found = new List<LogEntry>[segments.Count];
        var options = new ParallelOptions { MaxDegreeOfParallelism = ScanThreads, CancellationToken = cancellationToken };
        Parallel.For(0, segments.Count, options, format.ForThread, (i, _, threadFormat) =>
        {
            var (start, end) = segments[i];
            var buffer = ArrayPool<byte>.Shared.Rent((int)(end - start));
            try
            {
                var range = buffer.AsSpan(0, RandomAccess.Read(file, buffer.AsSpan(0, (int)(end - start)), start));
                var entries = new List<(int Start, int Length)>();
                SplitEntriesIfMayMatch(range, start, threadFormat, matcher, entries);
                var matches = new List<LogEntry>();
                foreach (var (entryStart, length) in entries)
                {
                    if (TryMatch(range.Slice(entryStart, length), start + entryStart, threadFormat, matcher) is { } entry)
                        matches.Add(entry);
                }
                found[i] = matches;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
            return threadFormat;
        }, _ => { });
        return found;
    }

    public LogEntry? Find(long id)
    {
        EnsureOpened();
        using var file = Open();
        if (file is null || FormatOf(file) is not { } format)
            return null;

        var start = id - 1;
        var readable = ReadableEnd(file);
        if (start < 0 || start >= readable || EntryScanner.EntryStartAtOrAfter(file, format, start, readable) != start)
            return null;

        var buffer = ArrayPool<byte>.Shared.Rent(BlockSize);
        try
        {
            var end = EntryScanner.EntryEndAfter(file, format, start, readable, ref buffer, firstEntryOnly: true);
            var entry = EntryScanner.ReadRange(file, start, end, ref buffer);
            return FileIndex.LineContent(EntryText.FirstLine(entry), start).Trim(" \t"u8).IsEmpty
                ? null
                : format.Parse(WithoutBom(entry, start), id);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

private bool BudgetSpent(long scanned, long started) =>
        scanned >= MaxScanBytes || Stopwatch.GetElapsedTime(started) >= MaxScanTime;

    private static LogEntry? TryMatch(ReadOnlySpan<byte> entry, long offset, ILogFormat format, LogQueryMatcher matcher)
    {
        var first = FileIndex.LineContent(EntryText.FirstLine(entry), offset);
        if (first.Trim(" \t"u8).IsEmpty)
            return null;

        format.ReadHeader(first, out var level, out var timestamp);
        if (matcher.ExcludesLevel(level) || matcher.ExcludesTime(timestamp))
            return null;

        if (!matcher.RawTextMayMatch(entry))
            return null;

        var parsed = format.Parse(WithoutBom(entry, offset), offset + 1);
        return !matcher.HasText || matcher.Matches(parsed) ? parsed : null;
    }

    private static ReadOnlySpan<byte> WithoutBom(ReadOnlySpan<byte> entry, long offset) =>
        offset == 0 && entry.StartsWith(FormatDetector.Bom) ? entry[FormatDetector.Bom.Length..] : entry;


    /// <summary>A range without the words the query needs is not split at all, so none of its entries gets parsed.</summary>
    private static void SplitEntriesIfMayMatch(ReadOnlySpan<byte> range, long rangeOffset, ILogFormat format, LogQueryMatcher matcher,
        List<(int Start, int Length)> entries)
    {
        if (matcher.RawTextMayMatch(range))
            SplitEntries(range, rangeOffset, format, entries);
        else
            entries.Clear();
    }
    /// <summary>
    /// Splits a range that starts on an entry boundary into entries. The first line always starts an entry,
    /// which keeps lines before the first recognized entry (a cut-off start of the file) visible.
    /// </summary>
    private static void SplitEntries(ReadOnlySpan<byte> range, long rangeOffset, ILogFormat format, List<(int Start, int Length)> entries)
    {
        entries.Clear();
        var entryStart = -1;
        var position = 0;
        while (position < range.Length)
        {
            var newLine = range[position..].IndexOf((byte)'\n');
            var lineEnd = newLine < 0 ? range.Length : position + newLine + 1;
            if (entryStart < 0 || EntryScanner.IsEntryStart(format, range[position..lineEnd], rangeOffset + position))
            {
                if (entryStart >= 0)
                    entries.Add((entryStart, position - entryStart));
                entryStart = position;
            }
            position = lineEnd;
        }
        if (entryStart >= 0)
            entries.Add((entryStart, range.Length - entryStart));
    }

    /// <summary>
    /// Entry counts in <paramref name="buckets"/> equal byte ranges of the file, from the index. Ids are offsets
    /// plus one, so the range in id space is <c>[1, length + 1)</c>. The part not indexed yet counts as empty.
    /// </summary>
    public Overview GetOverview(int buckets)
    {
        EnsureOpened();
        long length = 0;
        try
        {
            length = new FileInfo(Path).Length;
        }
        catch (IOException)
        {
        }

        var result = new OverviewBucket[buckets];
        var width = Math.Max(1.0, (double)length / buckets);
        foreach (var chunk in _index.Chunks)
        {
            var bucket = Math.Min(buckets - 1, (int)(chunk.Offset / width));
            result[bucket] = result[bucket] with
            {
                Total = result[bucket].Total + chunk.EntryCount,
                Warnings = result[bucket].Warnings + chunk.Warnings,
                Errors = result[bucket].Errors + chunk.Errors,
            };
        }
        return new Overview(1, length + 1, result);
    }

    /// <summary>The chunks indexed so far, for summaries across several files.</summary>
    public ChunkInfo[] GetChunks()
    {
        EnsureOpened();
        return _index.Chunks;
    }

    /// <summary>Entries per category in the indexed part of the file; empty for formats without categories.</summary>
    public Dictionary<string, long> GetCategoryCounts()
    {
        EnsureOpened();
        return _index.CategoryCounts;
    }

    /// <summary>When the file was last read through MonitorWay; idle files get their index released.</summary>
    public long LastAccess => Volatile.Read(ref _lastAccess);

    private long _lastAccess;

    /// <summary>Frees the index. The next read opens the file again and the index is rebuilt in the background.</summary>
    public void Release()
    {
        lock (_updateLock)
        {
            _opened = false;
            _index.Clear();
        }
    }

    private void EnsureOpened()
    {
        Volatile.Write(ref _lastAccess, Environment.TickCount64);
        if (_opened)
            return;
        _opened = true;
        Refresh();
    }

    /// <summary>Whether the index is in use: the file was opened and has not been released.</summary>
    public bool IsOpened => _opened;

    private SafeFileHandle? Open()
    {
        try
        {
            return File.OpenHandle(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>Detects the format from the start of the file once; until the file has a complete line there is none.</summary>
    private ILogFormat? FormatOf(SafeFileHandle file)
    {
        if (_format is not null || _error is not null)
            return _format;

        var sample = new byte[SampleSize];
        var read = RandomAccess.Read(file, sample, 0);
        var format = FormatDetector.Detect(sample.AsSpan(0, read), _options, out var error, DateOfFile());
        _error = error;
        _format = format;
        return format;
    }

    private static readonly System.Text.RegularExpressions.Regex DateInName =
        new(@"(?<!\d)(?<y>20\d{2})-?(?<m>[01]\d)-?(?<d>[0-3]\d)(?!\d)", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// The day a file's entries belong to, for layouts that write only the time of day: the date in its name, as
    /// daily rotation names files (<c>app-20261013.log</c>), or else the day it was last written to.
    /// </summary>
    private DateOnly DateOfFile()
    {
        var match = DateInName.Match(System.IO.Path.GetFileName(Path));
        if (match.Success && DateOnly.TryParseExact($"{match.Groups["y"]}-{match.Groups["m"]}-{match.Groups["d"]}", "yyyy-MM-dd", out var named))
            return named;
        try
        {
            return DateOnly.FromDateTime(File.GetLastWriteTime(Path));
        }
        catch (IOException)
        {
            return DateOnly.FromDateTime(DateTime.Today);
        }
    }

    /// <summary>End of the last complete line; a line still being written is not readable yet.</summary>
    private long ReadableEnd(SafeFileHandle file)
    {
        var length = RandomAccess.GetLength(file);
        var floor = Math.Min(_index.IndexedEnd, length);
        Span<byte> block = stackalloc byte[4096];
        for (var end = length; end > floor;)
        {
            var start = Math.Max(floor, end - block.Length);
            var read = RandomAccess.Read(file, block[..(int)(end - start)], start);
            var newLine = block[..read].LastIndexOf((byte)'\n');
            if (newLine >= 0)
                return start + newLine + 1;
            end = start;
        }
        return floor;
    }
}

internal sealed record FileStats(
    string Path,
    long Length,
    long IndexedEnd,
    bool Indexing,
    long[] LevelCounts,
    string? Format,
    string? Error,
    long Version);
