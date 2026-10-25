using BenchmarkDotNet.Attributes;
using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Files;
using Kododo.MonitorWay.Querying;
using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.Benchmarks;

/// <summary>Indexing and reading a 64 MB file (above the parallel indexing threshold).</summary>
[MemoryDiagnoser]
public class FileBenchmarks
{
    private const long Size = 64L * 1024 * 1024;
    private const string Needle = "Needle42";

    private string _directory = null!;
    private string _clef = null!;
    private string _text = null!;
    private FileLogSource _indexed = null!;
    private LogQueryMatcher _needle = null!;
    private LogQueryMatcher _critical = null!;

    [GlobalSetup]
    public void Setup()
    {
        _directory = Path.Combine(Path.GetTempPath(), "monitorway-benchmarks", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_directory);
        _clef = Path.Combine(_directory, "app.clef");
        _text = Path.Combine(_directory, "app.log");
        SampleLogs.Write(_clef, Size, text: false);
        SampleLogs.Write(_text, Size, text: true);
        SampleLogs.PlantNearStart(_clef, Needle);

        _indexed = Open(_clef);
        _indexed.UpdateIndex(CancellationToken.None);
        _needle = LogQueryMatcher.Create(new LogQuery(Text: Needle));
        _critical = LogQueryMatcher.Create(new LogQuery(Levels: [LogLevel.Critical]));
    }

    [GlobalCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    private static FileLogSource Open(string path, FileSourceOptions? options = null)
    {
        var source = new FileLogSource(path, Path.GetFileName(path), options)
        {
            MaxScanTime = TimeSpan.FromMinutes(1),
        };
        return source;
    }

    [Benchmark(Description = "Index CLEF (64 MB)")]
    public long IndexClef()
    {
        var source = Open(_clef);
        source.UpdateIndex(CancellationToken.None);
        return source.Index.IndexedEnd;
    }

    [Benchmark(Description = "Index text template (64 MB)")]
    public long IndexText()
    {
        var source = Open(_text, new FileSourceOptions { Pattern = "*" }.UseTemplate(SampleLogs.TextTemplate));
        source.UpdateIndex(CancellationToken.None);
        return source.Index.IndexedEnd;
    }

    [Benchmark(Description = "Newest page of 300")]
    public int NewestPage() => _indexed.ReadBefore(null, 300, LogQueryMatcher.All, CancellationToken.None).Entries.Count;

    [Benchmark(Description = "Rare word near the start (whole file)")]
    public int SearchRareWord() => _indexed.ReadBefore(null, 10, _needle, CancellationToken.None).Entries.Count;

    [Benchmark(Description = "Critical only, 300 newest (chunk skipping)")]
    public int FilterCritical() => _indexed.ReadBefore(null, 300, _critical, CancellationToken.None).Entries.Count;
}
