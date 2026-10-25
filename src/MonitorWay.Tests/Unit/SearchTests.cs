using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Querying;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Kododo.MonitorWay.Tests.Unit;

public class SearchTests
{
    private static readonly LogEntry Payment = new(
        1, DateTimeOffset.UtcNow, LogLevel.Error, "Kododo.Shop.Payments.PaymentGateway",
        "Payment for order 1008 failed at Stripe", "Payment for order {OrderId} failed at {Provider}",
        "System.TimeoutException: Stripe did not answer", [new("OrderId", "1008"), new("CustomerId", "c-63"), new("TraceId", "abc123")],
        "abc123", "span1");

    private static readonly LogEntry Health = new(
        2, DateTimeOffset.UtcNow, LogLevel.Information, "Microsoft.AspNetCore.Hosting",
        "Request GET /health finished", null, null, [], null, null);

    [Theory]
    [InlineData("service:warehouse", true, false)]
    [InlineData("service:ware*", true, false)]
    [InlineData("-service:warehouse", false, true)]
    public void ServiceTerm_MatchesTheServiceOfTheEntry(string text, bool fromWarehouse, bool fromFile)
    {
        Assert.Equal(fromWarehouse, Matches(text, Payment with { ServiceName = "warehouse" }));
        // A file entry has no service.
        Assert.Equal(fromFile, Matches(text, Payment));
    }

    private static bool Matches(string text, LogEntry entry, bool caseSensitive = false) =>
        LogQueryMatcher.Create(new LogQuery(Text: text, CaseSensitive: caseSensitive)).Matches(entry);

    [Theory]
    [InlineData("payment stripe", true)]
    [InlineData("payment paypal", false)]
    [InlineData("\"order 1008 failed\"", true)]
    [InlineData("\"failed order\"", false)]
    [InlineData("-stripe", false)]
    [InlineData("payment -health", true)]
    [InlineData("-\"order 1008\"", false)]
    public void TextTerms_AreAndedAndCanBeNegated(string text, bool expected)
    {
        Assert.Equal(expected, Matches(text, Payment));
    }

    [Theory]
    [InlineData("category:Payments", true)]
    [InlineData("cat:payments", true)]
    [InlineData("-category:Payments", false)]
    [InlineData("category:Orders", false)]
    [InlineData("message:Stripe", true)]
    [InlineData("message:TimeoutException", false)]
    [InlineData("exception:TimeoutException", true)]
    [InlineData("ex:NullReference", false)]
    [InlineData("has:exception", true)]
    [InlineData("has:CustomerId", true)]
    [InlineData("has:Missing", false)]
    [InlineData("trace:ABC123", true)]
    [InlineData("trace:other", false)]
    [InlineData("OrderId:1008", true)]
    [InlineData("orderid:1008", true)]
    [InlineData("OrderId:100", false)]
    [InlineData("CustomerId:c-6*", true)]
    [InlineData("CustomerId:*63", true)]
    [InlineData("CustomerId:*-6*", true)]
    [InlineData("CustomerId:x*", false)]
    [InlineData("OrderId:\"1008\"", true)]
    public void FieldTerms(string text, bool expected)
    {
        Assert.Equal(expected, Matches(text, Payment));
    }

    [Fact]
    public void HasException_ExcludesEntriesWithout()
    {
        Assert.False(Matches("has:exception", Health));
        Assert.True(Matches("-has:exception", Health));
    }

    [Fact]
    public void TokenWithUrl_IsPlainText()
    {
        var entry = Health with { Message = "Calling https://api.example.com/pay" };

        Assert.True(Matches("https://api.example.com", entry));
    }

    [Fact]
    public void QuotedFieldLikeText_IsPlainText()
    {
        var entry = Health with { Message = "status:ok" };

        Assert.True(Matches("\"status:ok\"", entry));
    }

    [Fact]
    public void CaseSensitive_AppliesToText()
    {
        Assert.False(Matches("stripe", Payment, caseSensitive: true));
        Assert.True(Matches("Stripe", Payment, caseSensitive: true));
    }

    [Fact]
    public void Parse_SplitsTokens()
    {
        var terms = SearchExpression.Parse("  timeout -health \"a b\" OrderId:\"x y\" -cat:Cache  ").Terms;

        Assert.Equal(
            [
                new SearchTerm(SearchTermKind.Text, null, "timeout", false),
                new SearchTerm(SearchTermKind.Text, null, "health", true),
                new SearchTerm(SearchTermKind.Text, null, "a b", false),
                new SearchTerm(SearchTermKind.Property, "OrderId", "x y", false),
                new SearchTerm(SearchTermKind.Category, "cat", "Cache", true),
            ],
            terms);
    }

    [Fact]
    public void Categories_IncludeWholeNamesIgnoringCase()
    {
        var matcher = LogQueryMatcher.Create(new LogQuery(Categories: ["kododo.shop.payments.paymentgateway", "Other"]));

        Assert.True(matcher.Matches(Payment));
        Assert.False(matcher.Matches(Health));
        Assert.False(LogQueryMatcher.Create(new LogQuery(Categories: ["Kododo.Shop.Payments"])).Matches(Payment));
    }

    [Fact]
    public void ExcludedCategories_LeaveOutWholeNames()
    {
        var matcher = LogQueryMatcher.Create(new LogQuery(ExcludedCategories: ["Microsoft.AspNetCore.Hosting"]));

        Assert.True(matcher.Matches(Payment));
        Assert.False(matcher.Matches(Health));
        Assert.True(LogQueryMatcher.Create(new LogQuery(ExcludedCategories: ["Microsoft.AspNetCore"])).Matches(Health));
    }

    [Fact]
    public void Categories_LeaveOutEntriesWithoutCategoryOnlyWhenIncluding()
    {
        var uncategorized = Health with { Category = null };

        Assert.False(LogQueryMatcher.Create(new LogQuery(Categories: ["Any"])).Matches(uncategorized));
        Assert.True(LogQueryMatcher.Create(new LogQuery(ExcludedCategories: ["Any"])).Matches(uncategorized));
    }

    [Fact]
    public void Categories_ApplyToRegexSearch()
    {
        var matcher = LogQueryMatcher.Create(new LogQuery(Text: "GET|Stripe", Regex: true, ExcludedCategories: ["Microsoft.AspNetCore.Hosting"]));

        Assert.True(matcher.Matches(Payment));
        Assert.False(matcher.Matches(Health));
    }
}
