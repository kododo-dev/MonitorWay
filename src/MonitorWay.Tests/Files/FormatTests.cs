using System.Text;
using Kododo.MonitorWay.Files;
using Kododo.MonitorWay.Files.Formats;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kododo.MonitorWay.Tests.Files;

public class FormatTests
{
    private const string SerilogDefault = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    private static byte[] Bytes(string text) => Encoding.UTF8.GetBytes(text);

    private static Core.Model.LogEntry Parse(ILogFormat format, string entry) => format.Parse(Bytes(entry), 1);

    [Fact]
    public void Template_SerilogDefault_ParsesFirstLineAndException()
    {
        var format = new TemplateFormat(SerilogDefault);
        var entry = Parse(format, """
            2026-10-13 09:28:10.512 +02:00 [ERR] Payment for order 1008 failed
            System.TimeoutException: Stripe did not answer
               at Shop.Payments.Charge()
               at Shop.Orders.Place()

            """.Replace("\r\n", "\n"));

        Assert.Equal(new DateTimeOffset(2026, 10, 13, 9, 28, 10, 512, TimeSpan.FromHours(2)), entry.Timestamp);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("Payment for order 1008 failed", entry.Message);
        Assert.StartsWith("System.TimeoutException: Stripe did not answer\n   at Shop.Payments.Charge()", entry.Exception);
    }

    [Fact]
    public void Template_MultiLineMessage_StaysInMessageUntilException()
    {
        var format = new TemplateFormat(SerilogDefault);
        var entry = Parse(format, "2026-10-13 09:28:10.512 +02:00 [WRN] First line\nsecond line\nthird line\nSystem.Exception: boom\n   at X.Y()\n");

        Assert.Equal("First line\nsecond line\nthird line", entry.Message);
        Assert.Equal("System.Exception: boom\n   at X.Y()", entry.Exception);
    }

    [Fact]
    public void Template_ExceptionWithHResult_IsTheException()
    {
        var format = new TemplateFormat(SerilogDefault);
        var entry = Parse(format, "2026-10-13 09:28:10.512 +02:00 [ERR] Query failed\nNpgsql.NpgsqlException (0x80004005): Exception while reading from stream\n   at X.Y()\n");

        Assert.Equal("Query failed", entry.Message);
        Assert.Equal("Npgsql.NpgsqlException (0x80004005): Exception while reading from stream\n   at X.Y()", entry.Exception);
    }

    [Fact]
    public void Template_ContinuationLinesAreNotEntryStarts()
    {
        var format = new TemplateFormat(SerilogDefault);

        Assert.True(format.IsEntryStart(Bytes("2026-10-13 09:28:10.512 +02:00 [INF] Hello")));
        Assert.False(format.IsEntryStart(Bytes("   at Shop.Orders.Place()")));
        Assert.False(format.IsEntryStart(Bytes("System.Exception: boom")));
        Assert.False(format.IsEntryStart(Bytes("")));
    }

    [Fact]
    public void Template_WithSourceContextAndProperties()
    {
        var format = new TemplateFormat("[{Timestamp:HH:mm:ss} {Level:u3}] {SourceContext}: {Message:lj} {Properties:j}{NewLine}{Exception}");
        var entry = Parse(format, "[09:28:10 INF] Shop.Orders.OrderService: Order 5 placed {\"OrderId\":5,\"Customer\":\"c-1\"}\n");

        Assert.Equal("Shop.Orders.OrderService", entry.Category);
        Assert.Equal("Order 5 placed", entry.Message);
        Assert.Contains(new("OrderId", "5"), entry.Properties);
        Assert.Contains(new("Customer", "c-1"), entry.Properties);
        Assert.Equal(new TimeSpan(9, 28, 10), entry.Timestamp!.Value.TimeOfDay);
    }

    [Fact]
    public void Template_UnknownTokensBecomeProperties()
    {
        var format = new TemplateFormat("{Timestamp:yyyy-MM-dd HH:mm:ss} {Level:u3} [{ThreadId}] {Message}");
        var entry = Parse(format, "2026-10-13 09:28:10 DBG [17] Cache warmed\n");

        Assert.Equal(LogLevel.Debug, entry.Level);
        Assert.Contains(new("ThreadId", "17"), entry.Properties);
    }

    [Fact]
    public void Template_NLogDefault()
    {
        var format = new TemplateFormat(FormatDetector.Presets.Single(p => p.Name == "nlog").Template);
        var entry = Parse(format, "2026-10-13 09:28:10.5123|WARN|Shop.Payments|Slow answer\n");

        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Shop.Payments", entry.Category);
        Assert.Equal("Slow answer", entry.Message);
        Assert.Equal(512, entry.Timestamp!.Value.Millisecond);
    }

    [Fact]
    public void Template_Log4NetDefaultWithPaddedLevel()
    {
        var format = new TemplateFormat(FormatDetector.Presets.Single(p => p.Name == "log4net").Template);
        var info = Parse(format, "2026-10-13 09:28:10,512 [12] INFO  Shop.Orders - Order placed\n");
        var error = Parse(format, "2026-10-13 09:28:11,001 [7] ERROR Shop.Orders - Failed\n");

        Assert.Equal(LogLevel.Information, info.Level);
        Assert.Equal("Shop.Orders", info.Category);
        Assert.Equal("Order placed", info.Message);
        Assert.Contains(new("ThreadId", "12"), info.Properties);
        Assert.Equal(LogLevel.Error, error.Level);
    }

    [Theory]
    [InlineData("yyyy-MM-dd HH:mm:ss.fff zzz", "2026-10-13 09:28:10.512 +02:00")]
    [InlineData("dd/MM/yyyy HH:mm:ss", "13/10/2026 09:28:10")]
    [InlineData("yyyy-MM-ddTHH:mm:ss.FFFFFFFK", "2026-10-13T09:28:10.5Z")]
    [InlineData("HH:mm:ss 'UTC'", "09:28:10 UTC")]
    [InlineData("MMM d, yyyy h:mm tt", "Oct 3, 2026 9:28 AM")]
    public void TimestampPattern_MatchesFormattedDates(string format, string value)
    {
        var pattern = TemplateFormat.TimestampPattern(format, out _);

        Assert.Matches($"^{pattern}$", value);
    }

    [Fact]
    public void Json_SerilogJsonFormatter()
    {
        var entry = Parse(JsonLinesFormat.Instance,
            """{"Timestamp":"2026-10-13T09:28:10.512+02:00","Level":"Warning","MessageTemplate":"Order {OrderId} slow","RenderedMessage":"Order 5 slow","Properties":{"OrderId":5,"SourceContext":"Shop.Orders"}}""");

        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Order 5 slow", entry.Message);
        Assert.Equal("Order {OrderId} slow", entry.MessageTemplate);
        Assert.Equal("Shop.Orders", entry.Category);
        Assert.Contains(new("OrderId", "5"), entry.Properties);
    }

    [Fact]
    public void Json_NLogStyleWithEpochTime()
    {
        var entry = Parse(JsonLinesFormat.Instance,
            """{"ts":1791883690.5,"level":"ERROR","logger":"Shop.Orders","message":"Failed","exception":"System.Exception: x","user":"ann"}""");

        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Equal("Shop.Orders", entry.Category);
        Assert.Equal("System.Exception: x", entry.Exception);
        Assert.Equal(DateTimeOffset.FromUnixTimeMilliseconds(1791883690500), entry.Timestamp);
        Assert.Contains(new("user", "ann"), entry.Properties);
    }

    [Fact]
    public void Json_ReadHeaderFindsLevelAnywhere()
    {
        JsonLinesFormat.Instance.ReadHeader(Bytes("""{"message":"x","nested":{"level":"DEBUG"},"level":"ERROR","time":"2026-10-13T09:28:10Z"}"""),
            out var level, out var timestamp);

        Assert.Equal(LogLevel.Error, level);
        Assert.Equal(new DateTimeOffset(2026, 10, 13, 9, 28, 10, TimeSpan.Zero), timestamp);
    }

    [Theory]
    [InlineData("2026-10-13 09:28:10 ERROR something broke", LogLevel.Error)]
    [InlineData("[2026-10-13T09:28:10Z] [Warning] disk almost full", LogLevel.Warning)]
    [InlineData("fail: Microsoft.AspNetCore.Server[1]", LogLevel.Error)]
    [InlineData("12:00 | Debug | cache", LogLevel.Debug)]
    [InlineData("No error was found in the configuration", LogLevel.Information)]
    public void Plain_GuessesLevel(string line, LogLevel expected)
    {
        PlainTextFormat.Instance.ReadHeader(Bytes(line), out var level, out _);

        Assert.Equal(expected, level);
    }

    [Fact]
    public void Plain_IndentedLinesContinueTheEntry()
    {
        Assert.True(PlainTextFormat.Instance.IsEntryStart(Bytes("Starting server")));
        Assert.False(PlainTextFormat.Instance.IsEntryStart(Bytes("    at Server.Run()")));
        Assert.False(PlainTextFormat.Instance.IsEntryStart(Bytes("\tdetails")));

        var entry = Parse(PlainTextFormat.Instance, "2026-10-13 09:28:10 ERROR crash\n   at A()\n   at B()\n");
        Assert.Equal("2026-10-13 09:28:10 ERROR crash", entry.Message);
        Assert.Equal("   at A()\n   at B()", entry.Exception);
        Assert.Equal(new DateTime(2026, 10, 13, 9, 28, 10), entry.Timestamp!.Value.DateTime);
    }

    private static string? Detect(string sample, FileSourceOptions? options = null) =>
        FormatDetector.Detect(Bytes(sample), options ?? new FileSourceOptions { Pattern = "*" }, out _)?.Name;

    [Fact]
    public void Detect_RecognizesFormats()
    {
        Assert.Equal("clef", Detect("""{"@t":"2026-10-13T09:28:10Z","@mt":"x"}""" + "\n"));
        Assert.Equal("json", Detect("""{"Timestamp":"2026-10-13T09:28:10Z","Level":"Information","RenderedMessage":"x"}""" + "\n"));
        Assert.Equal("serilog", Detect("2026-10-13 09:28:10.512 +02:00 [INF] a\n2026-10-13 09:28:11.000 +02:00 [ERR] b\nSystem.Exception: x\n"));
        Assert.Equal("nlog", Detect("2026-10-13 09:28:10.5123|INFO|Shop|a\n"));
        Assert.Equal("log4net", Detect("2026-10-13 09:28:10,512 [1] INFO  Shop - a\n"));
        Assert.Equal("text", Detect("Server started\nListening on port 80\n"));
    }

    [Fact]
    public void Detect_IgnoresByteOrderMark()
    {
        Assert.Equal("serilog", Detect("﻿2026-10-13 09:28:10.512 +02:00 [INF] a\n"));
    }

    [Fact]
    public void Detect_WaitsForFirstCompleteLine()
    {
        Assert.Null(Detect("2026-10-13 09:28:10.512 +02:00 [INF] still being writ"));
    }

    [Fact]
    public void Detect_ConfiguredTemplateWins()
    {
        var options = new FileSourceOptions { Pattern = "*" }.UseTemplate("{Level:u3} {Message}");

        Assert.Equal("template", Detect("{\"@t\":\"x\"}\n", options));
    }

    [Fact]
    public void Detect_BinaryIsAnError()
    {
        Assert.Null(FormatDetector.Detect([0x50, 0x00, 0x0A], new FileSourceOptions { Pattern = "*" }, out var error));
        Assert.NotNull(error);
    }
}
