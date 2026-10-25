using System.Text;
using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Files;
using Kododo.MonitorWay.Files.Formats;
using Kododo.MonitorWay.Querying;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kododo.MonitorWay.Tests.Files;

public class ConsoleFormatTests
{
    private const string Output = """
        info: Microsoft.Hosting.Lifetime[14]
              Now listening on: http://[::]:8080
        warn: Shop.Payments.PaymentGateway[3]
              PayU responded slowly
              after 7000 ms
        fail: Shop.Payments.PaymentGateway[0]
              Payment for order 1008 failed
              System.TimeoutException: Stripe did not answer
                 at Shop.Payments.Charge()
                 at Shop.Orders.Place()
        dbug: Shop.Data.ShopDbContext[20101]
              Executed DbCommand (4ms)

        """;

    private static LogEntry[] Read(string text)
    {
        using var file = new ClefFile("console.log");
        file.AppendRaw(text.Replace("\r\n", "\n"));
        var source = new FileLogSource(file.Path, "console.log");
        source.UpdateIndex(CancellationToken.None);
        Assert.Equal("console", source.Format?.Name);
        return source.ReadAfter(0, 100, LogQueryMatcher.All, CancellationToken.None).Entries.ToArray();
    }

    [Fact]
    public void ReadsEntriesWithMessagesOnTheNextLines()
    {
        var entries = Read(Output);

        Assert.Equal([LogLevel.Information, LogLevel.Warning, LogLevel.Error, LogLevel.Debug], entries.Select(e => e.Level));
        Assert.Equal("Microsoft.Hosting.Lifetime", entries[0].Category);
        Assert.Equal("Now listening on: http://[::]:8080", entries[0].Message);
        Assert.Contains(new("EventId", "14"), entries[0].Properties);
        Assert.Equal("PayU responded slowly\nafter 7000 ms", entries[1].Message);
        Assert.Equal("Payment for order 1008 failed", entries[2].Message);
        Assert.Equal("System.TimeoutException: Stripe did not answer\n   at Shop.Payments.Charge()\n   at Shop.Orders.Place()", entries[2].Exception);
        Assert.Equal("Executed DbCommand (4ms)", entries[3].Message);
    }

    [Fact]
    public void ExceptionWithHResultAndInnerException_IsTheException()
    {
        var entries = Read("""
            fail: Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware[1]
                  An unhandled exception has occurred while executing the request.
                  Npgsql.NpgsqlException (0x80004005): Exception while reading from stream
                   ---> System.TimeoutException: Timeout during reading attempt
                     at Npgsql.Internal.NpgsqlReadBuffer.EnsureLong(Int32 count, Boolean async)
                     at Shop.Api.Data.OrderRepository.FindAsync(Int32 id)

            """);

        Assert.Equal("An unhandled exception has occurred while executing the request.", entries[0].Message);
        Assert.StartsWith("Npgsql.NpgsqlException (0x80004005): Exception while reading from stream\n ---> System.TimeoutException", entries[0].Exception);
        Assert.EndsWith("at Shop.Api.Data.OrderRepository.FindAsync(Int32 id)", entries[0].Exception);
    }

    [Theory]
    [InlineData("Microsoft.Data.SqlClient.SqlException (0x80131904): Login failed for user 'shop'.")]
    [InlineData("Shop.Api.Orders+OrderNotFoundException: Order 1008 was not found")]
    [InlineData("System.AggregateException")]
    public void ExceptionFirstLines_StartTheException(string line)
    {
        var entries = Read($"fail: Shop.Orders[0]\n      Failed\n      {line}\n         at Shop.Orders.Place()\n");

        Assert.Equal("Failed", entries[0].Message);
        Assert.Equal($"{line}\n   at Shop.Orders.Place()", entries[0].Exception);
    }

    [Fact]
    public void MessageMentioningAnException_StaysTheMessage()
    {
        var entries = Read("warn: Shop.Orders[0]\n      Retrying after TimeoutException (attempt 2 of 3)\n");

        Assert.Equal("Retrying after TimeoutException (attempt 2 of 3)", entries[0].Message);
        Assert.Null(entries[0].Exception);
    }

    [Fact]
    public void ReadsTimestampPrefix()
    {
        var entries = Read("2026-10-13 09:28:10 info: Shop.Orders[1]\n      Order placed\n[2026-10-13 09:28:11] fail: Shop.Orders[2]\n      Failed\n");

        Assert.Equal(new DateTime(2026, 10, 13, 9, 28, 10), entries[0].Timestamp!.Value.DateTime);
        Assert.Equal(new DateTime(2026, 10, 13, 9, 28, 11), entries[1].Timestamp!.Value.DateTime);
        Assert.Equal("Shop.Orders", entries[1].Category);
    }

    [Fact]
    public void CountsLevelsAndCategories()
    {
        using var file = new ClefFile("console.log");
        file.AppendRaw(Output.Replace("\r\n", "\n"));
        var source = new FileLogSource(file.Path, "console.log");
        source.UpdateIndex(CancellationToken.None);

        Assert.Equal(1, source.GetStats().LevelCounts[(int)LogLevel.Error]);
        Assert.Equal(2, source.GetCategoryCounts()["Shop.Payments.PaymentGateway"]);
    }

    [Theory]
    [InlineData("info: Microsoft.Hosting.Lifetime[14]", true)]
    [InlineData("crit: App[-1]", true)]
    [InlineData("      Now listening on: http://[::]:8080", false)]
    [InlineData("information: App[1]", false)]
    [InlineData("info: App", false)]
    public void RecognizesEntryStarts(string line, bool expected)
    {
        Assert.Equal(expected, ConsoleFormat.Instance.IsEntryStart(Encoding.UTF8.GetBytes(line)));
    }
}
