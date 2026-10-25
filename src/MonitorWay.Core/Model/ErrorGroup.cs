namespace Kododo.MonitorWay.Core.Model;

/// <summary>
/// One time an exception happened. When both a log entry and a span event of the same trace reported it, it is one
/// occurrence with both.
/// </summary>
/// <param name="Timestamp">When it happened (the earlier of the two reports).</param>
/// <param name="Message">The exception's message, which often differs between occurrences (ids, paths).</param>
/// <param name="ServiceName">The service it happened in.</param>
/// <param name="Endpoint">The operation the application was busy with: the name of the local root span, e.g. <c>POST /orders</c>.</param>
/// <param name="TraceId">The trace it happened in.</param>
/// <param name="SpanId">The span that recorded it, or the span the log entry was written in.</param>
/// <param name="LogEntryId">Id of the log entry in the memory buffer, when a log reported it.</param>
/// <param name="Category">Category of the logger, when a log reported it.</param>
/// <param name="FromSpan">An <c>exception</c> event of a span reported it.</param>
public sealed record ErrorOccurrence(
    DateTimeOffset Timestamp,
    string Message,
    string? ServiceName,
    string? Endpoint,
    string? TraceId,
    string? SpanId,
    long? LogEntryId,
    string? Category,
    bool FromSpan);

/// <summary>
/// Exceptions grouped by their fingerprint: the same type thrown from the same place in the application's code.
/// </summary>
/// <param name="Fingerprint">Identifies the group; stable across restarts.</param>
/// <param name="Type">Full name of the exception's type.</param>
/// <param name="Message">Message of the latest occurrence.</param>
/// <param name="StackTrace">The exception as text (type, message, stack trace) of the latest occurrence.</param>
/// <param name="Frames">The frames the fingerprint was made of, top first, e.g. <c>Shop.Payments.Gateway.Charge</c>.</param>
/// <param name="Count">Occurrences since the group was first seen, including those no longer kept.</param>
/// <param name="FirstSeen">The first occurrence.</param>
/// <param name="LastSeen">The latest occurrence.</param>
/// <param name="Services">Services it happened in.</param>
/// <param name="Endpoints">Operations it happened in, most frequent first.</param>
/// <param name="Occurrences">The latest occurrences, newest first.</param>
public sealed record ErrorGroup(
    string Fingerprint,
    string Type,
    string Message,
    string? StackTrace,
    IReadOnlyList<string> Frames,
    long Count,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    IReadOnlyList<string> Services,
    IReadOnlyList<string> Endpoints,
    IReadOnlyList<ErrorOccurrence> Occurrences);

/// <summary>Filter applied to error groups. Every criterion that is set must match.</summary>
/// <param name="Text">
/// Words in the type, message, stack trace or operations; <c>type:</c>, <c>service:</c>, <c>endpoint:</c> and
/// <c>trace:</c> narrow to one field, <c>-word</c> excludes.
/// </param>
/// <param name="From">Groups with an occurrence at or after this time.</param>
/// <param name="To">Groups first seen at or before this time.</param>
public sealed record ErrorQuery(string? Text = null, DateTimeOffset? From = null, DateTimeOffset? To = null)
{
    /// <summary>A query that matches every group.</summary>
    public static ErrorQuery All { get; } = new();
}
