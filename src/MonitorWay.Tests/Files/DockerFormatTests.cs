using System.Text;
using System.Text.Json;
using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Files;
using Kododo.MonitorWay.Files.Formats;
using Kododo.MonitorWay.Querying;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kododo.MonitorWay.Tests.Files;

public class DockerFormatTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 13, 10, 0, 0, TimeSpan.Zero);

    /// <summary>Lines of a container's output as Docker's json-file driver writes them, one second apart.</summary>
    private static string Docker(params string[] output) => Docker(output.Select(o => (o + "\n", "stdout")).ToArray());

    private static string Docker(params (string Log, string Stream)[] output) => string.Concat(output.Select((o, i) =>
        JsonSerializer.Serialize(new { log = o.Log, stream = o.Stream, time = Start.AddSeconds(i).ToString("yyyy-MM-ddTHH:mm:ss.fffffff") + "99Z" }) + "\n"));

    private static (FileLogSource Source, ClefFile File) Open(string content, FileSourceOptions? options = null)
    {
        var file = new ClefFile("0123abcd-json.log");
        file.AppendRaw(content);
        var source = new FileLogSource(file.Path, "web-1", options);
        source.UpdateIndex(CancellationToken.None);
        return (source, file);
    }

    private static LogEntry[] Read(FileLogSource source, LogQuery? query = null) =>
        source.ReadAfter(0, 100, query is null ? LogQueryMatcher.All : LogQueryMatcher.Create(query), CancellationToken.None).Entries.ToArray();

    [Fact]
    public void ReadsTheConsoleOutputOfAnAspNetCoreContainer()
    {
        var (source, file) = Open(Docker(
            "info: Microsoft.Hosting.Lifetime[14]",
            "      Now listening on: http://[::]:8080",
            "fail: Shop.Payments.PaymentGateway[0]",
            "      Payment for order 1008 failed",
            "      System.TimeoutException: Stripe did not answer",
            "         at Shop.Payments.Charge()"));
        using var _ = file;

        var entries = Read(source);

        Assert.Equal("docker · console", source.Format?.Name);
        Assert.Equal([LogLevel.Information, LogLevel.Error], entries.Select(e => e.Level));
        Assert.Equal("Now listening on: http://[::]:8080", entries[0].Message);
        Assert.Equal("Shop.Payments.PaymentGateway", entries[1].Category);
        Assert.Equal("Payment for order 1008 failed", entries[1].Message);
        Assert.Equal("System.TimeoutException: Stripe did not answer\n   at Shop.Payments.Charge()", entries[1].Exception);
        Assert.Contains(new("Stream", "stdout"), entries[1].Properties);
    }

    [Fact]
    public void UsesDockersTimeWhenTheApplicationWritesNone()
    {
        var (source, file) = Open(Docker("info: A[0]", "      first", "warn: B[0]", "      second"));
        using var _ = file;

        var entries = Read(source);

        // Nanoseconds are cut to the 100 ns .NET keeps.
        Assert.Equal(Start, entries[0].Timestamp);
        Assert.Equal(Start.AddSeconds(2), entries[1].Timestamp);
        Assert.Equal(Start.AddSeconds(2), DockerFormat.ParseTime("2026-10-13T10:00:02.000000099Z"u8));
    }

    [Fact]
    public void PrefersTheApplicationsOwnTime()
    {
        var clef = """{"@t":"2026-10-12T08:30:00.0000000Z","@mt":"Order {Id} placed","@l":"Warning","Id":7}""";
        var (source, file) = Open(Docker(clef));
        using var _ = file;

        var entry = Assert.Single(Read(source));

        Assert.Equal("docker · clef", source.Format?.Name);
        Assert.Equal("Order 7 placed", entry.Message);
        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal(new DateTimeOffset(2026, 10, 12, 8, 30, 0, TimeSpan.Zero), entry.Timestamp);
    }

    [Fact]
    public void UnescapesTheOutput()
    {
        var text = "2026-10-13 10:00:00.000 +00:00 [ERR] Can't parse \"<order>\"\tfor Zoë & co";
        var (source, file) = Open(Docker(text));
        using var _ = file;

        var entry = Assert.Single(Read(source));

        Assert.Equal("docker · serilog", source.Format?.Name);
        Assert.Equal("Can't parse \"<order>\"\tfor Zoë & co", entry.Message);
    }

    [Fact]
    public void ReadsLinesLongerThanTheStackBuffer()
    {
        var message = string.Concat(Enumerable.Repeat("long message ", 1000)).Trim();
        var (source, file) = Open(Docker("info: A[0]", "      " + message));
        using var _ = file;

        Assert.Equal(message, Assert.Single(Read(source)).Message);
    }

    [Fact]
    public void JoinsLinesDockerSplit()
    {
        // Docker splits output lines longer than 16 KB; only the last part ends with a line break.
        var (source, file) = Open(Docker(("info: A[0]\n", "stdout"), ("      first half, ", "stdout"), ("second half\n", "stdout")));
        using var _ = file;

        Assert.Equal("first half, second half", Assert.Single(Read(source)).Message);
    }

    [Fact]
    public void FiltersByTextAndStream()
    {
        var (source, file) = Open(Docker(
            ("info: Shop.Orders[0]\n", "stdout"), ("      Order placed\n", "stdout"),
            ("fail: Shop.Payments[0]\n", "stderr"), ("      Payment failed\n", "stderr")));
        using var _ = file;

        Assert.Equal(["Payment failed"], Read(source, new LogQuery(Text: "payment")).Select(e => e.Message));
        Assert.Equal(["Payment failed"], Read(source, new LogQuery(Text: "Stream:stderr")).Select(e => e.Message));
        Assert.Equal(["Order placed"], Read(source, new LogQuery(Levels: [LogLevel.Information])).Select(e => e.Message));
    }

    [Fact]
    public void ReadsAConfiguredTemplateInside()
    {
        var (source, file) = Open(
            Docker("10:00:01 ERR | Payment failed"),
            new FileSourceOptions { Pattern = "*-json.log", Format = LogFileFormat.Docker, Template = "{Timestamp:HH:mm:ss} {Level:u3} | {Message:lj}{NewLine}{Exception}" });
        using var _ = file;

        var entry = Assert.Single(Read(source));

        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("Payment failed", entry.Message);
    }

    [Fact]
    public void ShowsALineThatIsNotDockersAsText()
    {
        var (source, file) = Open(Docker("Started", "Ready") + "not json\n");
        using var _ = file;

        var entries = Read(source);

        Assert.Equal(["Started", "Ready", "not json"], entries.Select(e => e.Message));
    }

    [Fact]
    public void IsNotMistakenForJsonLines()
    {
        var sample = Encoding.UTF8.GetBytes(Docker("plain text output"));

        var format = FormatDetector.Detect(sample, new FileSourceOptions { Pattern = "*" }, out var error);

        Assert.Null(error);
        Assert.IsType<DockerFormat>(format);
        Assert.Equal("text", ((DockerFormat)format).Inner.Name);
    }
}
