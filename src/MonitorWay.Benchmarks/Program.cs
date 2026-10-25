using System.Diagnostics;
using BenchmarkDotNet.Running;
using Kododo.MonitorWay.Benchmarks;
using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Files;
using Kododo.MonitorWay.Querying;
using Microsoft.Extensions.Logging;

// dotnet run -c Release -- --filter * --inProcess         BenchmarkDotNet suites (in process: the assembly name
//                                                          has the Kododo. prefix, so BenchmarkDotNet cannot find
//                                                          the project to build a separate one)
// dotnet run -c Release -- bigfile <path> <gigabytes>     one pass over a generated file of any size (kept for reruns)
if (args is ["bigfile", var path, var gigabytes, ..])
{
    BigFile(path, double.Parse(gigabytes, System.Globalization.CultureInfo.InvariantCulture));
    return;
}
BenchmarkSwitcher.FromAssembly(typeof(FileBenchmarks).Assembly).Run(args);

static void BigFile(string path, double gigabytes)
{
    const string needle = "Needle42";
    if (!File.Exists(path))
    {
        Measure($"generate {gigabytes} GB", () =>
        {
            SampleLogs.Write(path, (long)(gigabytes * 1024 * 1024 * 1024), text: false);
            SampleLogs.PlantNearStart(path, needle);
            return "";
        });
    }

    var source = new FileLogSource(path, Path.GetFileName(path)) { MaxScanTime = TimeSpan.FromHours(1) };
    Measure("newest page of 300 (no index, first call)", () => $"{source.ReadBefore(null, 300, LogQueryMatcher.All, CancellationToken.None).Entries.Count} entries");
    Measure("newest page of 300 (no index, again)", () => $"{source.ReadBefore(null, 300, LogQueryMatcher.All, CancellationToken.None).Entries.Count} entries");
    Measure("index", () =>
    {
        source.UpdateIndex(CancellationToken.None);
        var counts = source.GetStats().LevelCounts;
        return $"{counts.Sum():N0} entries, {source.Index.ChunkCount:N0} chunks, {source.Index.MemoryBytes / 1024 / 1024} MB";
    });
    Measure("newest page of 300 (indexed)", () => $"{source.ReadBefore(null, 300, LogQueryMatcher.All, CancellationToken.None).Entries.Count} entries");
    Measure("critical only, 300 newest", () =>
        $"{source.ReadBefore(null, 300, LogQueryMatcher.Create(new LogQuery(Levels: [LogLevel.Critical])), CancellationToken.None).Entries.Count} entries");
    // A read stops after its budget and returns a cursor; the UI continues from it until it finds enough, so do the same.
    Measure($"rare word near the start ('{needle}')", () =>
    {
        var matcher = LogQueryMatcher.Create(new LogQuery(Text: needle));
        long? before = null;
        var calls = 0;
        while (true)
        {
            var page = source.ReadBefore(before, 10, matcher, CancellationToken.None);
            calls++;
            if (page.Entries.Count > 0 || !page.HasMore)
                return $"{page.Entries.Count} found in {calls} calls";
            before = page.Cursor;
        }
    });
    Measure("common word, 300 newest ('webcam')", () =>
        $"{source.ReadBefore(null, 300, LogQueryMatcher.Create(new LogQuery(Text: "webcam")), CancellationToken.None).Entries.Count} found");
}

static void Measure(string name, Func<string> action)
{
    var watch = Stopwatch.StartNew();
    var result = action();
    Console.WriteLine($"{name,-40} {watch.Elapsed.TotalSeconds,8:F2} s   {result}");
}
