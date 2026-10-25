using Kododo.Reiho.AspNetCore.API;
using Kododo.Way.Shell;
using Microsoft.Extensions.DependencyInjection;

namespace Kododo.MonitorWay.UI;

/// <summary>
/// Extension methods for enabling the MonitorWay embedded web UI.
/// </summary>
public static class MonitorWayBuilderExtensions
{
    /// <summary>
    /// Enables the MonitorWay web UI by registering its API request handlers and Way.Shell, which the UI runs in.
    /// Call <see cref="EndpointRouteBuilderExtensions.UseMonitorWay"/> in the middleware pipeline to mount the UI at a
    /// specific path; configure the shell (title, links, user) with <c>services.AddWayShell(o => ...)</c>.
    /// </summary>
    /// <param name="builder">The MonitorWay builder.</param>
    /// <param name="configure">Optional delegate for configuring the UI.</param>
    /// <returns>The same builder instance for method chaining.</returns>
    public static MonitorWayBuilder AddUi(this MonitorWayBuilder builder, Action<MonitorWayUiOptions>? configure = null)
    {
        var options = new MonitorWayUiOptions();
        configure?.Invoke(options);
        builder.Services.AddSingleton(options);
        builder.Services.AddRequestHandlers(typeof(MonitorWayBuilderExtensions).Assembly);
        builder.Services.AddWayShell();
        return builder;
    }
}
