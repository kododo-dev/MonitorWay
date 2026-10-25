namespace Kododo.MonitorWay.Core.Model;

/// <summary>What a span stands for in its trace, as in OpenTelemetry.</summary>
public enum TraceSpanKind
{
    /// <summary>An operation inside the application.</summary>
    Internal,

    /// <summary>The handling of a request from another service, e.g. an incoming HTTP request.</summary>
    Server,

    /// <summary>A request to another service, e.g. an outgoing HTTP call or a database query.</summary>
    Client,

    /// <summary>A message sent to a broker or queue.</summary>
    Producer,

    /// <summary>A message received from a broker or queue.</summary>
    Consumer,
}

/// <summary>The outcome of a span, as in OpenTelemetry.</summary>
public enum TraceSpanStatus
{
    /// <summary>The instrumentation did not set a status, which counts as success.</summary>
    Unset,

    /// <summary>Marked as successful.</summary>
    Ok,

    /// <summary>The operation failed.</summary>
    Error,
}

/// <summary>A moment within a span, e.g. an exception (<c>exception</c> with <c>exception.*</c> attributes).</summary>
public sealed record SpanEvent(
    string Name,
    DateTimeOffset Timestamp,
    IReadOnlyList<KeyValuePair<string, string?>> Attributes);

/// <summary>A span of another trace this one is related to, e.g. the message that started it.</summary>
public sealed record SpanLink(string TraceId, string SpanId);

/// <summary>
/// One operation of a trace: an incoming request, a database query, a call to another service.
/// </summary>
/// <param name="TraceId">W3C trace id, 32 hexadecimal digits.</param>
/// <param name="SpanId">W3C span id, 16 hexadecimal digits.</param>
/// <param name="ParentSpanId">The span this one runs in; <c>null</c> for the root of the trace.</param>
/// <param name="Name">Operation name, e.g. <c>GET /orders/{id}</c>.</param>
/// <param name="Kind">What the span stands for.</param>
/// <param name="Start">When the operation started.</param>
/// <param name="Duration">How long it took.</param>
/// <param name="Status">The outcome.</param>
/// <param name="StatusDescription">Why it failed, when the instrumentation says.</param>
/// <param name="ServiceName">The service that recorded the span: <c>service.name</c> of its OpenTelemetry resource.</param>
/// <param name="ScopeName">The instrumentation that recorded the span (the name of its <c>ActivitySource</c>).</param>
/// <param name="Attributes">Attributes of the span, already converted to strings.</param>
/// <param name="Events">Events of the span, oldest first.</param>
/// <param name="Links">Links to spans of other traces.</param>
/// <param name="HasRemoteParent">The parent is in another process, so within this application the span is a root.</param>
public sealed record TraceSpan(
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    string Name,
    TraceSpanKind Kind,
    DateTimeOffset Start,
    TimeSpan Duration,
    TraceSpanStatus Status,
    string? StatusDescription,
    string? ServiceName,
    string? ScopeName,
    IReadOnlyList<KeyValuePair<string, string?>> Attributes,
    IReadOnlyList<SpanEvent> Events,
    IReadOnlyList<SpanLink> Links,
    bool HasRemoteParent = false)
{
    /// <summary>When the operation ended.</summary>
    public DateTimeOffset End => Start + Duration;

    /// <summary>The value of an attribute, or <c>null</c> when the span does not have it.</summary>
    public string? Attribute(string key)
    {
        foreach (var (name, value) in Attributes)
        {
            if (name == key)
                return value;
        }
        return null;
    }
}
