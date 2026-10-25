using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Files;
using Kododo.MonitorWay.Querying;
using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.Sources;

/// <summary>
/// Several sources read as one, ordered by time: the rotated files of one log, the replicas of a service, or
/// sources chosen together in the UI.
/// </summary>
/// <remarks>
/// <para>
/// An entry's id is its member's id times <see cref="MaxMembers"/> plus the member's index, so it stays a cursor:
/// entries are ordered by time, then by member index, then by the member's own id. Reading before or after an id
/// reads the member it came from by position and every other member up to or from its time; the members' indexes
/// skip what lies on the other side of that time. An entry without a time keeps its place in its file.
/// </para>
/// <para>
/// A member that stops early (its scan budget ran out) limits the result to what is certainly complete: the
/// entries on the near side of where it stopped. The cursor then points at that place, so the next read
/// continues there.
/// </para>
/// <para>
/// A slot of <see cref="Members"/> can be empty when a member went away; indexes of the others stay the same.
/// Any change of the members or of a member's version changes <see cref="Version"/>.
/// </para>
/// </remarks>
internal sealed class MergedSource : ILogSource
{
    public const int MaxMembers = 1024;

    public MergedSource(string id, string name, string? group, IReadOnlyList<ILogSource?> members)
    {
        if (members.Count > MaxMembers)
            throw new ArgumentException($"A merged source can have at most {MaxMembers} members.", nameof(members));
        Id = id;
        Name = name;
        Group = group;
        Members = members;
    }

    public string Id { get; }
    public string Name { get; }
    public string? Group { get; }
    /// <summary>
    /// <c>merged</c> when the members are different sources; otherwise, e.g. for the rotated files of one log, the kind
    /// of its members.
    /// </summary>
    public string Kind => HasDistinctMembers ? "merged" : Present.FirstOrDefault()?.Kind ?? "merged";

    /// <summary>
    /// Whether the members have different names, e.g. replicas checked together, rather than being the rotated files of
    /// one log, which share its name.
    /// </summary>
    public bool HasDistinctMembers => Present.Select(m => m.Name).Distinct(StringComparer.Ordinal).Skip(1).Any();

    /// <summary>
    /// The name of the member an entry of this source comes from, by its id; <c>null</c> when the members share their
    /// name, so it would say nothing.
    /// </summary>
    public Func<long, string?> MemberNames()
    {
        if (!HasDistinctMembers)
            return _ => null;
        var names = Members.Select(m => m?.Name).ToArray();
        return id => Decode(id).Member is var member && member < names.Length ? names[member] : null;
    }

    public IReadOnlyList<ILogSource?> Members { get; }

    public IEnumerable<ILogSource> Present => Members.OfType<ILogSource>();

    public long Version
    {
        get
        {
            var version = 17L;
            foreach (var member in Members)
                version = unchecked(version * 31 + (member is null ? 0 : member.Id.GetHashCode() * 7919L + member.Version + 1));
            return version;
        }
    }

    /// <summary>The first error of a member file, for showing next to the entries.</summary>
    public string? Error => Present.OfType<FileLogSource>().Select(f => f.Error).FirstOrDefault(e => e is not null);

    public static long Encode(int member, long id) => id * MaxMembers + member;

    public static (int Member, long Id) Decode(long id) => ((int)(id % MaxMembers), id / MaxMembers);

    public ReadResult ReadBefore(long? beforeId, int count, LogQueryMatcher matcher, CancellationToken cancellationToken)
    {
        var reads = new ReadResult?[Members.Count];
        if (beforeId is not { } before)
        {
            ForEachMember((i, member) => reads[i] = member.ReadBefore(null, count, matcher, cancellationToken), cancellationToken);
        }
        else
        {
            var (anchor, id) = Decode(before);
            if (MemberAt(anchor) is not { } anchorSource)
                return ReadResult.Empty;
            var time = TimeNear(anchorSource, id, cancellationToken);
            ForEachMember((i, member) => reads[i] = i == anchor
                ? member.ReadBefore(id, count, matcher, cancellationToken)
                : time is { } t
                    ? ReadBeforeTime(member, count, matcher, i < anchor ? t : t.AddTicks(-1), cancellationToken)
                    : null, cancellationToken);
        }

        var (items, hasMore, stops) = Collect(reads, count, cancellationToken);
        items.Sort();
        // Members that stopped early may have entries newer than the ones others returned below the stop.
        var stop = stops.Count > 0 ? stops.Max() : (Item?)null;
        if (stop is { } s)
            items.RemoveAll(item => item.CompareTo(s) < 0);
        if (items.Count > count)
        {
            items.RemoveRange(0, items.Count - count);
            hasMore = true;
        }
        return Result(items, hasMore, items.Count < count ? stop : null);
    }

    public ReadResult ReadAfter(long afterId, int count, LogQueryMatcher matcher, CancellationToken cancellationToken)
    {
        var reads = new ReadResult?[Members.Count];
        if (afterId <= 0)
        {
            ForEachMember((i, member) => reads[i] = member.ReadAfter(0, count, matcher, cancellationToken), cancellationToken);
        }
        else
        {
            var (anchor, id) = Decode(afterId);
            if (MemberAt(anchor) is not { } anchorSource)
                return ReadResult.Empty;
            var time = TimeNear(anchorSource, id, cancellationToken);
            ForEachMember((i, member) => reads[i] = i == anchor
                ? member.ReadAfter(id, count, matcher, cancellationToken)
                : time is { } t
                    ? ReadAfterTime(member, count, matcher, i < anchor ? t.AddTicks(1) : t, cancellationToken)
                    : null, cancellationToken);
        }

        var (items, hasMore, stops) = Collect(reads, count, cancellationToken);
        items.Sort();
        var stop = stops.Count > 0 ? stops.Min() : (Item?)null;
        if (stop is { } s)
            items.RemoveAll(item => item.CompareTo(s) >= 0);
        if (items.Count > count)
        {
            items.RemoveRange(count, items.Count - count);
            hasMore = true;
        }
        return Result(items, hasMore, items.Count < count ? stop : null);
    }

    public LogEntry? Find(long id)
    {
        var (member, memberId) = Decode(id);
        return MemberAt(member)?.Find(memberId) is { } entry ? entry with { Id = id } : null;
    }

    /// <summary>
    /// For each member, the id after which its entries come after <paramref name="afterId"/>, or its end when that is
    /// <c>null</c>. The live stream follows the members from there one by one, so an entry a replica writes a moment
    /// late still arrives.
    /// </summary>
    public long?[] PositionsAfter(long? afterId, CancellationToken cancellationToken)
    {
        var positions = new long?[Members.Count];
        if (afterId is not { } after)
        {
            ForEachMember((i, member) => positions[i] = EndOf(member, cancellationToken), cancellationToken);
            return positions;
        }

        var (anchor, id) = Decode(after);
        var time = MemberAt(anchor) is { } anchorSource ? TimeNear(anchorSource, id, cancellationToken) : null;
        ForEachMember((i, member) =>
        {
            if (i == anchor)
                positions[i] = id;
            else if (time is { } t)
                positions[i] = member.ReadBefore(null, 1, LogQueryMatcher.All.Within(null, i < anchor ? t : t.AddTicks(-1)), cancellationToken)
                    .Entries.LastOrDefault()?.Id ?? 0;
            else
                positions[i] = EndOf(member, cancellationToken);
        }, cancellationToken);
        return positions;
    }

    /// <summary>The id after the newest entry of <paramref name="source"/>, where following it starts.</summary>
    internal static long EndOf(ILogSource source, CancellationToken cancellationToken) =>
        source is FileLogSource file
            ? file.ReadAfter(long.MaxValue, 1, LogQueryMatcher.All, cancellationToken).Cursor ?? 0
            : source.ReadBefore(null, 1, LogQueryMatcher.All, cancellationToken).Entries.LastOrDefault()?.Id ?? 0;

    /// <summary>Sums of the member files' statistics.</summary>
    public FileStats GetStats()
    {
        var files = Present.OfType<FileLogSource>().Select(f => f.GetStats()).ToList();
        var levels = new long[(int)LogLevel.None + 1];
        foreach (var file in files)
        {
            for (var level = 0; level < levels.Length && level < file.LevelCounts.Length; level++)
                levels[level] += file.LevelCounts[level];
        }
        var formats = files.Select(f => f.Format).OfType<string>().Distinct().ToList();
        return new FileStats(
            files.Count == 1 ? files[0].Path : $"{files.Count} files",
            files.Sum(f => f.Length),
            files.Sum(f => Math.Min(f.IndexedEnd, f.Length)),
            files.Any(f => f.Indexing),
            levels,
            formats.Count > 0 ? string.Join(", ", formats) : null,
            files.Select(f => f.Error).FirstOrDefault(e => e is not null),
            Version);
    }

    public Dictionary<string, long> GetCategoryCounts()
    {
        var counts = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var member in Present)
        {
            var memberCounts = member switch
            {
                FileLogSource file => file.GetCategoryCounts(),
                MemorySource memory => memory.Buffer.GetCategoryCounts(),
                _ => [],
            };
            foreach (var (category, count) in memberCounts)
                counts[category] = counts.GetValueOrDefault(category) + count;
        }
        return counts;
    }

    /// <summary>
    /// Entry counts of the member files along time, from their indexes. Merged ids are not contiguous, so the overview
    /// is in time space: <see cref="Overview.Start"/> and <see cref="Overview.End"/> are Unix milliseconds (exact in
    /// JavaScript, unlike ticks), and a position on it is a moment.
    /// </summary>
    public Overview? GetOverview(int buckets)
    {
        var chunks = Present.OfType<FileLogSource>()
            .SelectMany(f => f.GetChunks())
            .Where(c => c.EntryCount > 0 && !c.HasUnknownTime)
            .ToList();
        if (chunks.Count == 0)
            return null;

        static long Milliseconds(long ticks) => (ticks - DateTimeOffset.UnixEpoch.UtcTicks) / TimeSpan.TicksPerMillisecond;
        var start = Milliseconds(chunks.Min(c => c.MinTicks));
        var end = Milliseconds(chunks.Max(c => c.MaxTicks)) + 1;
        var width = Math.Max(1.0, (double)(end - start) / buckets);
        var result = new OverviewBucket[buckets];
        foreach (var chunk in chunks)
        {
            var middle = Milliseconds(chunk.MinTicks + (chunk.MaxTicks - chunk.MinTicks) / 2);
            var bucket = Math.Min(buckets - 1, (int)((middle - start) / width));
            result[bucket] = result[bucket] with
            {
                Total = result[bucket].Total + chunk.EntryCount,
                Warnings = result[bucket].Warnings + chunk.Warnings,
                Errors = result[bucket].Errors + chunk.Errors,
            };
        }
        return new Overview(start, end, result);
    }

    /// <summary>
    /// The newest entries of <paramref name="member"/> up to <paramref name="to"/>. A read that stops early on the
    /// newer side of that time continues: where it stopped says nothing about the merged order, so it cannot be a
    /// cursor, and the next page would read the member from there without the time limit.
    /// </summary>
    private static ReadResult ReadBeforeTime(ILogSource member, int count, LogQueryMatcher matcher, DateTimeOffset to, CancellationToken cancellationToken)
    {
        var bounded = matcher.Within(null, to);
        long? position = null;
        while (true)
        {
            var read = Placed(member, member.ReadBefore(position, count, bounded, cancellationToken), t => t <= to.UtcTicks, cancellationToken);
            if (read.Entries.Count > 0 || !read.HasMore || read.Cursor is not { } cursor || cursor == position
                || !(TimeNear(member, cursor, cancellationToken) > to))
                return read;
            position = cursor;
        }
    }

    /// <summary>The oldest entries of <paramref name="member"/> from <paramref name="from"/>; see <see cref="ReadBeforeTime"/>.</summary>
    private static ReadResult ReadAfterTime(ILogSource member, int count, LogQueryMatcher matcher, DateTimeOffset from, CancellationToken cancellationToken)
    {
        var bounded = matcher.Within(from, null);
        long position = 0;
        while (true)
        {
            var read = Placed(member, member.ReadAfter(position, count, bounded, cancellationToken), t => t >= from.UtcTicks, cancellationToken);
            if (read.Entries.Count > 0 || !read.HasMore || read.Cursor is not { } cursor || cursor == position
                || !(TimeNear(member, cursor, cancellationToken) < from))
                return read;
            position = cursor;
        }
    }

    /// <summary>
    /// <paramref name="read"/> without the entries whose time, or for an entry without one the time before it, is not
    /// <paramref name="inRange"/>. The time limit of the read itself keeps entries without a time wherever they are.
    /// </summary>
    private static ReadResult Placed(ILogSource member, ReadResult read, Func<long, bool> inRange, CancellationToken cancellationToken)
    {
        if (read.Entries.All(e => e.Timestamp is not null))
            return read;
        var times = TimesOf(member, read.Entries, cancellationToken);
        var kept = read.Entries.Where((_, e) => times[e] is not { } t || inRange(t)).ToList();
        return read with { Entries = kept };
    }

    /// <summary>
    /// The times of <paramref name="entries"/> (in ascending order) for merging; an entry without one takes the time of
    /// the entry before it in its member.
    /// </summary>
    private static long?[] TimesOf(ILogSource member, IReadOnlyList<LogEntry> entries, CancellationToken cancellationToken)
    {
        var times = new long?[entries.Count];
        long? last = null;
        for (var e = 0; e < entries.Count; e++)
        {
            if (entries[e].Timestamp is null && last is null)
                last = TimeBefore(member, entries[e].Id, cancellationToken)?.UtcTicks;
            times[e] = last = entries[e].Timestamp?.UtcTicks ?? last;
        }
        return times;
    }

    private const int Look = 16;

    private static DateTimeOffset? TimeBefore(ILogSource source, long id, CancellationToken cancellationToken) =>
        source.ReadBefore(id, Look, LogQueryMatcher.All, cancellationToken).Entries.LastOrDefault(e => e.Timestamp is not null)?.Timestamp
        ?? source.ReadAfter(id, Look, LogQueryMatcher.All, cancellationToken).Entries.FirstOrDefault(e => e.Timestamp is not null)?.Timestamp;

    private ILogSource? MemberAt(int index) => index < Members.Count ? Members[index] : null;

    private void ForEachMember(Action<int, ILogSource> read, CancellationToken cancellationToken)
    {
        var present = Enumerable.Range(0, Members.Count).Where(i => Members[i] is not null).ToArray();
        if (present.Length == 1)
            read(present[0], Members[present[0]]!);
        else
            Parallel.ForEach(present, new ParallelOptions { CancellationToken = cancellationToken }, i => read(i, Members[i]!));
    }

    /// <summary>
    /// The entries the members returned, with entries without a time placed at the time of their neighbours, and the
    /// places where members stopped before reading <paramref name="count"/> entries.
    /// </summary>
    private (List<Item> Items, bool HasMore, List<Item> Stops) Collect(ReadResult?[] reads, int count, CancellationToken cancellationToken)
    {
        var items = new List<Item>();
        var stops = new List<Item>();
        var hasMore = false;
        for (var i = 0; i < reads.Length; i++)
        {
            if (reads[i] is not { } read)
                continue;

            var entries = read.Entries;
            var times = Members[i] is { } source ? TimesOf(source, entries, cancellationToken) : new long?[entries.Count];
            for (var e = 0; e < entries.Count; e++)
                items.Add(new Item(times[e] ?? 0, i, entries[e].Id, entries[e]));

            if (!read.HasMore)
                continue;
            hasMore = true;
            if (entries.Count < count && read.Cursor is { } cursor && Members[i] is { } member)
                stops.Add(new Item(TimeNear(member, cursor, cancellationToken)?.UtcTicks ?? 0, i, cursor, null));
        }
        return (items, hasMore, stops);
    }

    private static ReadResult Result(List<Item> items, bool hasMore, Item? stop) => new(
        items.Select(i => i.Entry! with { Id = Encode(i.Member, i.Id) }).ToList(),
        hasMore || stop is not null,
        stop is { } s ? Encode(s.Member, s.Id) : null);

    /// <summary>
    /// The time at <paramref name="id"/> in <paramref name="source"/>, as <see cref="TimesOf"/> places the entry with that
    /// id; for a position between entries, the time of the next entry with one.
    /// </summary>
    private static DateTimeOffset? TimeNear(ILogSource source, long id, CancellationToken cancellationToken)
    {
        if (source.Find(id) is { } entry)
            return entry.Timestamp ?? TimeBefore(source, id, cancellationToken);
        return source.ReadAfter(id, Look, LogQueryMatcher.All, cancellationToken).Entries.FirstOrDefault(e => e.Timestamp is not null)?.Timestamp
            ?? source.ReadBefore(id, Look, LogQueryMatcher.All, cancellationToken).Entries.LastOrDefault(e => e.Timestamp is not null)?.Timestamp;
    }

    /// <summary>An entry, or a place in a member, in the merged order.</summary>
    private readonly record struct Item(long Ticks, int Member, long Id, LogEntry? Entry) : IComparable<Item>
    {
        public int CompareTo(Item other)
        {
            var byTime = Ticks.CompareTo(other.Ticks);
            if (byTime != 0)
                return byTime;
            var byMember = Member.CompareTo(other.Member);
            return byMember != 0 ? byMember : Id.CompareTo(other.Id);
        }
    }
}
