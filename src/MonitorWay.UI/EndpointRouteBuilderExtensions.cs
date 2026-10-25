using Kododo.MonitorWay.Memory;
using Kododo.MonitorWay.UI.API;
using Kododo.Reiho.AspNetCore.API;
using Kododo.Way.Shell;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Kododo.MonitorWay.UI;

/// <summary>
/// Extension methods for mounting the MonitorWay web UI in the ASP.NET Core middleware pipeline.
/// </summary>
public static class EndpointRouteBuilderExtensions
{
    /// <summary>
    /// The UI as a Way.Shell module: <c>SPA/dist</c> is built with the wayModule() preset. Telemetry often holds
    /// personal data and secrets, so the Way is sensitive.
    /// </summary>
    internal static readonly WayModule Module =
        new("monitor", "Monitor", typeof(EndpointRouteBuilderExtensions).Assembly, "SPA/dist")
        {
            Icon = "dashboard",
            IsSensitive = true,
        };

    extension(IEndpointRouteBuilder endpoints)
    {
        /// <summary>
        /// Mounts the MonitorWay web UI and its API at the specified path, in Way.Shell.
        /// </summary>
        /// <param name="path">
        /// The base path for the MonitorWay UI. Defaults to <c>/monitor</c>.
        /// The API is available at <c>{path}/api</c> and the UI at <c>{path}</c>.
        /// </param>
        /// <returns>
        /// A <see cref="RouteGroupBuilder"/> that can be used to apply additional middleware such as authentication or
        /// authorization policies. Outside the Development environment the UI and the API answer <c>403</c> until
        /// authorization is required here, anonymous access is allowed explicitly (<c>AllowAnonymous()</c>) or
        /// <c>ShellOptions.AllowAnonymous</c> is set.
        /// </returns>
        /// <example>
        /// <code>
        /// app.UseMonitorWay("/monitor").RequireAuthorization("Admin");
        /// </code>
        /// </example>
        public RouteGroupBuilder UseMonitorWay(string path = "/monitor")
        {
            if (endpoints.ServiceProvider.GetService<MonitorWayUiOptions>()?.CaptureOwnRequests != true &&
                endpoints.ServiceProvider.GetService<IgnoredRequests>() is { } ignored)
            {
                ignored.Add(path);
                // The shell's scripts and styles, loaded with every page of the UI.
                ignored.Add(endpoints.ServiceProvider.GetService<IOptions<ShellOptions>>()?.Value.AssetsPath ?? "/_way-shell");
            }

            var monitorwayEndpoints = endpoints.MapWay(Module, path);
            var api = monitorwayEndpoints.MapGroup("/api");
            api.MapRequests(typeof(EndpointRouteBuilderExtensions).Assembly);
            // Mapped as a Delegate, not a RequestDelegate: endpoint filters, such as the shell's protection, only run for
            // route handlers.
            api.MapGet("/stream", (Delegate)LiveStream.HandleAsync);
            return monitorwayEndpoints;
        }
    }
}
