using System.Text.RegularExpressions;
using Kododo.MonitorWay.Core.Model;
using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.Querying;

/// <summary>
/// A <see cref="LogQuery"/> prepared for evaluating many entries.
/// </summary>
/// <remarks>
/// The text is a <see cref="SearchExpression"/> (terms, phrases, exclusions, field filters) unless the query is
/// a regular expression. Regular expressions come from users, so they run with
/// <see cref="RegexOptions.NonBacktracking"/>, which has no catastrophic backtracking, plus a timeout as a
/// second line of defence.
/// </remarks>
internal sealed class LogQueryMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);

    private readonly bool[]? _levels;
    private readonly IReadOnlyList<SearchTerm> _terms = [];
    private readonly StringComparison _comparison;
    private readonly Regex? _regex;
    private readonly DateTimeOffset? _from;
    private readonly DateTimeOffset? _to;
    private readonly HashSet<string>? _categories;
    private readonly HashSet<string>? _excludedCategories;

    private LogQueryMatcher(LogQuery query)
    {
        if (query.Levels is { Count: > 0 } levels)
        {
            _levels = new bool[(int)LogLevel.None + 1];
            foreach (var level in levels)
                _levels[(int)level] = true;
        }

        _comparison = query.CaseSensitive ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        if (!string.IsNullOrWhiteSpace(query.Text))
        {
            if (query.Regex)
            {
                var options = RegexOptions.NonBacktracking | RegexOptions.CultureInvariant;
                if (!query.CaseSensitive)
                    options |= RegexOptions.IgnoreCase;
                _regex = new Regex(query.Text, options, RegexTimeout);
            }
            else
            {
                _terms = SearchExpression.Parse(query.Text).Terms;
                _requiredWords = RequiredWords(_terms);
            }
        }

        _from = query.From;
        _to = query.To;
        if (query.Categories is { Count: > 0 } categories)
            _categories = new HashSet<string>(categories, StringComparer.OrdinalIgnoreCase);
        if (query.ExcludedCategories is { Count: > 0 } excluded)
            _excludedCategories = new HashSet<string>(excluded, StringComparer.OrdinalIgnoreCase);
    }

    private readonly byte[][] _requiredWords = [];

    /// <summary>
    /// ASCII words that every matching entry contains somewhere in its raw text, whatever the file format.
    /// </summary>
    /// <remarks>
    /// Words, not phrases: a CLEF message is often a template with the values stored as separate properties,
    /// so a phrase appears only after rendering, but each of its words is in the raw line. Only ASCII letters and
    /// digits are used, which JSON never escapes. Negated terms and <c>has:</c> filters say nothing about the
    /// raw text and are left out.
    /// </remarks>
    private static byte[][] RequiredWords(IReadOnlyList<SearchTerm> terms)
    {
        var words = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var term in terms)
        {
            if (term.Negate || term.Kind == SearchTermKind.Has)
                continue;
            foreach (var word in term.Value.Split(NonWordCharacters, StringSplitOptions.RemoveEmptyEntries))
            {
                if (word.Length >= 2 && word.All(char.IsAsciiLetterOrDigit))
                    words.Add(word);
            }
        }
        return words.Select(w => System.Text.Encoding.ASCII.GetBytes(w)).ToArray();
    }

    private static readonly char[] NonWordCharacters =
        Enumerable.Range(0, 128).Select(c => (char)c).Where(c => !char.IsAsciiLetterOrDigit(c)).ToArray();

    /// <summary>
    /// Whether raw text may contain a matching entry. <c>false</c> means it certainly does not, so a whole range of
    /// a file can be skipped without parsing it. Case is ignored, because the case of the query applies to the
    /// parsed fields.
    /// </summary>
    public bool RawTextMayMatch(ReadOnlySpan<byte> raw)
    {
        foreach (var word in _requiredWords)
        {
            if (!ContainsIgnoreCase(raw, word))
                return false;
        }
        return true;
    }

    private static bool ContainsIgnoreCase(ReadOnlySpan<byte> text, ReadOnlySpan<byte> word)
    {
        var lower = (byte)char.ToLowerInvariant((char)word[0]);
        var upper = (byte)char.ToUpperInvariant((char)word[0]);
        var rest = word[1..];
        while (text.Length >= word.Length)
        {
            var index = text.IndexOfAny(lower, upper);
            if (index < 0 || text.Length - index < word.Length)
                return false;
            if (System.Text.Ascii.EqualsIgnoreCase(text.Slice(index + 1, rest.Length), rest))
                return true;
            text = text[(index + 1)..];
        }
        return false;
    }

    public static LogQueryMatcher All { get; } = new(LogQuery.All);

    /// <summary>
    /// Prepares <paramref name="query"/>.
    /// </summary>
    /// <exception cref="ArgumentException">The query text is not a valid regular expression.</exception>
    public static LogQueryMatcher Create(LogQuery query) => new(query);

    /// <summary>The earliest time a matching entry can have, for skipping parts of a file by their time range.</summary>
    public DateTimeOffset? From => Later(_from, _boundFrom);

    /// <summary>The latest time a matching entry can have.</summary>
    public DateTimeOffset? To => Earlier(_to, _boundTo);

    private DateTimeOffset? _boundFrom;
    private DateTimeOffset? _boundTo;

    /// <summary>
    /// This query limited to entries written within <paramref name="from"/> and <paramref name="to"/> (inclusive).
    /// Unlike the query's own range, the limit keeps entries without a time: a merged source uses it to read the part
    /// of a member next to a moment, and an entry without a time belongs where it is in its file.
    /// </summary>
    public LogQueryMatcher Within(DateTimeOffset? from, DateTimeOffset? to)
    {
        var bounded = (LogQueryMatcher)MemberwiseClone();
        bounded._boundFrom = Later(_boundFrom, from);
        bounded._boundTo = Earlier(_boundTo, to);
        return bounded;
    }

    private static DateTimeOffset? Later(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null || a >= b ? a : b;
    private static DateTimeOffset? Earlier(DateTimeOffset? a, DateTimeOffset? b) => a is null ? b : b is null || a <= b ? a : b;

    /// <summary>Whether entries can be matched only after parsing their text (the search or the categories).</summary>
    public bool HasText => _terms.Count > 0 || _regex is not null || _categories is not null || _excludedCategories is not null;

    /// <summary>Whether the query restricts levels and <paramref name="level"/> is excluded.</summary>
    public bool ExcludesLevel(LogLevel level) => _levels is not null && !_levels[(int)level];

    /// <summary>Whether the query has a time range and <paramref name="timestamp"/> is outside of it.</summary>
    public bool ExcludesTime(DateTimeOffset? timestamp) =>
        (_from is { } from && !(timestamp >= from)) || (_to is { } to && !(timestamp <= to))
        || timestamp is { } t && (t < _boundFrom || t > _boundTo);

    public bool Matches(LogEntry entry)
    {
        if (ExcludesLevel(entry.Level) || ExcludesTime(entry.Timestamp) || ExcludesCategory(entry.Category))
            return false;
        if (_regex is not null)
            return AnyText(entry, value => MatchesRegex(value));

        foreach (var term in _terms)
        {
            if (MatchesTerm(entry, term) == term.Negate)
                return false;
        }
        return true;
    }

    // An entry without a category is left out only when the query names the categories to include.
    private bool ExcludesCategory(string? category) =>
        category is null
            ? _categories is not null
            : _categories?.Contains(category) == false || _excludedCategories?.Contains(category) == true;

    private bool MatchesTerm(LogEntry entry, SearchTerm term) => term.Kind switch
    {
        SearchTermKind.Text => AnyText(entry, value => value.Contains(term.Value, _comparison)),
        SearchTermKind.Message => entry.Message.Contains(term.Value, _comparison),
        SearchTermKind.Category => entry.Category?.Contains(term.Value, StringComparison.OrdinalIgnoreCase) == true,
        SearchTermKind.Exception => entry.Exception?.Contains(term.Value, _comparison) == true,
        SearchTermKind.Has => term.Value.Equals("exception", StringComparison.OrdinalIgnoreCase)
            ? entry.Exception is not null
            : entry.Properties.Any(p => p.Key.Equals(term.Value, StringComparison.OrdinalIgnoreCase)),
        SearchTermKind.Service => entry.ServiceName is { } service && Wildcard(service, term.Value),
        SearchTermKind.Trace => string.Equals(entry.TraceId, term.Value, StringComparison.OrdinalIgnoreCase)
            || entry.Properties.Any(p => p.Key == "TraceId" && string.Equals(p.Value, term.Value, StringComparison.OrdinalIgnoreCase)),
        _ => entry.Properties.Any(p =>
            p.Key.Equals(term.Field, StringComparison.OrdinalIgnoreCase) && p.Value is not null && Wildcard(p.Value, term.Value)),
    };

    private static bool AnyText(LogEntry entry, Func<string, bool> predicate)
    {
        if (predicate(entry.Message) || entry.Category is { } c && predicate(c) || entry.Exception is { } e && predicate(e))
            return true;
        foreach (var property in entry.Properties)
        {
            if (property.Value is { } value && predicate(value))
                return true;
        }
        return false;
    }

    /// <summary>Case-insensitive equality where <c>*</c> stands for any text.</summary>
    internal static bool Wildcard(string value, string pattern)
    {
        if (!pattern.Contains('*'))
            return value.Equals(pattern, StringComparison.OrdinalIgnoreCase);

        var parts = pattern.Split('*');
        var position = 0;
        for (var i = 0; i < parts.Length; i++)
        {
            var part = parts[i];
            if (part.Length == 0)
                continue;
            if (i == 0)
            {
                if (!value.StartsWith(part, StringComparison.OrdinalIgnoreCase))
                    return false;
                position = part.Length;
                continue;
            }
            if (i == parts.Length - 1)
                return value.Length - part.Length >= position && value.EndsWith(part, StringComparison.OrdinalIgnoreCase);

            var found = value.IndexOf(part, position, StringComparison.OrdinalIgnoreCase);
            if (found < 0)
                return false;
            position = found + part.Length;
        }
        return true;
    }

    private bool MatchesRegex(string value)
    {
        try
        {
            return _regex!.IsMatch(value);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }
}
