using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Querying;
using Xunit;
using static Kododo.MonitorWay.Tests.Unit.TraceStoreTests;

namespace Kododo.MonitorWay.Tests.Unit;

public class TraceSearchTests
{
    private static readonly DateTimeOffset Start = new(2026, 10, 25, 12, 0, 0, TimeSpan.Zero);

    private static readonly TraceEntry Order = new(1, "0af7651916cd43dd8448eb211c80319c",
    [
        Span("t", "r", null, kind: TraceSpanKind.Server, name: "POST /orders", duration: TimeSpan.FromMilliseconds(800),
            attributes: [new("http.route", "/orders"), new("http.response.status_code", "500")]),
        Span("t", "db", "r", kind: TraceSpanKind.Client, name: "INSERT orders", service: "orders-db",
            attributes: [new("db.system", "postgresql")]),
        Span("t", "pay", "r", kind: TraceSpanKind.Client, name: "charge", status: TraceSpanStatus.Error,
            events: [new SpanEvent("exception", Start, [new("exception.type", "System.TimeoutException"), new("exception.message", "PayU did not answer")])]),
    ]);

    private static readonly TraceEntry Health = new(2, "11111111111111111111111111111111",
    [
        Span("h", "r", null, kind: TraceSpanKind.Server, name: "GET /health", duration: TimeSpan.FromMilliseconds(3),
            attributes: [new("http.route", "/health")]),
    ]);

    [Theory]
    [InlineData("orders", true, false)]
    [InlineData("payu", true, false)]
    [InlineData("-health", true, false)]
    [InlineData("\"GET /health\"", false, true)]
    [InlineData("service:orders-db", true, false)]
    [InlineData("service:orders*", true, false)]
    [InlineData("name:GET*", false, true)]
    [InlineData("kind:client", true, false)]
    [InlineData("status:error", true, false)]
    [InlineData("status:ok", false, true)]
    [InlineData("has:error", true, false)]
    [InlineData("has:exception", true, false)]
    [InlineData("has:db.system", true, false)]
    [InlineData("exception:Timeout", true, false)]
    [InlineData("trace:0af76519", true, false)]
    [InlineData("http.route:/orders", true, false)]
    [InlineData("http.response.status_code:5*", true, false)]
    [InlineData("duration:>500ms", true, false)]
    [InlineData("duration:<10", false, true)]
    [InlineData("duration:>=0.8s", true, false)]
    [InlineData("duration:1s", false, false)]
    [InlineData("service:shop http.route:/health", false, true)]
    public void Matches(string text, bool order, bool health)
    {
        var matcher = TraceQueryMatcher.Create(new TraceQuery(text));
        Assert.Equal(order, matcher.Matches(Order));
        Assert.Equal(health, matcher.Matches(Health));
    }

    [Theory]
    [InlineData("duration:>fast")]
    [InlineData("duration:5h")]
    [InlineData("status:broken")]
    [InlineData("kind:server2")]
    public void InvalidTerms_AreReported(string text) =>
        Assert.Throws<ArgumentException>(() => TraceQueryMatcher.Create(new TraceQuery(text)));

    [Fact]
    public void TimeRange_AppliesToTheStartOfTheTrace()
    {
        Assert.True(TraceQueryMatcher.Create(new TraceQuery(From: Start)).Matches(Order));
        Assert.False(TraceQueryMatcher.Create(new TraceQuery(From: Start.AddSeconds(1))).Matches(Order));
        Assert.False(TraceQueryMatcher.Create(new TraceQuery(To: Start.AddSeconds(-1))).Matches(Order));
    }
}
