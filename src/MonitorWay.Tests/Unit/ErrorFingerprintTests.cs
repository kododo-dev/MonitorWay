using Kododo.MonitorWay.Errors;
using Xunit;

namespace Kododo.MonitorWay.Tests.Unit;

public class ErrorFingerprintTests
{
    private const string Timeout = """
        System.TimeoutException: PayU did not answer within 10 seconds.
           at Shop.Payments.Gateway.<ChargeAsync>d__4.MoveNext() in C:\src\Gateway.cs:line 42
        --- End of stack trace from previous location ---
           at System.Runtime.ExceptionServices.ExceptionDispatchInfo.Throw()
           at Shop.Orders.OrderService.<PlaceAsync>d__7.MoveNext() in C:\src\OrderService.cs:line 18
           at Microsoft.AspNetCore.Routing.EndpointMiddleware.Invoke(HttpContext httpContext)
        """;

    [Fact]
    public void Frames_AreTheApplicationMethodsWithoutCompilerNames()
    {
        var (_, frames) = ErrorFingerprint.Compute("System.TimeoutException", Timeout, null);

        Assert.Equal(["Shop.Payments.Gateway.ChargeAsync", "Shop.Orders.OrderService.PlaceAsync"], frames);
    }

    [Fact]
    public void Message_LineNumbersAndOperation_DoNotChangeTheFingerprint()
    {
        var other = Timeout.Replace("PayU", "Stripe").Replace("line 42", "line 45").Replace("C:\\src", "/app");

        Assert.Equal(
            ErrorFingerprint.Compute("System.TimeoutException", Timeout, "POST /orders").Fingerprint,
            ErrorFingerprint.Compute("System.TimeoutException", other, "POST payments").Fingerprint);
    }

    [Fact]
    public void TypeAndPlace_ChangeTheFingerprint()
    {
        var fingerprint = ErrorFingerprint.Compute("System.TimeoutException", Timeout, null).Fingerprint;

        Assert.NotEqual(fingerprint, ErrorFingerprint.Compute("System.InvalidOperationException", Timeout, null).Fingerprint);
        Assert.NotEqual(fingerprint, ErrorFingerprint.Compute("System.TimeoutException", Timeout.Replace("ChargeAsync", "RefundAsync"), null).Fingerprint);
    }

    [Fact]
    public void TypeWithoutItsNamespace_IsTheSameError()
    {
        // The OTLP log exporter of .NET sends the type's name alone; a span event, as most senders, the full name.
        var full = ErrorFingerprint.Compute("System.TimeoutException", Timeout, null, out var fullType);
        var shortName = ErrorFingerprint.Compute("TimeoutException", Timeout, null, out var completed);

        Assert.Equal(full.Fingerprint, shortName.Fingerprint);
        Assert.Equal("System.TimeoutException", fullType);
        Assert.Equal("System.TimeoutException", completed);
    }

    [Fact]
    public void WithoutFrames_TheOperationTells()
    {
        var a = ErrorFingerprint.Compute("System.TimeoutException", "System.TimeoutException: late", "POST /orders");
        var b = ErrorFingerprint.Compute("System.TimeoutException", "System.TimeoutException: later", "POST /orders");
        var c = ErrorFingerprint.Compute("System.TimeoutException", "System.TimeoutException: late", "GET /health");

        Assert.Empty(a.Frames);
        Assert.Equal(a.Fingerprint, b.Fingerprint);
        Assert.NotEqual(a.Fingerprint, c.Fingerprint);
    }

    [Fact]
    public void InnerExceptions_TheOuterFramesCount()
    {
        var text = """
            System.InvalidOperationException: Order failed
             ---> System.IO.IOException: Disk full
               at Shop.Storage.Files.Write() in C:\src\Files.cs:line 3
               --- End of inner exception stack trace ---
               at Shop.Orders.OrderService.Place() in C:\src\OrderService.cs:line 9
            """;

        Assert.Equal(["Shop.Orders.OrderService.Place"], ErrorFingerprint.Compute("System.InvalidOperationException", text, null).Frames);
    }

    [Fact]
    public void OnlyFrameworkFrames_AreUsedWhenThereIsNothingElse()
    {
        var text = """
            System.ArgumentNullException: Value cannot be null.
               at System.ArgumentNullException.Throw(String paramName)
               at System.Text.Json.JsonSerializer.Deserialize(String json)
            """;

        Assert.Equal(["System.ArgumentNullException.Throw", "System.Text.Json.JsonSerializer.Deserialize"],
            ErrorFingerprint.Compute("System.ArgumentNullException", text, null).Frames);
    }

    [Fact]
    public void LocalizedStackTraces_AreRead()
    {
        var text = "System.Exception: x\n   w Shop.Orders.OrderService.Place() w C:\\src\\OrderService.cs:wiersz 9";

        Assert.Equal(["Shop.Orders.OrderService.Place"], ErrorFingerprint.Compute("System.Exception", text, null).Frames);
    }

    [Fact]
    public void Lambdas_KeepTheirMethod()
    {
        var text = "System.Exception: x\n   at Shop.Program.<>c__DisplayClass0_0.<Main>b__1(HttpContext c) in Program.cs:line 3";

        Assert.Equal(["Shop.Program.Main.lambda"], ErrorFingerprint.Compute("System.Exception", text, null).Frames);
    }
}
