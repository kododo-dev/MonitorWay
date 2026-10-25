using Kododo.MonitorWay.Files;
using Kododo.MonitorWay.Querying;
using Xunit;

namespace Kododo.MonitorWay.Tests.Files;

public class IndexReleaseTests
{
    private static FileSourceRegistry Registry(string root) =>
        new([new FileSourceOptions { Pattern = "*.clef" }], root, TimeProvider.System);

    private static void Open(FileLogSource source)
    {
        source.ReadBefore(null, 1, LogQueryMatcher.All, CancellationToken.None);
        source.UpdateIndex(CancellationToken.None);
    }

    [Fact]
    public void IdleIndex_IsReleasedAndRebuiltOnNextRead()
    {
        using var file = new ClefFile().AppendMany(5_000);
        var registry = Registry(file.Directory);
        var source = registry.Sources.Single();
        Open(source);
        var id = source.ReadBefore(null, 1, LogQueryMatcher.All, CancellationToken.None).Entries[0].Id;
        var version = source.Version;
        Assert.True(source.Index.ChunkCount > 0);

        registry.ReleaseIndexes(source.LastAccess + (long)registry.IdleTimeout.TotalMilliseconds + 1);

        Assert.False(source.IsOpened);
        Assert.Equal(0, source.Index.ChunkCount);
        // Ids are byte offsets, so they stay valid without the index.
        Assert.Equal(version, source.Version);
        Assert.NotNull(source.Find(id));

        Open(source);
        Assert.True(source.Index.ChunkCount > 0);
        Assert.Equal(5_000, source.GetStats().LevelCounts.Sum());
    }

    [Fact]
    public void RecentlyReadIndex_IsKept()
    {
        using var file = new ClefFile().AppendMany(1_000);
        var registry = Registry(file.Directory);
        var source = registry.Sources.Single();
        Open(source);

        registry.ReleaseIndexes(source.LastAccess + 1_000);

        Assert.True(source.IsOpened);
        Assert.True(source.Index.ChunkCount > 0);
    }

    [Fact]
    public void OverMemoryLimit_LeastRecentlyReadIsReleasedFirst()
    {
        using var file = new ClefFile("a.clef").AppendMany(5_000);
        File.Copy(file.Path, Path.Combine(file.Directory, "b.clef"));
        var registry = Registry(file.Directory);
        var older = registry.Sources.Single(s => s.Name == "a.clef");
        var newer = registry.Sources.Single(s => s.Name == "b.clef");
        Open(older);
        Thread.Sleep(20);
        Open(newer);
        registry.IndexMemoryLimit = newer.Index.MemoryBytes;

        registry.ReleaseIndexes(newer.LastAccess);

        Assert.False(older.IsOpened);
        Assert.True(newer.IsOpened);
    }
}
