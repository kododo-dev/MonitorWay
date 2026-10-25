using Kododo.MonitorWay.Files;
using Xunit;

namespace Kododo.MonitorWay.Tests.Files;

public class FileSourceRegistryTests
{
    private static FileSourceRegistry Registry(string root, params string[] patterns) =>
        new(patterns.Select(p => new FileSourceOptions { Pattern = p }).ToArray(), root, TimeProvider.System);

    [Fact]
    public void RelativePattern_IsResolvedAgainstContentRoot()
    {
        using var file = new ClefFile();
        Directory.CreateDirectory(Path.Combine(file.Directory, "logs"));
        File.WriteAllText(Path.Combine(file.Directory, "logs", "a.clef"), "");
        File.WriteAllText(Path.Combine(file.Directory, "logs", "b.txt"), "");

        var sources = Registry(file.Directory, "logs/*.clef").Sources;

        Assert.Equal(["logs/a.clef"], sources.Select(s => s.Name));
    }

    [Fact]
    public void AbsolutePattern_AndRecursiveGlob()
    {
        using var file = new ClefFile();
        Directory.CreateDirectory(Path.Combine(file.Directory, "x", "y"));
        File.WriteAllText(Path.Combine(file.Directory, "x", "y", "deep.clef"), "");

        var sources = Registry(Path.GetTempPath(), Path.Combine(file.Directory, "**", "*.clef")).Sources;

        Assert.Contains(sources, s => s.Path.EndsWith("deep.clef", StringComparison.Ordinal));
        Assert.Contains(sources, s => s.Path == file.Path);
    }

    [Fact]
    public void FileOutsideContentRoot_IsNamedRelativeToThePatternAndGroupedByIt()
    {
        using var file = new ClefFile();
        Directory.CreateDirectory(Path.Combine(file.Directory, "api", "old"));
        File.WriteAllText(Path.Combine(file.Directory, "api", "old", "app.clef"), "");
        var contentRoot = Path.Combine(file.Directory, "app");

        var source = Assert.Single(Registry(contentRoot, Path.Combine(file.Directory, "api", "**", "*.clef")).Sources);

        Assert.Equal("old/app.clef", source.Name);
        Assert.Equal(Path.Combine(file.Directory, "api").Replace('\\', '/'), source.Group);
    }

    [Fact]
    public void Describe_NamesGroupsAndLeavesOutFiles()
    {
        using var file = new ClefFile();
        File.WriteAllText(Path.Combine(file.Directory, "skip.clef"), "");
        var calls = 0;
        var options = new FileSourceOptions
        {
            Pattern = "*.clef",
            Describe = path =>
            {
                calls++;
                return Path.GetFileName(path) == "skip.clef" ? null : new FileDescription("web-1") { Group = "shop / web" };
            },
        };
        var registry = new FileSourceRegistry([options], file.Directory, TimeProvider.System);

        var source = Assert.Single(registry.Sources);
        registry.Rescan();

        Assert.Equal("web-1", source.Name);
        Assert.Equal("shop / web", source.Group);
        Assert.Single(registry.Sources);
        // Each file is described once, also the one left out.
        Assert.Equal(2, calls);
    }

    [Fact]
    public void Rescan_PicksUpNewFilesAndKeepsExistingSources()
    {
        using var file = new ClefFile();
        var registry = Registry(file.Directory, "*.clef");
        var first = Assert.Single(registry.Sources);

        File.WriteAllText(Path.Combine(file.Directory, "rotated.clef"), "");
        registry.Rescan();

        Assert.Equal(2, registry.Sources.Count);
        Assert.Same(first, registry.Find(first.Id));
    }

    [Fact]
    public void MissingDirectory_HasNoSources()
    {
        Assert.Empty(Registry(Path.GetTempPath(), "does-not-exist-" + Guid.NewGuid() + "/*.clef").Sources);
    }

    [Fact]
    public void Ids_AreStableAcrossRegistries()
    {
        using var file = new ClefFile();

        Assert.Equal(
            Registry(file.Directory, "*.clef").Sources.Single().Id,
            Registry(file.Directory, "*.clef").Sources.Single().Id);
    }
}
