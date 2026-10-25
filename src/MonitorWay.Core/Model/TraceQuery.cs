namespace Kododo.MonitorWay.Core.Model;

/// <summary>
/// Filter applied to traces. Every criterion that is set must match.
/// </summary>
/// <param name="Text">
/// Search terms, all of which must match some span of the trace: words in span names, services and attribute values;
/// <c>service:</c>, <c>name:</c>, <c>kind:</c>, <c>status:error</c>, <c>duration:&gt;500ms</c> (of the whole trace),
/// <c>trace:</c> and <c>attribute.name:value</c> with <c>*</c> as a wildcard.
/// </param>
/// <param name="From">Include traces that started at or after this time.</param>
/// <param name="To">Include traces that started at or before this time.</param>
public sealed record TraceQuery(
    string? Text = null,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null)
{
    /// <summary>A query that matches every trace.</summary>
    public static TraceQuery All { get; } = new();
}
