using System.Globalization;
using Kododo.MonitorWay.Core.Model;

namespace Kododo.MonitorWay.Querying;

/// <summary>
/// A <see cref="TraceQuery"/> prepared for evaluating many traces. The text uses the syntax of the log search
/// (<see cref="SearchExpression"/>); a term matches a trace when it matches any of its spans.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>timeout</c>, <c>"order 1008"</c>: text in a span name, service, status description, attribute or event.</item>
/// <item><c>service:shop</c>, <c>name:GET*</c>, <c>kind:client</c>: a span of the service, with the name or of the kind.</item>
/// <item><c>status:error</c>: the trace has a failed span; <c>status:ok</c>: it has none.</item>
/// <item><c>duration:&gt;500ms</c>, <c>duration:&lt;=2s</c>: the whole trace; a bare value means at least.</item>
/// <item><c>has:error</c>, <c>has:exception</c>, <c>has:db.system</c>: a failed span, an exception event, an attribute.</item>
/// <item><c>exception:Timeout</c>: text in an exception event; <c>trace:</c> the trace id.</item>
/// <item><c>http.route:/orders/*</c>: an attribute of some span equals the value; <c>*</c> matches any text.</item>
/// </list>
/// </remarks>
internal sealed class TraceQueryMatcher
{
    private readonly List<(Func<TraceEntry, bool> Predicate, bool Negate)> _terms = [];
    private readonly DateTimeOffset? _from;
    private readonly DateTimeOffset? _to;

    private TraceQueryMatcher(TraceQuery query)
    {
        _from = query.From;
        _to = query.To;
        if (string.IsNullOrWhiteSpace(query.Text))
            return;
        foreach (var term in SearchExpression.Parse(query.Text).Terms)
            _terms.Add((Compile(term), term.Negate));
    }

    public static TraceQueryMatcher All { get; } = new(TraceQuery.All);

    /// <exception cref="ArgumentException">A term cannot be used, e.g. a duration that is not one.</exception>
    public static TraceQueryMatcher Create(TraceQuery query) => new(query);

    public bool Matches(TraceEntry trace)
    {
        if (_from is { } from && trace.Start < from || _to is { } to && trace.Start > to)
            return false;
        foreach (var (predicate, negate) in _terms)
        {
            if (predicate(trace) == negate)
                return false;
        }
        return true;
    }

    private static Func<TraceEntry, bool> Compile(SearchTerm term)
    {
        var value = term.Value;
        switch (term.Kind)
        {
            case SearchTermKind.Text:
                return t => t.Spans.Any(s => AnyText(s, value));
            case SearchTermKind.Message:
                return t => t.Spans.Any(s => s.Name.Contains(value, StringComparison.OrdinalIgnoreCase));
            case SearchTermKind.Category:
                return t => t.Spans.Any(s => s.ScopeName?.Contains(value, StringComparison.OrdinalIgnoreCase) == true);
            case SearchTermKind.Exception:
                return t => t.Spans.Any(s => s.Events.Any(e => e.Name == "exception" &&
                    e.Attributes.Any(a => a.Value?.Contains(value, StringComparison.OrdinalIgnoreCase) == true)));
            case SearchTermKind.Trace:
                return t => t.TraceId.StartsWith(value, StringComparison.OrdinalIgnoreCase);
            case SearchTermKind.Service:
                return t => t.Spans.Any(s => s.ServiceName is { } name && LogQueryMatcher.Wildcard(name, value));
            case SearchTermKind.Has:
                return value.ToLowerInvariant() switch
                {
                    "error" => t => t.HasError,
                    "exception" => t => t.Spans.Any(s => s.Events.Any(e => e.Name == "exception")),
                    _ => t => t.Spans.Any(s => s.Attributes.Any(a => a.Key.Equals(value, StringComparison.OrdinalIgnoreCase))),
                };
        }

        switch (term.Field!.ToLowerInvariant())
        {
            case "service":
                return t => t.Spans.Any(s => s.ServiceName is { } name && LogQueryMatcher.Wildcard(name, value));
            case "name" or "span" or "operation":
                return t => t.Spans.Any(s => LogQueryMatcher.Wildcard(s.Name, value));
            case "kind":
                var kind = Enum.TryParse<TraceSpanKind>(value, ignoreCase: true, out var parsedKind)
                    ? parsedKind
                    : throw new ArgumentException($"'{value}' is not a span kind: use server, client, internal, producer or consumer.");
                return t => t.Spans.Any(s => s.Kind == kind);
            case "status":
                return value.ToLowerInvariant() switch
                {
                    "error" => t => t.HasError,
                    "ok" or "unset" => t => !t.HasError,
                    _ => throw new ArgumentException($"'{value}' is not a status: use status:error or status:ok."),
                };
            case "duration":
                var (compare, limit) = ParseDurationFilter(value);
                return t => compare(t.Duration.CompareTo(limit));
            default:
                var key = term.Field;
                return t => t.Spans.Any(s => s.Attributes.Any(a =>
                    a.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && a.Value is not null && LogQueryMatcher.Wildcard(a.Value, value)));
        }
    }

    private static bool AnyText(TraceSpan span, string value)
    {
        bool Has(string? text) => text?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;

        if (Has(span.Name) || Has(span.ServiceName) || Has(span.StatusDescription))
            return true;
        foreach (var attribute in span.Attributes)
        {
            if (Has(attribute.Value))
                return true;
        }
        foreach (var e in span.Events)
        {
            if (Has(e.Name) || e.Attributes.Any(a => Has(a.Value)))
                return true;
        }
        return false;
    }

    /// <summary>
    /// <c>&gt;500ms</c>, <c>&gt;=1.5s</c>, <c>&lt;200ms</c>, <c>&lt;=2m</c> or a bare <c>1s</c>, which means at least.
    /// Units: <c>us</c>, <c>ms</c>, <c>s</c>, <c>m</c> (or <c>min</c>); without one, milliseconds.
    /// </summary>
    internal static (Func<int, bool> Compare, TimeSpan Limit) ParseDurationFilter(string text)
    {
        var (compare, rest) = text switch
        {
            _ when text.StartsWith(">=", StringComparison.Ordinal) => ((Func<int, bool>)(c => c >= 0), text[2..]),
            _ when text.StartsWith("<=", StringComparison.Ordinal) => (c => c <= 0, text[2..]),
            _ when text.StartsWith('>') => (c => c > 0, text[1..]),
            _ when text.StartsWith('<') => (c => c < 0, text[1..]),
            _ => (c => c >= 0, text),
        };
        return (compare, ParseDuration(rest) ?? throw new ArgumentException(
            $"'{text}' is not a duration: use e.g. duration:>500ms, duration:<=2s or duration:1.5s."));
    }

    private static TimeSpan? ParseDuration(string text)
    {
        var unitStart = text.Length;
        while (unitStart > 0 && char.IsLetter(text[unitStart - 1]))
            unitStart--;
        if (!double.TryParse(text[..unitStart], NumberStyles.Float, CultureInfo.InvariantCulture, out var number) || number < 0)
            return null;

        return text[unitStart..].ToLowerInvariant() switch
        {
            "us" or "µs" => TimeSpan.FromTicks((long)(number * 10)),
            "" or "ms" => TimeSpan.FromTicks((long)(number * TimeSpan.TicksPerMillisecond)),
            "s" => TimeSpan.FromTicks((long)(number * TimeSpan.TicksPerSecond)),
            "m" or "min" => TimeSpan.FromTicks((long)(number * TimeSpan.TicksPerMinute)),
            _ => null,
        };
    }
}
