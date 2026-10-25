using System.Security.Cryptography;
using System.Text;
using Kododo.MonitorWay.Sources;
using Microsoft.Extensions.FileSystemGlobbing;
using Microsoft.Extensions.FileSystemGlobbing.Abstractions;
using Microsoft.Extensions.Hosting;

namespace Kododo.MonitorWay.Files;

/// <summary>
/// The files matched by the configured patterns. Rescans for new and removed files (rotation) and keeps
/// the indexes of files that were opened up to date as they grow.
/// </summary>
internal sealed class FileSourceRegistry(IReadOnlyList<FileSourceOptions> patterns, string contentRoot, TimeProvider time)
    : BackgroundService
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan RescanInterval = TimeSpan.FromSeconds(5);

    private readonly object _lock = new();
    private FileLogSource[] _sources = [];
    private DateTimeOffset _lastScan = DateTimeOffset.MinValue;

    /// <summary>Matched files, in the order of the patterns and, within a pattern, newest first.</summary>
    public IReadOnlyList<FileLogSource> Sources
    {
        get
        {
            if (_lastScan == DateTimeOffset.MinValue)
                Rescan();
            return Volatile.Read(ref _sources);
        }
    }

    public FileLogSource? Find(string id) => Sources.FirstOrDefault(s => s.Id == id);

    /// <summary>
    /// The sources as listed: the files, except that files of a pattern with <see cref="FileSourceOptions.Merge"/>
    /// are listed once per description, as a <see cref="MergedSource"/>.
    /// </summary>
    public IReadOnlyList<ILogSource> Views
    {
        get
        {
            if (_lastScan == DateTimeOffset.MinValue)
                Rescan();
            return Volatile.Read(ref _views);
        }
    }

    public ILogSource? FindView(string id) => Views.FirstOrDefault(s => s.Id == id);

    private ILogSource[] _views = [];

    /// <summary>
    /// The files of each merged source by their index in it. A file that disappears keeps its slot, so the ids issued
    /// for the others stay valid, and gets it back if it returns.
    /// </summary>
    private readonly Dictionary<(string? Group, string Name), List<string>> _slots = new();

    public void Rescan()
    {
        var found = new List<(string Path, FileDescription Description, FileSourceOptions Options)>();
        foreach (var pattern in patterns)
            found.AddRange(Match(pattern.Pattern).Select(f => (f.Path, f.Description, pattern)));

        lock (_lock)
        {
            var existing = _sources.ToDictionary(s => s.Path, StringComparer.Ordinal);
            var sources = new List<(FileLogSource Source, FileSourceOptions Options)>();
            foreach (var (path, defaults, options) in found.DistinctBy(f => f.Path))
            {
                if (existing.TryGetValue(path, out var source))
                    sources.Add((source, options));
                else if (Describe(path, defaults, options) is { } description)
                    sources.Add((new FileLogSource(path, description.Name, options, description.Group), options));
            }
            _sources = sources.Select(s => s.Source).ToArray();
            _views = BuildViews(sources);
            // A file left out stays out; one that disappeared is described again if it comes back.
            _skipped.IntersectWith(found.Select(f => f.Path));
            _lastScan = time.GetUtcNow();
        }
    }

    private readonly HashSet<string> _skipped = new(StringComparer.Ordinal);

    private ILogSource[] BuildViews(List<(FileLogSource Source, FileSourceOptions Options)> sources)
    {
        var byPath = sources.ToDictionary(s => s.Source.Path, s => s.Source, StringComparer.Ordinal);
        var views = new List<ILogSource>();
        var merged = new HashSet<(string?, string)>();
        foreach (var (source, options) in sources)
        {
            if (!options.Merge)
            {
                views.Add(source);
                continue;
            }

            var key = (source.Group, source.Name);
            if (!merged.Add(key))
                continue;
            if (!_slots.TryGetValue(key, out var slots))
                _slots[key] = slots = [];
            foreach (var path in sources.Where(s => s.Options.Merge && (s.Source.Group, s.Source.Name) == key).Select(s => s.Source.Path))
            {
                if (!slots.Contains(path) && slots.Count < MergedSource.MaxMembers)
                    slots.Add(path);
            }
            views.Add(new MergedSource(MergedId(key), source.Name, source.Group,
                slots.Select(path => byPath.GetValueOrDefault(path) as ILogSource).ToArray()));
        }
        // Files are listed newest first, which moves merged sources around as they are written to; they keep
        // their places by group and name instead.
        var slotsOfMerged = Enumerable.Range(0, views.Count).Where(i => views[i] is MergedSource).ToList();
        var sorted = slotsOfMerged.Select(i => views[i])
            .OrderBy(v => v.Group, StringComparer.OrdinalIgnoreCase).ThenBy(v => v.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        for (var i = 0; i < slotsOfMerged.Count; i++)
            views[slotsOfMerged[i]] = sorted[i];
        return [.. views];
    }

    private static string MergedId((string? Group, string Name) key) =>
        "merged-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{key.Group}\n{key.Name}")))[..12].ToLowerInvariant();

    /// <summary>The host's description of a newly found file, or the default one; <c>null</c> when the file is left out.</summary>
    private FileDescription? Describe(string path, FileDescription defaults, FileSourceOptions options)
    {
        if (options.Describe is null)
            return defaults;
        if (_skipped.Contains(path))
            return null;
        var description = options.Describe(path);
        if (description is null)
            _skipped.Add(path);
        return description;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(RefreshInterval, time);
        do
        {
            if (time.GetUtcNow() - _lastScan >= RescanInterval)
                Rescan();
            ReleaseIndexes(Environment.TickCount64);
            foreach (var source in Volatile.Read(ref _sources))
                source.Refresh();
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    /// <summary>A file not read for this long has its index released.</summary>
    internal TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Memory all file indexes may hold together; above it the least recently read ones are released.</summary>
    internal long IndexMemoryLimit { get; set; } = 256L * 1024 * 1024;

    /// <param name="now">Current <see cref="Environment.TickCount64"/>; a parameter so tests can move time.</param>
    internal void ReleaseIndexes(long now)
    {
        var opened = Volatile.Read(ref _sources).Where(s => s.IsOpened).OrderBy(s => s.LastAccess).ToList();
        var total = opened.Sum(s => s.Index.MemoryBytes);
        foreach (var source in opened)
        {
            if (now - source.LastAccess <= IdleTimeout.TotalMilliseconds && total <= IndexMemoryLimit)
                continue;
            total -= source.Index.MemoryBytes;
            source.Release();
        }
    }

    private IEnumerable<(string Path, FileDescription Description)> Match(string pattern)
    {
        var (root, relative) = Split(pattern);
        if (!Directory.Exists(root))
            return [];

        var matcher = new Matcher(OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
        matcher.AddInclude(relative);
        return matcher.Execute(new DirectoryInfoWrapper(new DirectoryInfo(root))).Files
            .Select(f => System.IO.Path.GetFullPath(System.IO.Path.Combine(root, f.Path)))
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .Select(path => (path, DefaultDescription(path, root)));
    }

    /// <summary>
    /// The path relative to the content root, or, for a file outside it, relative to the directory its pattern starts
    /// in, with that directory as the group: <c>/var/log/api/app.log</c> from <c>/var/log/api/*.log</c> is
    /// <c>app.log</c> under <c>/var/log/api</c>.
    /// </summary>
    private FileDescription DefaultDescription(string path, string root)
    {
        var relative = System.IO.Path.GetRelativePath(contentRoot, path);
        if (!relative.StartsWith("..", StringComparison.Ordinal) && !System.IO.Path.IsPathRooted(relative))
            return new FileDescription(relative.Replace('\\', '/'));
        return new FileDescription(System.IO.Path.GetRelativePath(root, path).Replace('\\', '/'))
        {
            Group = root.Replace('\\', '/'),
        };
    }

    /// <summary>Splits a pattern into the directory before the first wildcard and the glob relative to it.</summary>
    private (string Root, string Relative) Split(string pattern)
    {
        var full = pattern.Replace('\\', '/');
        var wildcard = full.IndexOfAny(['*', '?', '[', '{']);
        var cut = full.LastIndexOf('/', wildcard < 0 ? full.Length - 1 : wildcard);
        var root = cut < 0 ? "" : full[..cut];
        var relative = full[(cut + 1)..];
        if (!System.IO.Path.IsPathRooted(root))
            root = System.IO.Path.Combine(contentRoot, root);
        return (System.IO.Path.GetFullPath(root.Length == 0 ? contentRoot : root), relative);
    }
}
