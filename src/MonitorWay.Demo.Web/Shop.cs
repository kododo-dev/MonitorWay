using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace Kododo.MonitorWay.Demo.Web;

/// <summary>
/// The demo's online shop behind <c>/shop</c>: its requests have nested spans (cache, database, stock, coupons, payment
/// provider), logs at every level with properties and scopes, slow calls, exceptions and metrics of their own.
/// <see cref="ShopSimulator"/> sends it customers.
/// </summary>
internal sealed class Shop(ILoggerFactory loggers, Warehouse warehouse)
{
    /// <summary>The name of the shop's activity source and meter; added to the tracing and metrics in Program.cs.</summary>
    public const string SourceName = "Kododo.Shop";

    public static readonly string[] Products = ["keyboard", "mouse", "monitor", "headset", "webcam", "dock", "cable"];
    private static readonly string[] Providers = ["Przelewy24", "Stripe", "PayU"];

    private static readonly ActivitySource Source = new(SourceName);
    private static readonly Meter Meter = new(SourceName);
    private static readonly Counter<long> Orders = Meter.CreateCounter<long>("shop.orders", "{order}", "Orders by result");
    private static readonly Histogram<double> OrderTotal = Meter.CreateHistogram<double>("shop.order.total", "PLN", "Value of the placed orders");
    private static int _openCarts;

    static Shop() =>
        Meter.CreateObservableUpDownCounter("shop.carts.open", () => Volatile.Read(ref _openCarts), "{cart}", "Carts being checked out");

    private readonly ILogger _orders = loggers.CreateLogger("Kododo.Shop.Orders.OrderService");
    private readonly ILogger _payments = loggers.CreateLogger("Kododo.Shop.Payments.PaymentGateway");
    private readonly ILogger _catalog = loggers.CreateLogger("Kododo.Shop.Catalog.CatalogCache");
    private readonly ILogger _inventory = loggers.CreateLogger("Kododo.Shop.Inventory.StockReservations");
    private readonly ILogger _database = loggers.CreateLogger("Kododo.Shop.Data.ShopDbContext");
    private int _orderNumber = 1000;

    /// <summary>Places an order and returns the status code of the response.</summary>
    public async Task<int> PlaceOrderAsync(string customer, CancellationToken cancellationToken)
    {
        var random = Random.Shared;
        var orderNumber = Interlocked.Increment(ref _orderNumber);
        Activity.Current?.SetTag("enduser.id", customer);
        using var scope = _orders.BeginScope(new Dictionary<string, object?>
        {
            ["RequestId"] = Guid.NewGuid().ToString("N")[..12],
            ["CustomerId"] = customer,
        });

        Interlocked.Increment(ref _openCarts);
        var result = "placed";
        try
        {
            var product = Products[random.Next(Products.Length)];
            await LookUpProductAsync(random, product, cancellationToken);

            var roll = random.Next(1000);
            if (roll < 6)
            {
                using var db = Database("SELECT", "connections");
                await Task.Delay(random.Next(900, 1500), cancellationToken);
                db?.SetStatus(ActivityStatusCode.Error, "Connection pool exhausted");
                _database.LogCritical("Database connection pool exhausted: {Active} of {Max} connections in use", 100, 100);
                result = "unavailable";
                return 503;
            }

            if (!await ReserveStockAsync(random, product, orderNumber, failing: roll < 60, cancellationToken))
            {
                result = "out_of_stock";
                return 409;
            }

            if (roll < 140)
            {
                var provider = Providers[random.Next(Providers.Length)];
                if (!await ChargeAsync(random, provider, orderNumber, failing: roll < 90, cancellationToken))
                {
                    result = "payment_failed";
                    return 502;
                }
            }
            else
            {
                await ChargeAsync(random, Providers[random.Next(Providers.Length)], orderNumber, failing: false, cancellationToken, quick: true);
            }

            if (random.Next(30) == 0)
                ApplyCoupon(random);

            using (Database("INSERT", "orders"))
                await Task.Delay(random.Next(2, 15), cancellationToken);

            var total = Math.Round(random.NextDouble() * 900 + 20, 2);
            OrderTotal.Record(total);
            _orders.LogInformation(new EventId(100, "OrderPlaced"), "Order {OrderId} placed: {Quantity} x {Product}, total {Total} PLN",
                orderNumber, random.Next(1, 4), product, total);
            return 201;
        }
        finally
        {
            Interlocked.Decrement(ref _openCarts);
            Orders.Add(1, new KeyValuePair<string, object?>("result", result));
        }
    }

    /// <summary>Shows a product.</summary>
    public Task BrowseAsync(string product, CancellationToken cancellationToken) =>
        LookUpProductAsync(Random.Shared, product, cancellationToken);

    private async Task LookUpProductAsync(Random random, string product, CancellationToken cancellationToken)
    {
        using var lookup = Source.StartActivity("CatalogCache.Get");
        var hit = random.Next(4) != 0;
        lookup?.SetTag("cache.hit", hit);
        _catalog.LogTrace("Cache lookup for {Product}: hit={Hit}", product, hit);
        await Task.Delay(random.Next(0, 2), cancellationToken);
        if (hit)
            return;

        using var db = Database("SELECT", "products");
        var elapsed = random.Next(1, 40);
        await Task.Delay(elapsed, cancellationToken);
        _database.LogDebug("Executed DbCommand ({ElapsedMs}ms) SELECT p.\"Id\", p.\"Price\" FROM \"Products\" AS p WHERE p.\"Slug\" = @slug", elapsed);
    }

    private async Task<bool> ReserveStockAsync(Random random, string product, int orderNumber, bool failing, CancellationToken cancellationToken)
    {
        using var reserve = Source.StartActivity("StockReservations.Reserve");
        reserve?.SetTag("product", product);
        using (Database("UPDATE", "stock"))
            await Task.Delay(random.Next(3, 25), cancellationToken);

        // The warehouse is another service: the call's context goes to it as in the headers of a request.
        using (var call = Source.StartActivity("POST warehouse/reservations", ActivityKind.Client))
        {
            call?.SetTag("server.address", "warehouse");
            var context = call is null ? default : new ActivityContext(call.TraceId, call.SpanId, call.ActivityTraceFlags, isRemote: true);
            var reserved = await warehouse.ReserveAsync(context, product, 1, cancellationToken);
            call?.SetTag("http.response.status_code", reserved ? 201 : 500);
            if (!reserved)
            {
                call?.SetStatus(ActivityStatusCode.Error);
                _inventory.LogWarning("The warehouse could not reserve {Product} for order {OrderId}", product, orderNumber);
                return false;
            }
        }
        if (!failing)
            return true;

        try
        {
            throw new InvalidOperationException($"Stock of '{product}' in warehouse W{random.Next(1, 5)} changed during reservation.");
        }
        catch (InvalidOperationException e)
        {
            Fail(reserve, e);
            _inventory.LogError(e, "Could not reserve {Quantity} x {Product} for order {OrderId}", 3, product, orderNumber);
            return false;
        }
    }

    private async Task<bool> ChargeAsync(
        Random random, string provider, int orderNumber, bool failing, CancellationToken cancellationToken, bool quick = false)
    {
        using var call = Source.StartActivity("POST payments", ActivityKind.Client);
        call?.SetTag("http.request.method", "POST");
        call?.SetTag("server.address", $"api.{provider.ToLowerInvariant()}.example");
        call?.SetTag("payment.provider", provider);

        if (quick)
        {
            await Task.Delay(random.Next(40, 250), cancellationToken);
            call?.SetTag("http.response.status_code", 200);
            return true;
        }

        var elapsed = random.Next(1200, 3500);
        await Task.Delay(failing ? Math.Max(elapsed, 2500) : elapsed, cancellationToken);
        if (failing)
        {
            try
            {
                throw new TimeoutException($"{provider} did not answer within 10 seconds.");
            }
            catch (TimeoutException e)
            {
                Fail(call, e);
                _payments.LogError(e, "Payment for order {OrderId} failed at {Provider}", orderNumber, provider);
                return false;
            }
        }

        call?.SetTag("http.response.status_code", 200);
        _payments.LogWarning("{Provider} responded slowly ({ElapsedMs} ms) for order {OrderId}", provider, elapsed, orderNumber);
        return true;
    }

    /// <summary>A coupon code that is not a number: the order goes on without the discount.</summary>
    private void ApplyCoupon(Random random)
    {
        using var coupon = Source.StartActivity("Coupons.Apply");
        var code = $"SALE-{random.Next(10, 99)}";
        try
        {
            _ = int.Parse(code, System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (FormatException e)
        {
            // Handled, so the span records the exception without failing.
            RecordException(coupon, e);
            _orders.LogWarning(e, "Coupon {Coupon} ignored", code);
        }
    }

    private static Activity? Database(string operation, string table)
    {
        var activity = Source.StartActivity($"{operation} {table}", ActivityKind.Client);
        activity?.SetTag("db.system.name", "postgresql");
        activity?.SetTag("db.operation.name", operation);
        activity?.SetTag("db.collection.name", table);
        return activity;
    }

    /// <summary>Marks the span failed with the exception as an event, as the OpenTelemetry conventions describe.</summary>
    private static void Fail(Activity? activity, Exception e)
    {
        activity?.SetStatus(ActivityStatusCode.Error, e.Message);
        RecordException(activity, e);
    }

    private static void RecordException(Activity? activity, Exception e) =>
        activity?.AddEvent(new ActivityEvent("exception", tags: new ActivityTagsCollection
        {
            ["exception.type"] = e.GetType().FullName,
            ["exception.message"] = e.Message,
            ["exception.stacktrace"] = e.ToString(),
        }));
}
