using System.Text;

namespace Kododo.MonitorWay.Querying;

/// <summary>
/// The search text split into terms. All terms must match.
/// </summary>
/// <remarks>
/// <list type="bullet">
/// <item><c>timeout</c>, <c>"order 1008"</c>: text in the message, category, exception or a property value.</item>
/// <item><c>-health</c>: any term can be negated with a leading minus.</item>
/// <item><c>message:</c>, <c>category:</c>, <c>exception:</c>: text in that field only.</item>
/// <item><c>has:exception</c>, <c>has:OrderId</c>: the entry has an exception or the property.</item>
/// <item><c>trace:abc</c>: the entry belongs to the trace.</item>
/// <item><c>service:shop</c>: the entry comes from the service (<c>*</c> matches any text).</item>
/// <item><c>OrderId:1008</c>, <c>CustomerId:c-6*</c>: a property equals the value; <c>*</c> matches any text.</item>
/// </list>
/// A token that looks like <c>name:value</c> but contains <c>://</c> is plain text, so URLs can be searched for.
/// </remarks>
internal sealed record SearchExpression(IReadOnlyList<SearchTerm> Terms)
{
    public static SearchExpression Parse(string text)
    {
        var terms = new List<SearchTerm>();
        foreach (var token in Tokenize(text))
        {
            var negate = token.Raw.StartsWith('-') && token.Raw.Length > 1 && !token.Quoted;
            var raw = negate ? token.Raw[1..] : token.Raw;

            var colon = token.Quoted ? -1 : raw.IndexOf(':');
            if (colon > 0 && colon < raw.Length - 1 && !raw.Contains("://", StringComparison.Ordinal) && IsFieldName(raw.AsSpan(0, colon)))
            {
                var field = raw[..colon];
                var value = Unquote(raw[(colon + 1)..]);
                terms.Add(new SearchTerm(KindOf(field), field, value, negate));
            }
            else
            {
                terms.Add(new SearchTerm(SearchTermKind.Text, null, token.Quoted ? raw : Unquote(raw), negate));
            }
        }
        return new SearchExpression(terms);
    }

    private static SearchTermKind KindOf(string field) => field.ToLowerInvariant() switch
    {
        "message" or "msg" => SearchTermKind.Message,
        "category" or "cat" => SearchTermKind.Category,
        "exception" or "ex" => SearchTermKind.Exception,
        "has" => SearchTermKind.Has,
        "trace" or "traceid" => SearchTermKind.Trace,
        "service" => SearchTermKind.Service,
        _ => SearchTermKind.Property,
    };

    private static bool IsFieldName(ReadOnlySpan<char> name)
    {
        if (!char.IsLetter(name[0]) && name[0] != '_' && name[0] != '@')
            return false;
        foreach (var c in name)
        {
            if (!char.IsLetterOrDigit(c) && c is not ('_' or '.' or '@' or '-'))
                return false;
        }
        return true;
    }

    private static string Unquote(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    /// <summary>Splits on whitespace outside quotes. A token that is entirely quoted is a phrase.</summary>
    private static IEnumerable<(string Raw, bool Quoted)> Tokenize(string text)
    {
        var current = new StringBuilder();
        var inQuotes = false;
        var quotedWhole = false;
        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : ' ';
            if (c == '"')
            {
                if (!inQuotes && current.Length == 0)
                    quotedWhole = true;
                inQuotes = !inQuotes;
                if (!quotedWhole)
                    current.Append(c);
                continue;
            }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                    yield return (current.ToString(), quotedWhole);
                current.Clear();
                quotedWhole = false;
                continue;
            }
            current.Append(c);
        }
    }
}

internal enum SearchTermKind
{
    Text,
    Message,
    Category,
    Exception,
    Has,
    Trace,
    Service,
    Property,
}

/// <param name="Field">The field name as typed, for property terms.</param>
internal sealed record SearchTerm(SearchTermKind Kind, string? Field, string Value, bool Negate);
