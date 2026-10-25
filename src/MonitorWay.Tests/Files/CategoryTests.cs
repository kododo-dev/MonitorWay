using Kododo.MonitorWay.Files;
using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Files.Formats;
using Kododo.MonitorWay.Querying;
using Xunit;

namespace Kododo.MonitorWay.Tests.Files;

public class CategoryTests
{
    private static Dictionary<string, long> Counts(ClefFile file, FileSourceOptions? options = null)
    {
        var source = new FileLogSource(file.Path, "f", options);
        source.UpdateIndex(CancellationToken.None);
        return source.GetCategoryCounts();
    }

    [Fact]
    public void Clef_CountsSourceContext()
    {
        using var file = new ClefFile();
        file.Append(
            """{"@t":"2026-10-13T08:00:00Z","@m":"a","SourceContext":"Shop.Orders"}""",
            """{"@t":"2026-10-13T08:00:01Z","@m":"b","SourceContext":"Shop.Payments"}""",
            """{"@t":"2026-10-13T08:00:02Z","@m":"c","SourceContext":"Shop.Orders"}""",
            """{"@t":"2026-10-13T08:00:03Z","@m":"no category"}""",
            """{"@t":"2026-10-13T08:00:04Z","@m":"d","SourceContext":"Shop.\"Quoted\""}""");

        var counts = Counts(file);

        Assert.Equal(2, counts["Shop.Orders"]);
        Assert.Equal(1, counts["Shop.Payments"]);
        Assert.Equal(1, counts["Shop.\"Quoted\""]);
        Assert.Equal(3, counts.Count);
    }

    [Fact]
    public void Json_CountsCommonCategoryKeys()
    {
        using var file = new ClefFile("app.json");
        file.Append(
            """{"time":"2026-10-13T08:00:00Z","level":"INFO","logger":"Shop.Orders","message":"a"}""",
            """{"Timestamp":"2026-10-13T08:00:01Z","Level":"Information","RenderedMessage":"b","Properties":{"SourceContext":"Shop.Cache"}}""");

        var counts = Counts(file);

        Assert.Equal(1, counts["Shop.Orders"]);
        Assert.Equal(1, counts["Shop.Cache"]);
    }

    [Fact]
    public void Template_CountsSourceContextToken()
    {
        using var file = new ClefFile("app.log");
        file.AppendRaw(
            "2026-10-13 08:00:00.000 +02:00 [INF] Shop.Orders: a\n" +
            "2026-10-13 08:00:01.000 +02:00 [ERR] Shop.Payments: b\nSystem.Exception: x\n   at Y()\n" +
            "2026-10-13 08:00:02.000 +02:00 [INF] Shop.Orders: c\n");
        var options = new FileSourceOptions { Pattern = "*" }
            .UseTemplate("{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {SourceContext}: {Message:lj}{NewLine}{Exception}");

        var counts = Counts(file, options);

        Assert.Equal(2, counts["Shop.Orders"]);
        Assert.Equal(1, counts["Shop.Payments"]);
    }

    [Fact]
    public void FormatsWithoutCategories_HaveNone()
    {
        using var file = new ClefFile("app.log");
        file.AppendRaw("2026-10-13 08:00:00.000 +02:00 [INF] a\nplain line\n");

        Assert.Empty(Counts(file));
    }

    [Fact]
    public void Names_ReuseRecentStrings()
    {
        var names = new CategoryNames();

        var first = names.Get("Shop.Orders".AsSpan());
        var second = names.Get("Shop.Orders".AsSpan());

        Assert.Same(first, second);
    }

    [Fact]
    public void CategoryFilter_ReadsOnlyThoseCategories()
    {
        using var file = new ClefFile();
        file.Append(
            """{"@t":"2026-10-13T08:00:00Z","@m":"a","SourceContext":"Shop.Orders"}""",
            """{"@t":"2026-10-13T08:00:01Z","@m":"b","SourceContext":"Shop.OrdersCache"}""",
            """{"@t":"2026-10-13T08:00:02Z","@m":"c","SourceContext":"Shop.Payments"}""",
            """{"@t":"2026-10-13T08:00:03Z","@m":"d"}""");
        var source = new FileLogSource(file.Path, "f");
        source.UpdateIndex(CancellationToken.None);

        string[] Read(LogQuery query) => source
            .ReadBefore(null, 100, LogQueryMatcher.Create(query), CancellationToken.None)
            .Entries.Select(e => e.Message).ToArray();

        Assert.Equal(["a", "c"], Read(new LogQuery(Categories: ["Shop.Orders", "Shop.Payments"])));
        Assert.Equal(["b", "d"], Read(new LogQuery(ExcludedCategories: ["Shop.Orders", "Shop.Payments"])));
    }
}
