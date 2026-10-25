using System.ComponentModel;
using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Memory;

namespace Kododo.MonitorWay.UI.DTO;

internal sealed record TraceQueryDto(
    [property: Description("Search: words must all occur in some span (name, service, attribute or event); \"a phrase\"; -word excludes; " +
                           "service:, name:, kind:server|client|internal; status:error or status:ok; duration:>500ms of the whole trace; " +
                           "has:error, has:exception, has:<attribute>; exception:<text>; trace:<id>; " +
                           "<attribute>:<value> (* is a wildcard), e.g. http.route:/orders/{id} or http.response.status_code:5*.")] string? Text = null,
    [property: Description("Only traces that started at or after this moment (ISO 8601).")] DateTimeOffset? From = null,
    [property: Description("Only traces that started at or before this moment (ISO 8601).")] DateTimeOffset? To = null)
{
    public TraceQuery ToTraceQuery() => new(Text, From, To);
}

/// <param name="Id">Position in the store, the cursor for paging (<c>before</c>).</param>
/// <param name="Name">Name of the root span, e.g. <c>GET /orders/{id}</c>.</param>
/// <param name="StatusCode">HTTP status code of the root span, when it is a request.</param>
internal sealed record TraceRow(
    long Id,
    string TraceId,
    DateTimeOffset Start,
    double DurationMs,
    string Name,
    string Kind,
    string? Service,
    string[] Services,
    int SpanCount,
    int ErrorCount,
    string? StatusCode)
{
    public static TraceRow From(TraceEntry trace) => new(
        trace.Id, trace.TraceId, trace.Start, trace.Duration.TotalMilliseconds, trace.Root.Name, trace.Root.Kind.ToString(),
        trace.Root.ServiceName, trace.Services.ToArray(), trace.Spans.Count, trace.ErrorCount,
        trace.Root.Attribute("http.response.status_code") ?? trace.Root.Attribute("http.status_code"));
}

/// <param name="LastId">Id of the newest stored trace, so a client can tell whether anything changed.</param>
internal sealed record TracesPage(TraceRow[] Traces, bool HasMore, string? Error = null, long LastId = 0);

/// <param name="ErrorFingerprint">For an <c>exception</c> event, the error group it belongs to.</param>
internal sealed record SpanEventDto(string Name, double OffsetMs, DateTimeOffset Timestamp, Property[] Attributes, string? ErrorFingerprint = null);

internal sealed record SpanLinkDto(string TraceId, string SpanId);

/// <param name="OffsetMs">Start of the span from the start of the trace, in milliseconds.</param>
internal sealed record SpanDto(
    string SpanId,
    string? ParentSpanId,
    string Name,
    string Kind,
    DateTimeOffset Start,
    double OffsetMs,
    double DurationMs,
    string Status,
    string? StatusDescription,
    string? Service,
    string? Scope,
    Property[] Attributes,
    SpanEventDto[] Events,
    SpanLinkDto[] Links);

/// <param name="IsComplete"><c>false</c> while spans of the trace are still being received.</param>
/// <param name="DroppedSpans">Spans left out because the trace had more than the store keeps.</param>
internal sealed record TraceDetail(
    string TraceId,
    bool IsComplete,
    int DroppedSpans,
    DateTimeOffset Start,
    double DurationMs,
    SpanDto[] Spans)
{
    public static TraceDetail From(TraceEntry trace) => new(
        trace.TraceId, trace.IsComplete, trace.DroppedSpans, trace.Start, trace.Duration.TotalMilliseconds,
        trace.Spans.Select(s => new SpanDto(
            s.SpanId, s.ParentSpanId, s.Name, s.Kind.ToString(), s.Start, (s.Start - trace.Start).TotalMilliseconds,
            s.Duration.TotalMilliseconds, s.Status.ToString(), s.StatusDescription, s.ServiceName, s.ScopeName,
            Properties(s.Attributes),
            s.Events.Select(e => new SpanEventDto(
                e.Name, (e.Timestamp - trace.Start).TotalMilliseconds, e.Timestamp, Properties(e.Attributes),
                e.Name == "exception" ? ExceptionLink(e, trace.Root.Name) : null)).ToArray(),
            s.Links.Select(l => new SpanLinkDto(l.TraceId, l.SpanId)).ToArray())).ToArray());

    private static string? ExceptionLink(SpanEvent e, string operation)
    {
        string? Attribute(string key) => e.Attributes.FirstOrDefault(a => a.Key == key).Value;
        var type = Attribute("exception.type") ?? "Exception";
        return ErrorLink.Of(type, Attribute("exception.stacktrace") ?? $"{type}: {Attribute("exception.message")}", operation);
    }

    private static Property[] Properties(IReadOnlyList<KeyValuePair<string, string?>> attributes) =>
        attributes.Select(a => new Property(a.Key, a.Value)).ToArray();
}

internal sealed record TracePool(
    string Name,
    int Count,
    long Bytes,
    DateTimeOffset? Oldest,
    long Evicted,
    int? MaxTraces,
    long? MaxBytes,
    double? MaxAgeSeconds)
{
    public static TracePool From(TracePoolStats stats) => new(
        stats.Name, stats.Count, stats.Bytes, stats.Oldest, stats.Evicted, stats.MaxTraces, stats.MaxBytes,
        stats.MaxAge?.TotalSeconds);
}

/// <param name="Exported">Whether the OpenTelemetry SDK exports traces to MonitorWay.</param>
/// <param name="Pending">Traces still being received.</param>
internal sealed record TraceStats(
    bool Exported,
    TracePool[] Pools,
    int Pending,
    long TotalBytes,
    long MaxTotalBytes,
    double SlowThresholdMs,
    long LastId,
    DateTimeOffset Now);
