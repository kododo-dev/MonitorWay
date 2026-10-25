using System.Text;
using System.Text.Json;

namespace Kododo.MonitorWay.Demo.Web;

/// <summary>
/// Writes large log files covering the past week once, <c>logs/history.clef</c> (CLEF) and <c>logs/history.log</c>
/// (text with multi-line exceptions), so the demo shows how MonitorWay handles big files: the newest entries appear
/// at once while the index is built in the background.
/// </summary>
internal sealed class HistoryGenerator(IHostEnvironment environment, IConfiguration configuration, ILogger<HistoryGenerator> logger)
    : BackgroundService
{
    private static readonly string[] Products = ["keyboard", "mouse", "monitor", "headset", "webcam", "dock", "cable"];

    private const string StackTrace =
        "System.TimeoutException: Stripe did not answer within 10 seconds.\n" +
        "   at Kododo.Shop.Payments.PaymentGateway.ChargeAsync(Order order)\n" +
        "   at Kododo.Shop.Orders.OrderService.PlaceAsync(Cart cart)";

    private sealed record Event(DateTimeOffset Time, LogLevel Level, string Category, string Template, string Message, string? Exception, long Order, string Product);

    protected override Task ExecuteAsync(CancellationToken stoppingToken) => Task.Run(() =>
    {
        Generate("history.clef", configuration.GetValue("Demo:HistoryMegabytes", 100), Clef, stoppingToken);
        Generate("history.log", configuration.GetValue("Demo:TextHistoryMegabytes", 50),
            e => TextFileLoggerProvider.Format(e.Time, e.Level, e.Category, e.Message, e.Exception), stoppingToken);
    }, stoppingToken);

    private void Generate(string name, int megabytes, Func<Event, string> write, CancellationToken stoppingToken)
    {
        var path = Path.Combine(environment.ContentRootPath, "logs", name);
        if (megabytes <= 0 || File.Exists(path))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        var random = new Random(42);
        var limit = megabytes * 1024L * 1024;
        var start = DateTimeOffset.Now.AddHours(-1) - TimeSpan.FromDays(7);
        long written = 0, entries = 0;

        using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20))
        {
            while (written < limit && !stoppingToken.IsCancellationRequested)
            {
                // Time follows the bytes written, so the week ends an hour ago whatever the line lengths are.
                var time = start + TimeSpan.FromDays(7) * ((double)written / limit);
                var bytes = Encoding.UTF8.GetBytes(write(Next(random, time, entries)));
                stream.Write(bytes);
                written += bytes.Length;
                entries++;
            }
        }
        if (stoppingToken.IsCancellationRequested)
            return;

        File.Move(temporary, path, overwrite: true);
        logger.LogInformation("Generated {Path} with {Entries} entries ({Megabytes} MB)", path, entries, megabytes);
    }

    private static Event Next(Random random, DateTimeOffset time, long index)
    {
        var roll = random.Next(1000);
        var order = 100_000 + index / 3;
        var product = Products[random.Next(Products.Length)];
        return roll switch
        {
            < 2 => new(time, LogLevel.Critical, "Kododo.Shop.Data.ShopDbContext", "Database connection pool exhausted: {Active} of {Max} connections in use",
                "Database connection pool exhausted: 100 of 100 connections in use", null, order, product),
            < 25 => new(time, LogLevel.Error, "Kododo.Shop.Payments.PaymentGateway", "Payment for order {OrderId} failed at {Provider}",
                $"Payment for order {order} failed at Stripe", StackTrace, order, product),
            < 80 => new(time, LogLevel.Warning, "Kododo.Shop.Payments.PaymentGateway", "{Provider} responded slowly ({ElapsedMs} ms) for order {OrderId}",
                $"PayU responded slowly ({random.Next(2000, 9000)} ms) for order {order}", null, order, product),
            < 400 => new(time, LogLevel.Information, "Kododo.Shop.Orders.OrderService", "Order {OrderId} placed: {Quantity} x {Product}",
                $"Order {order} placed: {random.Next(1, 4)} x {product}", null, order, product),
            < 700 => new(time, LogLevel.Debug, "Kododo.Shop.Data.ShopDbContext", "Executed DbCommand ({ElapsedMs}ms)",
                $"Executed DbCommand ({random.Next(1, 40)}ms)", null, order, product),
            _ => new(time, LogLevel.Trace, "Kododo.Shop.Catalog.CatalogCache", "Cache lookup for {Product}: hit={Hit}",
                $"Cache lookup for {product}: hit={random.Next(4) != 0}", null, order, product),
        };
    }

    private static string Clef(Event e)
    {
        var line = new StringBuilder("{\"@t\":\"").Append(e.Time.ToString("O")).Append("\",\"@mt\":")
            .Append(JsonSerializer.Serialize(e.Template)).Append(",\"@m\":").Append(JsonSerializer.Serialize(e.Message));
        if (e.Level != LogLevel.Information)
            line.Append(",\"@l\":\"").Append(ClefFileLoggerProvider.LevelName(e.Level)).Append('"');
        if (e.Exception is not null)
            line.Append(",\"@x\":").Append(JsonSerializer.Serialize(e.Exception));
        return line
            .Append(",\"OrderId\":").Append(e.Order)
            .Append(",\"Product\":\"").Append(e.Product).Append('"')
            .Append(",\"SourceContext\":\"").Append(e.Category).Append("\"}\n")
            .ToString();
    }
}
