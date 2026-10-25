using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.Core.Model;

/// <summary>
/// Filter applied to log entries. Every criterion that is set must match.
/// </summary>
/// <param name="Levels">Levels to include. <c>null</c> or empty includes all levels.</param>
/// <param name="Text">
/// Text searched in the message, category, exception and property values.
/// Treated as a regular expression when <paramref name="Regex"/> is set.
/// </param>
/// <param name="Regex">Interpret <paramref name="Text"/> as a regular expression.</param>
/// <param name="CaseSensitive">Match <paramref name="Text"/> case-sensitively.</param>
/// <param name="From">Include entries written at or after this time.</param>
/// <param name="To">Include entries written at or before this time.</param>
/// <param name="Categories">
/// Categories to include, compared whole and ignoring case. <c>null</c> or empty includes all categories.
/// </param>
/// <param name="ExcludedCategories">Categories to leave out, compared whole and ignoring case.</param>
public sealed record LogQuery(
    IReadOnlyList<LogLevel>? Levels = null,
    string? Text = null,
    bool Regex = false,
    bool CaseSensitive = false,
    DateTimeOffset? From = null,
    DateTimeOffset? To = null,
    IReadOnlyList<string>? Categories = null,
    IReadOnlyList<string>? ExcludedCategories = null)
{
    /// <summary>A query that matches every entry.</summary>
    public static LogQuery All { get; } = new();
}
