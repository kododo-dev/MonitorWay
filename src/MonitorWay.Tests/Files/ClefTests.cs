using System.Text;
using System.Text.Json;
using Kododo.MonitorWay.Files;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kododo.MonitorWay.Tests.Files;

public class ClefTests
{
    private static Core.Model.LogEntry Parse(string line) => Clef.Parse(Encoding.UTF8.GetBytes(line), 1);

    [Fact]
    public void Parse_ReadsReservedAndApplicationProperties()
    {
        var entry = Parse("""
            {"@t":"2026-10-13T08:00:01.5+02:00","@mt":"Order {OrderId} failed","@l":"Error","@x":"System.Exception: boom","@i":"a1b2","@tr":"abc","@sp":"def","OrderId":42,"SourceContext":"Shop.Orders","@@odd":"x"}
            """);

        Assert.Equal(new DateTimeOffset(2026, 10, 13, 8, 0, 1, 500, TimeSpan.FromHours(2)), entry.Timestamp);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("Order 42 failed", entry.Message);
        Assert.Equal("Order {OrderId} failed", entry.MessageTemplate);
        Assert.Equal("System.Exception: boom", entry.Exception);
        Assert.Equal("Shop.Orders", entry.Category);
        Assert.Equal("abc", entry.TraceId);
        Assert.Equal("def", entry.SpanId);
        Assert.Contains(new("OrderId", "42"), entry.Properties);
        Assert.Contains(new("EventId", "a1b2"), entry.Properties);
        Assert.Contains(new("@odd", "x"), entry.Properties);
        Assert.DoesNotContain(entry.Properties, p => p.Key == "SourceContext");
    }

    [Fact]
    public void Parse_MissingLevelMeansInformation()
    {
        Assert.Equal(LogLevel.Information, Parse("""{"@t":"2026-10-13T08:00:00Z","@m":"hi"}""").Level);
    }

    [Theory]
    [InlineData("Verbose", LogLevel.Trace)]
    [InlineData("Debug", LogLevel.Debug)]
    [InlineData("Warning", LogLevel.Warning)]
    [InlineData("Fatal", LogLevel.Critical)]
    [InlineData("Critical", LogLevel.Critical)]
    [InlineData("whatever", LogLevel.None)]
    public void Parse_MapsLevels(string level, LogLevel expected)
    {
        Assert.Equal(expected, Parse($$"""{"@t":"2026-10-13T08:00:00Z","@l":"{{level}}","@m":"hi"}""").Level);
    }

    [Fact]
    public void Parse_PrefersRenderedMessage()
    {
        Assert.Equal("rendered", Parse("""{"@t":"2026-10-13T08:00:00Z","@m":"rendered","@mt":"template {A}","A":1}""").Message);
    }

    [Fact]
    public void Parse_InvalidJsonKeepsRawText()
    {
        var entry = Parse("not json at all");

        Assert.Equal(LogLevel.None, entry.Level);
        Assert.Equal("not json at all", entry.Message);
    }

    [Fact]
    public void Render_QuotesStringsLikeSerilog()
    {
        var values = JsonDocument.Parse("""{"Name":"Ann","Count":3,"Items":[1,2]}""").RootElement
            .EnumerateObject().ToDictionary(p => p.Name, p => p.Value);

        Assert.Equal("\"Ann\" has 3 items [1,2]", Clef.Render("{Name} has {Count} items {@Items}", values));
        Assert.Equal("Ann {Missing} {literal}", Clef.Render("{Name:l} {Missing} {{literal}}", values));
    }

    [Fact]
    public void ReadHeader_FindsLevelAfterOtherReservedProperties()
    {
        Clef.ReadHeader("""{"@t":"2026-10-13T08:00:00Z","@mt":"x","@r":["a"],"@l":"Warning","@x":"long","A":1}"""u8,
            out var level, out var timestamp);

        Assert.Equal(LogLevel.Warning, level);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 8, 0, 0, TimeSpan.Zero), timestamp);
    }

    [Fact]
    public void ReadHeader_StopsAtFirstApplicationProperty()
    {
        Clef.ReadHeader("""{"@t":"2026-10-13T08:00:00Z","@mt":"x","A":1,"@l":"Error"}"""u8, out var level, out _);

        Assert.Equal(LogLevel.Information, level);
    }

    [Fact]
    public void ReadHeader_InvalidJsonHasUnknownLevel()
    {
        Clef.ReadHeader("{ broken"u8, out var level, out _);

        Assert.Equal(LogLevel.None, level);
    }
}
