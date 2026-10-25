using Kododo.MonitorWay.Core.Model;

namespace Kododo.MonitorWay.Querying;

/// <summary>
/// An <see cref="ErrorQuery"/> prepared for evaluating many groups, with the syntax of the log search
/// (<see cref="SearchExpression"/>).
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>timeout</c>, <c>"did not answer"</c>: text in the type, message, stack trace or operations.</item>
/// <item><c>type:Timeout</c> (or <c>exception:</c>): the type contains the text.</item>
/// <item><c>service:shop</c>, <c>endpoint:POST*</c>: a service or operation it happened in; <c>*</c> matches any text.</item>
/// <item><c>trace:</c>: one of the kept occurrences was in the trace.</item>
/// </list>
/// </remarks>
internal sealed class ErrorQueryMatcher
{
    private readonly List<(Func<ErrorGroup, bool> Predicate, bool Negate)> _terms = [];
    private readonly DateTimeOffset? _from;
    private readonly DateTimeOffset? _to;

    private ErrorQueryMatcher(ErrorQuery query)
    {
        _from = query.From;
        _to = query.To;
        if (string.IsNullOrWhiteSpace(query.Text))
            return;
        foreach (var term in SearchExpression.Parse(query.Text).Terms)
            _terms.Add((Compile(term), term.Negate));
    }

    public static ErrorQueryMatcher All { get; } = new(ErrorQuery.All);

    /// <exception cref="ArgumentException">A term cannot be used.</exception>
    public static ErrorQueryMatcher Create(ErrorQuery query) => new(query);

    /// <summary>Whether the query looks at the occurrences, which a list of groups otherwise does not copy.</summary>
    public bool NeedsOccurrences { get; private set; }

    public bool Matches(ErrorGroup group)
    {
        if (_from is { } from && group.LastSeen < from || _to is { } to && group.FirstSeen > to)
            return false;
        foreach (var (predicate, negate) in _terms)
        {
            if (predicate(group) == negate)
                return false;
        }
        return true;
    }

    private Func<ErrorGroup, bool> Compile(SearchTerm term)
    {
        var value = term.Value;
        bool Has(string? text) => text?.Contains(value, StringComparison.OrdinalIgnoreCase) == true;

        switch (term.Kind)
        {
            case SearchTermKind.Text or SearchTermKind.Message:
                return g => Has(g.Type) || Has(g.Message) || Has(g.StackTrace) || g.Endpoints.Any(Has);
            case SearchTermKind.Exception:
                return g => Has(g.Type);
            case SearchTermKind.Category:
                return g => g.Frames.Any(Has);
            case SearchTermKind.Trace:
                NeedsOccurrences = true;
                return g => g.Occurrences.Any(o => o.TraceId?.StartsWith(value, StringComparison.OrdinalIgnoreCase) == true);
            case SearchTermKind.Has:
                throw new ArgumentException($"'has:{value}' does not apply to errors.");
            case SearchTermKind.Service:
                return g => g.Services.Any(s => LogQueryMatcher.Wildcard(s, value));
        }

        return term.Field!.ToLowerInvariant() switch
        {
            "type" => g => Has(g.Type),
            "service" => g => g.Services.Any(s => LogQueryMatcher.Wildcard(s, value)),
            "endpoint" or "name" or "operation" => g => g.Endpoints.Any(e => LogQueryMatcher.Wildcard(e, value)),
            "fingerprint" or "id" => g => g.Fingerprint.StartsWith(value, StringComparison.OrdinalIgnoreCase),
            _ => throw new ArgumentException($"'{term.Field}:' does not apply to errors: use type:, service:, endpoint: or trace:."),
        };
    }
}
