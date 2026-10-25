using Kododo.MonitorWay.Memory;
using Kododo.MonitorWay.UI;
using Kododo.MonitorWay.UI.API;
using Kododo.Reiho.AspNetCore.API;
using Kododo.Reiho.AspNetCore.Mcp;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Kododo.Way.Shell;
using Microsoft.AspNetCore.Authorization.Policy;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Kododo.MonitorWay.Mcp;

/// <summary>
/// Lets AI agents read the telemetry over the Model Context Protocol, with the same API the web UI uses: an agent can go
/// from the overview or an error to its trace and to the logs of that request.
/// </summary>
public static class MonitorWayMcpExtensions
{
    // Only reads: an agent can look at the telemetry, not change anything.
    private static readonly Type[] Tools =
    [
        typeof(GetServiceOverview), typeof(GetServices),
        typeof(GetSources), typeof(GetEntries), typeof(GetEntry), typeof(GetAround), typeof(GetStats), typeof(GetCategories),
        typeof(GetTraces), typeof(GetTrace), typeof(GetTraceStats),
        typeof(GetErrors), typeof(GetError), typeof(GetErrorStats),
        typeof(GetMetrics), typeof(GetMetricSeries), typeof(GetMetricStats),
    ];

    extension(IMcpServerBuilder builder)
    {
        /// <summary>
        /// Offers the telemetry as read-only MCP tools: <c>monitorway_get_service_overview</c>, <c>_get_services</c>; the logs
        /// (<c>monitorway_get_sources</c>, <c>_get_entries</c>, <c>_get_entry</c>, <c>_get_around</c>, <c>_get_stats</c>,
        /// <c>_get_categories</c>); the traces (<c>_get_traces</c>, <c>_get_trace</c>, <c>_get_trace_stats</c>); the errors
        /// (<c>_get_errors</c>, <c>_get_error</c>, <c>_get_error_stats</c>) and the metrics (<c>_get_metrics</c>,
        /// <c>_get_metric_series</c>, <c>_get_metric_stats</c>). Needs <c>AddUi()</c>, which registers their handlers.
        /// </summary>
        /// <param name="prefix">Put before every tool name, so the tools do not clash with others on the same server.</param>
        /// <example>
        /// <code>
        /// builder.AddMonitorWay(x => { x.AddMemoryBuffer(); x.AddUi(); });
        /// builder.Services.AddMcpServer().WithHttpTransport().WithMonitorWayTools();
        /// app.MapMonitorWayMcp().RequireAuthorization("Admin");
        /// </code>
        /// </example>
        public IMcpServerBuilder WithMonitorWayTools(string prefix = "monitorway_") =>
            builder.WithRequests(typeof(GetEntries).Assembly, o =>
            {
                o.Prefix = prefix;
                foreach (var tool in Tools)
                    o.Include(tool, new McpToolAttribute { ReadOnly = true, Idempotent = true });
            });
    }

    extension(IEndpointRouteBuilder endpoints)
    {
        /// <summary>
        /// Maps the MCP endpoint. Like the UI, outside the Development environment it answers <c>403</c> until
        /// authorization is required on it, a fallback policy is set or <see cref="ShellOptions.AllowAnonymous"/> is
        /// set: telemetry often holds personal data and secrets.
        /// </summary>
        public IEndpointConventionBuilder MapMonitorWayMcp(string pattern = "/monitor/mcp")
        {
            // The agent's own requests would otherwise fill the buffer it reads.
            if (endpoints.ServiceProvider.GetService<MonitorWayUiOptions>()?.CaptureOwnRequests != true)
                endpoints.ServiceProvider.GetService<IgnoredRequests>()?.Add(pattern);

            var mcp = endpoints.MapMcp(pattern);
            mcp.Add(endpoint =>
            {
                var next = endpoint.RequestDelegate!;
                endpoint.RequestDelegate = context => IsProtected(context) ? next(context) : ForbidAsync(context);
            });
            return mcp;
        }
    }

    private static bool IsProtected(HttpContext context)
    {
        var services = context.RequestServices;
        var metadata = context.GetEndpoint()?.Metadata;
        return metadata?.GetMetadata<IAuthorizeData>() is not null ||
               metadata?.GetMetadata<AuthorizationPolicy>() is not null ||
               metadata?.GetMetadata<IAllowAnonymous>() is not null ||
               services.GetService<IOptions<AuthorizationOptions>>()?.Value.FallbackPolicy is not null ||
               services.GetService<IOptions<ShellOptions>>()?.Value.AllowAnonymous == true ||
               services.GetService<IHostEnvironment>()?.IsDevelopment() == true;
    }

    private static Task ForbidAsync(HttpContext context) =>
        Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "MonitorWay requires authorization",
            detail: "Outside the Development environment, call RequireAuthorization() on MapMonitorWayMcp() " +
                    "or set ShellOptions.AllowAnonymous.").ExecuteAsync(context);
}
