using System.Diagnostics;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;

namespace Kododo.MonitorWay.Demo.Web;

/// <summary>
/// Customers of the demo's <see cref="Shop"/>: real HTTP requests to the application itself, a few a second, so the
/// traces start with ASP.NET Core's spans and the overview has its request metrics.
/// </summary>
internal sealed class ShopSimulator(IServer server, IHostApplicationLifetime lifetime, Warehouse warehouse, ILogger<ShopSimulator> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!await StartedAsync(stoppingToken) || BaseAddress() is not { } address)
            return;
        warehouse.Start(address);

        // Without trace headers: each request is a trace of its own, started by the server's span, as from a browser.
        using var client = new HttpClient(new SocketsHttpHandler
        {
            ActivityHeadersPropagator = DistributedContextPropagator.CreateNoOutputPropagator(),
        })
        { BaseAddress = address };

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(Random.Shared.Next(150, 900), stoppingToken);
            // Requests overlap, as in a real shop.
            _ = Task.Run(() => SendAsync(client, stoppingToken), stoppingToken);
        }
    }

    private static async Task SendAsync(HttpClient client, CancellationToken cancellationToken)
    {
        try
        {
            using var response = Random.Shared.Next(5) == 0
                ? await client.GetAsync($"shop/products/{Shop.Products[Random.Shared.Next(Shop.Products.Length)]}", cancellationToken)
                : await client.PostAsync($"shop/orders?customer=c-{Random.Shared.Next(1, 400)}", null, cancellationToken);
        }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException)
        {
        }
    }

    private async Task<bool> StartedAsync(CancellationToken stoppingToken)
    {
        var started = new TaskCompletionSource();
        using var registration = lifetime.ApplicationStarted.Register(() => started.TrySetResult());
        await Task.WhenAny(started.Task, Task.Delay(Timeout.Infinite, stoppingToken));
        return started.Task.IsCompleted;
    }

    /// <summary>The address the server listens on, with a host name that can be connected to.</summary>
    private Uri? BaseAddress()
    {
        var addresses = server.Features.Get<IServerAddressesFeature>()?.Addresses ?? [];
        var address = addresses.FirstOrDefault(a => a.StartsWith("http://", StringComparison.Ordinal)) ?? addresses.FirstOrDefault();
        if (address is null)
        {
            logger.LogWarning("The shop simulator found no address of the server and does not run");
            return null;
        }
        var uri = new UriBuilder(address.Replace("://+", "://localhost").Replace("://*", "://localhost"));
        if (uri.Host is "0.0.0.0" or "[::]" or "::")
            uri.Host = "localhost";
        uri.Path = "/";
        return uri.Uri;
    }
}
