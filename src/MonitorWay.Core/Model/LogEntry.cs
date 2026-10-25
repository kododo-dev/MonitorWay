using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.Core.Model;

/// <summary>
/// A single log entry as presented by MonitorWay, regardless of the source it was read from.
/// </summary>
/// <param name="Id">
/// Position of the entry within its source. For the in-memory buffer this is a global,
/// monotonically increasing sequence number; for files it is the ordinal of the entry.
/// </param>
/// <param name="Timestamp">When the entry was written, if known.</param>
/// <param name="Level">Severity, normalized to <see cref="LogLevel"/>. <see cref="LogLevel.None"/> means unknown.</param>
/// <param name="Category">Logger category, usually the full name of the logging type.</param>
/// <param name="Message">The rendered message.</param>
/// <param name="MessageTemplate">The message template, when the entry was logged with one.</param>
/// <param name="Exception">The exception text including the stack trace, if any.</param>
/// <param name="Properties">Structured properties and scope values, already converted to strings.</param>
/// <param name="TraceId">W3C trace id of the activity the entry was logged in.</param>
/// <param name="SpanId">W3C span id of the activity the entry was logged in.</param>
/// <param name="ExceptionType">Full name of the exception's type, when known (from OpenTelemetry, not from files).</param>
/// <param name="ServiceName">The service that logged the entry: <c>service.name</c> of its OpenTelemetry resource.</param>
public sealed record LogEntry(
    long Id,
    DateTimeOffset? Timestamp,
    LogLevel Level,
    string? Category,
    string Message,
    string? MessageTemplate,
    string? Exception,
    IReadOnlyList<KeyValuePair<string, string?>> Properties,
    string? TraceId,
    string? SpanId,
    string? ExceptionType = null,
    string? ServiceName = null);
