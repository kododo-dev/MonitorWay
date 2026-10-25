using System.ComponentModel;
using Kododo.MonitorWay.Core.Model;
using Kododo.MonitorWay.Errors;
using Kododo.MonitorWay.Memory;

namespace Kododo.MonitorWay.UI.DTO;

internal sealed record ErrorQueryDto(
    [property: Description("Search: words in the exception type, message, stack trace or operations; \"a phrase\"; -word excludes; " +
                           "type:<text>, service:<name>, endpoint:<operation> (* is a wildcard), trace:<id>.")] string? Text = null,
    [property: Description("Only groups that occurred at or after this moment (ISO 8601).")] DateTimeOffset? From = null,
    [property: Description("Only groups first seen at or before this moment (ISO 8601).")] DateTimeOffset? To = null)
{
    public ErrorQuery ToErrorQuery() => new(Text, From, To);
}

/// <param name="Trend">Occurrences per hour for the last 24 hours, the current hour last.</param>
internal sealed record ErrorRow(
    string Fingerprint,
    string Type,
    string Message,
    long Count,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string[] Services,
    string[] Endpoints,
    int[] Trend)
{
    public static ErrorRow From(ErrorGroupView view) => new(
        view.Group.Fingerprint, view.Group.Type, view.Group.Message, view.Group.Count, view.Group.FirstSeen,
        view.Group.LastSeen, view.Group.Services.ToArray(), view.Group.Endpoints.ToArray(), view.Trend);
}

internal sealed record ErrorsPage(ErrorRow[] Errors, string? Error = null);

internal sealed record OccurrenceDto(
    DateTimeOffset Timestamp,
    string Message,
    string? Service,
    string? Endpoint,
    string? TraceId,
    string? SpanId,
    long? LogEntryId,
    string? Category,
    bool FromSpan,
    bool FromLog)
{
    public static OccurrenceDto From(ErrorOccurrence o) => new(
        o.Timestamp, o.Message, o.ServiceName, o.Endpoint, o.TraceId, o.SpanId, o.LogEntryId, o.Category, o.FromSpan,
        o.LogEntryId is not null);
}

/// <param name="Frames">The frames the group is recognized by, top first.</param>
/// <param name="Trend">Occurrences per hour for the last 7 days, the current hour last.</param>
internal sealed record ErrorDetail(
    string Fingerprint,
    string Type,
    string Message,
    string? StackTrace,
    string[] Frames,
    long Count,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string[] Services,
    string[] Endpoints,
    int[] Trend,
    OccurrenceDto[] Occurrences)
{
    public static ErrorDetail From(ErrorGroupView view)
    {
        var g = view.Group;
        return new ErrorDetail(
            g.Fingerprint, g.Type, g.Message, g.StackTrace, g.Frames.ToArray(), g.Count, g.FirstSeen, g.LastSeen,
            g.Services.ToArray(), g.Endpoints.ToArray(), view.Trend, g.Occurrences.Select(OccurrenceDto.From).ToArray());
    }
}

/// <param name="Exported">Whether OpenTelemetry exports logs or traces to MonitorWay, which is where errors come from.</param>
internal sealed record ErrorStats(bool Exported, int Groups, long Occurrences, int MaxGroups, double? MaxAgeSeconds);

/// <summary>The fingerprint of an exception given as text, to link a log entry or a span event to its group.</summary>
internal static class ErrorLink
{
    public static string? Of(string? type, string? text, string? operation)
    {
        if (text is null && type is null)
            return null;
        type ??= TypeOf(text!);
        return type is null ? null : ErrorFingerprint.Compute(type, text, operation).Fingerprint;
    }

    /// <summary>The type from the first line of an exception's text, <c>System.TimeoutException: message</c>.</summary>
    private static string? TypeOf(string text)
    {
        var end = text.IndexOfAny([':', '\r', '\n']);
        var type = (end < 0 ? text : text[..end]).Trim();
        return type.Length > 0 && !type.Contains(' ') ? type : null;
    }
}
