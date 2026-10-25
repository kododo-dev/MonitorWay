using System.IO.Compression;
using Google.Protobuf;
using Kododo.MonitorWay.Memory;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Proto.Collector.Logs.V1;
using OpenTelemetry.Proto.Collector.Metrics.V1;
using OpenTelemetry.Proto.Collector.Trace.V1;

namespace Kododo.MonitorWay.Otlp;

/// <summary>
/// Receives OTLP over HTTP: other services, in any language, send their traces, metrics and logs to the application's
/// MonitorWay, which shows them next to its own.
/// </summary>
public static class MonitorWayOtlpExtensions
{
    /// <summary>Largest request accepted, after decompression.</summary>
    internal const int MaxBodyBytes = 32 * 1024 * 1024;

    extension(IEndpointRouteBuilder endpoints)
    {
        /// <summary>
        /// Maps the OTLP/HTTP receiver: <c>{pattern}/v1/traces</c>, <c>{pattern}/v1/metrics</c> and <c>{pattern}/v1/logs</c>,
        /// in protobuf (<c>application/x-protobuf</c>) or JSON (<c>application/json</c>), optionally gzip-compressed. Point an
        /// OpenTelemetry SDK or collector at it, e.g. <c>OTEL_EXPORTER_OTLP_ENDPOINT=https://host/monitor/otlp</c> with
        /// <c>OTEL_EXPORTER_OTLP_PROTOCOL=http/protobuf</c>. Needs <c>builder.AddMonitorWay()</c>.
        /// </summary>
        /// <returns>
        /// The group of the three endpoints, for authorization. Outside the Development environment they answer <c>403</c>
        /// until authorization is required on them, anonymous access is allowed explicitly (<c>AllowAnonymous()</c>) or a
        /// fallback policy is set: anyone who can send could fill the memory or plant misleading data.
        /// </returns>
        /// <example>
        /// <code>
        /// app.MapMonitorWayOtlp("/monitor/otlp").RequireAuthorization("TelemetrySenders");
        /// </code>
        /// </example>
        public RouteGroupBuilder MapMonitorWayOtlp(string pattern = "/monitor/otlp")
        {
            var services = endpoints.ServiceProvider;
            var ingest = new OtlpIngest(
                Required<MemoryTraceStore>(services), Required<MemoryLogBuffer>(services), Required<MemoryMetricStore>(services),
                Required<MemoryErrorStore>(services), Required<ServiceDirectory>(services),
                services.GetService<TimeProvider>() ?? TimeProvider.System);

            // What other services send is data to show, like the application's own exports.
            Required<MemoryTraceStore>(services).IsExportedTo = true;
            Required<MemoryLogBuffer>(services).IsExportedTo = true;
            Required<MemoryMetricStore>(services).IsExportedTo = true;
            Required<MemoryErrorStore>(services).IsExportedTo = true;
            // The application's own telemetry of receiving would crowd what it receives.
            services.GetService<IgnoredRequests>()?.Add(pattern);

            var group = endpoints.MapGroup(pattern);
            group.MapPost("/v1/traces", context => ReceiveAsync(context, ExportTraceServiceRequest.Parser,
                request => ingest.Traces(request), new ExportTraceServiceResponse()));
            group.MapPost("/v1/logs", context => ReceiveAsync(context, ExportLogsServiceRequest.Parser,
                request => ingest.Logs(request), new ExportLogsServiceResponse()));
            group.MapPost("/v1/metrics", context => ReceiveAsync(context, ExportMetricsServiceRequest.Parser,
                request => ingest.Metrics(request), new ExportMetricsServiceResponse()));
            ((IEndpointConventionBuilder)group).Add(endpoint =>
            {
                var next = endpoint.RequestDelegate!;
                endpoint.RequestDelegate = context => IsProtected(context) ? next(context) : ForbidAsync(context);
            });
            return group;
        }
    }

    private static T Required<T>(IServiceProvider services) where T : notnull =>
        services.GetService<T>() ?? throw new InvalidOperationException(
            "MonitorWay is not registered: call builder.AddMonitorWay() before MapMonitorWayOtlp().");

    private static async Task ReceiveAsync<TRequest, TResponse>(
        HttpContext context, MessageParser<TRequest> parser, Func<TRequest, int> ingest, TResponse response)
        where TRequest : IMessage<TRequest>, new()
        where TResponse : IMessage<TResponse>
    {
        var json = IsJson(context.Request.ContentType);
        if (!json && !IsProtobuf(context.Request.ContentType))
        {
            await PlainAsync(context, StatusCodes.Status415UnsupportedMediaType, "Send application/x-protobuf or application/json.");
            return;
        }

        byte[] body;
        try
        {
            body = await ReadAsync(context.Request, context.RequestAborted);
        }
        catch (InvalidDataException e)
        {
            await PlainAsync(context, StatusCodes.Status400BadRequest, e.Message);
            return;
        }
        if (body.Length > MaxBodyBytes)
        {
            await PlainAsync(context, StatusCodes.Status413PayloadTooLarge, $"At most {MaxBodyBytes / 1024 / 1024} MB per request.");
            return;
        }

        TRequest request;
        try
        {
            request = json ? OtlpJson.Parse<TRequest>(body) : parser.ParseFrom(body);
        }
        catch (Exception e) when (e is InvalidProtocolBufferException or InvalidJsonException or FormatException)
        {
            await PlainAsync(context, StatusCodes.Status400BadRequest, $"Not an OTLP message: {e.Message}");
            return;
        }

        ingest(request);
        context.Response.StatusCode = StatusCodes.Status200OK;
        if (json)
        {
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync(JsonFormatter.Default.Format(response), context.RequestAborted);
        }
        else
        {
            context.Response.ContentType = "application/x-protobuf";
            await context.Response.Body.WriteAsync(response.ToByteArray(), context.RequestAborted);
        }
    }

    /// <summary>The body, decompressed when it is gzip, read up to one byte over the limit.</summary>
    private static async Task<byte[]> ReadAsync(HttpRequest request, CancellationToken cancellationToken)
    {
        var gzip = request.Headers.ContentEncoding.Any(e => string.Equals(e, "gzip", StringComparison.OrdinalIgnoreCase));
        await using var source = gzip ? new GZipStream(request.Body, CompressionMode.Decompress) : request.Body;
        using var buffer = new MemoryStream();
        var chunk = new byte[81920];
        int read;
        while ((read = await source.ReadAsync(chunk, cancellationToken)) > 0)
        {
            buffer.Write(chunk, 0, read);
            if (buffer.Length > MaxBodyBytes)
                break;
        }
        return buffer.ToArray();
    }

    private static bool IsJson(string? contentType) =>
        contentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsProtobuf(string? contentType) =>
        contentType?.StartsWith("application/x-protobuf", StringComparison.OrdinalIgnoreCase) == true ||
        contentType?.StartsWith("application/protobuf", StringComparison.OrdinalIgnoreCase) == true;

    private static Task PlainAsync(HttpContext context, int status, string message)
    {
        context.Response.StatusCode = status;
        context.Response.ContentType = "text/plain";
        return context.Response.WriteAsync(message, context.RequestAborted);
    }

    private static bool IsProtected(HttpContext context)
    {
        var services = context.RequestServices;
        var metadata = context.GetEndpoint()?.Metadata;
        return metadata?.GetMetadata<IAuthorizeData>() is not null ||
               metadata?.GetMetadata<AuthorizationPolicy>() is not null ||
               metadata?.GetMetadata<IAllowAnonymous>() is not null ||
               services.GetService<IOptions<AuthorizationOptions>>()?.Value.FallbackPolicy is not null ||
               services.GetService<IHostEnvironment>()?.IsDevelopment() == true;
    }

    private static Task ForbidAsync(HttpContext context) =>
        Results.Problem(
            statusCode: StatusCodes.Status403Forbidden,
            title: "MonitorWay requires authorization",
            detail: "Outside the Development environment, call RequireAuthorization() on MapMonitorWayOtlp() " +
                    "or AllowAnonymous() for a receiver that only trusted networks reach.").ExecuteAsync(context);
}
