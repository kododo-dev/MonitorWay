using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Kododo.MonitorWay.Core.Model;
using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.Files.Formats;

/// <summary>
/// Plain text written with a Serilog-style output template, e.g.
/// <c>{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}</c>.
/// </summary>
/// <remarks>
/// The part of the template before <c>{NewLine}</c> becomes a regular expression for the first line of an
/// entry; lines that do not match it continue the previous entry. Continuation lines go to the message until
/// the first line that looks like an exception (<c>System.TimeoutException: …</c> or a <c>   at …</c> frame),
/// and from there to the exception. Known tokens: <c>Timestamp</c>, <c>Level</c>, <c>Message</c>,
/// <c>SourceContext</c>, <c>Exception</c>, <c>Properties</c> (JSON), <c>NewLine</c>; any other token
/// becomes a property. Text the template does not describe stays unparsed: such lines are continuations.
/// </remarks>
internal sealed class TemplateFormat : ILogFormat
{
    private static readonly TimeSpan MatchTimeout = TimeSpan.FromSeconds(1);

    private readonly Regex _firstLine;
    private readonly string? _timestampFormat;
    private readonly string[] _propertyGroups;

    /// <param name="defaultDate">The date of entries whose timestamp has only the time of day.</param>
    public TemplateFormat(string template, string name = "template", DateOnly? defaultDate = null)
    {
        Template = template;
        Name = name;
        _defaultDate = defaultDate;
        var compiled = Compile(template);
        (_firstLine, _level, _timestamp, _category, _timestampFormat, _propertyGroups) =
            (compiled.FirstLine, compiled.Level, compiled.Timestamp, compiled.Category, compiled.TimestampFormat, compiled.PropertyGroups);
        _dateParser = _timestampFormat is null ? null : DateFormatParser.TryCreate(_timestampFormat, defaultDate);
    }

    private readonly DateFormatParser? _dateParser;
    private readonly DateOnly? _defaultDate;

    private readonly Regex? _level;
    private readonly Regex? _timestamp;
    private readonly Regex? _category;

    public string Template { get; }
    public string Name { get; }

    public ILogFormat ForThread() => new TemplateFormat(Template, Name, _defaultDate);

    public string? ReadCategory(ReadOnlySpan<byte> line, CategoryNames names)
    {
        if (_category is null || line.IsEmpty)
            return null;
        Span<char> chars = line.Length <= 1024 ? stackalloc char[line.Length] : new char[line.Length];
        chars = chars[..Encoding.UTF8.GetChars(line, chars)];
        return FirstMatch(_category, chars) is { } range ? names.Get(chars[range]) : null;
    }

    public bool IsEntryStart(ReadOnlySpan<byte> line)
    {
        if (line.IsEmpty)
            return false;
        Span<char> chars = line.Length <= 1024 ? stackalloc char[line.Length] : new char[line.Length];
        var length = Encoding.UTF8.GetChars(line, chars);
        return SafeIsMatch(chars[..length]);
    }

    public void ReadHeader(ReadOnlySpan<byte> line, out LogLevel level, out DateTimeOffset? timestamp) =>
        TryReadEntryStart(line, out level, out timestamp);

    /// <summary>
    /// Indexing calls this for every line of the file, so it works on a stack copy of the line and finds the level
    /// and the timestamp with their own expressions, without allocating strings or <see cref="Match"/> objects.
    /// </summary>
    public bool TryReadEntryStart(ReadOnlySpan<byte> line, out LogLevel level, out DateTimeOffset? timestamp)
    {
        level = LogLevel.None;
        timestamp = null;
        if (line.IsEmpty)
            return false;

        Span<char> chars = line.Length <= 1024 ? stackalloc char[line.Length] : new char[line.Length];
        chars = chars[..Encoding.UTF8.GetChars(line, chars)];
        if (!SafeIsMatch(chars))
            return false;
        if (_level is not null && FirstMatch(_level, chars) is { } levelRange)
            level = LevelNames.Parse(chars[levelRange]);
        if (_timestamp is not null && FirstMatch(_timestamp, chars) is { } timestampRange)
            timestamp = ParseTimestamp(chars[timestampRange]);
        return true;
    }

    private static Range? FirstMatch(Regex regex, ReadOnlySpan<char> text)
    {
        try
        {
            foreach (var match in regex.EnumerateMatches(text))
                return new Range(match.Index, match.Index + match.Length);
        }
        catch (RegexMatchTimeoutException)
        {
        }
        return null;
    }

    public LogEntry Parse(ReadOnlySpan<byte> entry, long id)
    {
        var first = Encoding.UTF8.GetString(EntryText.FirstLine(entry));
        var rest = Encoding.UTF8.GetString(EntryText.Continuation(entry));
        var match = SafeMatch(first);
        if (match is null)
            return new LogEntry(id, null, LogLevel.None, null, rest.Length > 0 ? $"{first}\n{rest}" : first, null, null, [], null, null);

        var properties = new List<KeyValuePair<string, string?>>();
        foreach (var group in _propertyGroups)
        {
            if (match.Groups[group] is { Success: true } value)
                properties.Add(new(group[2..], value.Value));
        }
        if (match.Groups["properties"] is { Success: true } json)
            properties.AddRange(FlattenJson(json.Value));

        var message = match.Groups["message"] is { Success: true } m ? m.Value : "";
        var exception = match.Groups["exception"] is { Success: true, Length: > 0 } x ? x.Value : null;
        if (rest.Length > 0)
        {
            var (messageLines, exceptionLines) = SplitContinuation(rest);
            if (messageLines.Length > 0)
                message = message.Length > 0 ? $"{message}\n{messageLines}" : messageLines;
            if (exceptionLines.Length > 0)
                exception = exception is null ? exceptionLines : $"{exception}\n{exceptionLines}";
        }

        return new LogEntry(
            id,
            match.Groups["timestamp"] is { Success: true } t ? ParseTimestamp(t.ValueSpan) : null,
            match.Groups["level"] is { Success: true } l ? LevelNames.Parse(l.ValueSpan) : LogLevel.None,
            match.Groups["category"] is { Success: true, Length: > 0 } c ? c.Value : null,
            message, null, exception, properties, null, null);
    }

    /// <summary>Lines up to the first one that looks like an exception belong to the message.</summary>
    private static (string Message, string Exception) SplitContinuation(string rest)
    {
        var lines = rest.Split('\n');
        var start = Array.FindIndex(lines, l => ExceptionText.Start.IsMatch(l.TrimEnd('\r')));
        if (start < 0)
            return (rest, "");
        return (string.Join('\n', lines[..start]).TrimEnd(), string.Join('\n', lines[start..]));
    }

    private DateTimeOffset? ParseTimestamp(ReadOnlySpan<char> text)
    {
        if (_dateParser is not null && _dateParser.TryParse(text, out var fast))
            return fast;
        if (_timestampFormat is not null &&
            DateTimeOffset.TryParseExact(text, _timestampFormat, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var exact))
            return exact;
        // Without an exact format, a decimal comma (log4net) is read as a point.
        Span<char> normalized = text.Length <= 64 ? stackalloc char[text.Length] : new char[text.Length];
        text.CopyTo(normalized);
        normalized.Replace(',', '.');
        return DateTimeOffset.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : null;
    }

    private static IEnumerable<KeyValuePair<string, string?>> FlattenJson(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return [];
            return document.RootElement.EnumerateObject()
                .Select(p => new KeyValuePair<string, string?>(p.Name,
                    p.Value.ValueKind == JsonValueKind.String ? p.Value.GetString() : p.Value.GetRawText()))
                .ToArray();
        }
        catch (JsonException)
        {
            return [new("Properties", json)];
        }
    }

    private bool SafeIsMatch(ReadOnlySpan<char> line)
    {
        try
        {
            return _firstLine.IsMatch(line);
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private Match? SafeMatch(string line)
    {
        try
        {
            var match = _firstLine.Match(line);
            return match.Success ? match : null;
        }
        catch (RegexMatchTimeoutException)
        {
            return null;
        }
    }

    /// <summary>
    /// The compiled template: the first-line expression with named groups, and for the level and the timestamp an
    /// expression that matches just that part (a lookbehind for everything before it), so indexing can find them
    /// with <see cref="Regex.EnumerateMatches(ReadOnlySpan{char})"/> without allocating a <see cref="Match"/> per line.
    /// </summary>
    internal sealed record Compiled(Regex FirstLine, Regex? Level, Regex? Timestamp, Regex? Category, string? TimestampFormat, string[] PropertyGroups);

    internal static Compiled Compile(string template)
    {
        var tokens = Tokenize(template);
        var newLine = tokens.FindIndex(t => t.Name == "NewLine");
        if (newLine >= 0)
            tokens = tokens[..newLine];

        var pattern = new StringBuilder("^");
        // The same expression without groups, used as the lookbehind of the level and timestamp expressions.
        var plain = new StringBuilder("^");
        // Where each part sits in the plain expression; its own expression is built once the whole line is known.
        var parts = new Dictionary<string, (int Start, int End)>();
        string? timestampFormat = null;
        var propertyGroups = new List<string>();
        var groups = new HashSet<string>();

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i];
            var last = tokens.Skip(i + 1).All(t => t.Name is null && string.IsNullOrWhiteSpace(t.Literal));
            var rest = last ? ".*" : ".*?";

            if (token.Name is null)
            {
                AppendLiteral(pattern, token.Literal!);
                AppendLiteral(plain, token.Literal!);
                continue;
            }

            var (group, expression) = token.Name switch
            {
                "Timestamp" => ("timestamp", TimestampPattern(token.Format, out timestampFormat)),
                "Level" => ("level", "[A-Za-z]+"),
                "Message" => ("message", rest),
                "SourceContext" => ("category", last ? @"\S+" : @"\S*?"),
                "Exception" => ("exception", rest),
                "Properties" => ("properties", last ? @"\{.*\}" : @"\{.*?\}"),
                _ => ("p_" + Regex.Replace(token.Name, @"[^\w]", "_"), last ? ".*" : @".*?"),
            };

            var alignment = token.Alignment ? @"\s*" : "";
            if (!groups.Add(group))
            {
                pattern.Append("(?:").Append(expression).Append(')').Append(alignment);
                plain.Append("(?:").Append(expression).Append(')').Append(alignment);
                continue;
            }
            if (group.StartsWith("p_", StringComparison.Ordinal))
                propertyGroups.Add(group);
            pattern.Append("(?<").Append(group).Append('>').Append(expression).Append(')').Append(alignment);
            var partStart = plain.Length;
            plain.Append("(?:").Append(expression).Append(')');
            parts[group] = (partStart, plain.Length);
            plain.Append(alignment);
        }
        var end = tokens.Count > 0 && tokens[^1].Name is "Message" or "Exception" or "Properties" ? "$" : "";
        pattern.Append(end);
        plain.Append(end);

        // A part's expression is anchored on both sides by the rest of the line: a lazy part such as the category
        // (\S*?) would otherwise match an empty string.
        var full = plain.ToString();
        Regex? Part(string group) => parts.TryGetValue(group, out var range)
            ? new Regex($"(?<={full[..range.Start]}){full[range.Start..range.End]}(?={full[range.End..]})",
                RegexOptions.CultureInvariant | RegexOptions.Compiled, MatchTimeout)
            : null;
        var levelRegex = Part("level");
        var timestampRegex = Part("timestamp");
        var categoryRegex = Part("category");

        var regex = new Regex(pattern.ToString(), RegexOptions.CultureInvariant | RegexOptions.Compiled, MatchTimeout);
        return new Compiled(regex, levelRegex, timestampRegex, categoryRegex, timestampFormat, propertyGroups.ToArray());
    }

    /// <summary>Literal text of the template; a run of spaces also matches padding, so it becomes <c>\s+</c>.</summary>
    private static void AppendLiteral(StringBuilder pattern, string literal)
    {
        var i = 0;
        while (i < literal.Length)
        {
            if (char.IsWhiteSpace(literal[i]))
            {
                while (i < literal.Length && char.IsWhiteSpace(literal[i]))
                    i++;
                pattern.Append(@"\s+");
                continue;
            }
            pattern.Append(Regex.Escape(literal[i].ToString()));
            i++;
        }
    }

    /// <summary>A pattern for a .NET custom date format; the format is then used to parse the value exactly.</summary>
    internal static string TimestampPattern(string? format, out string? exactFormat)
    {
        if (string.IsNullOrEmpty(format))
        {
            exactFormat = null;
            return @"\d{4}-\d{2}-\d{2}[ T][\d:.,]+(?:\s?(?:Z|[+-]\d{2}:?\d{2}))?";
        }

        exactFormat = format;
        var pattern = new StringBuilder();
        for (var i = 0; i < format.Length;)
        {
            var c = format[i];
            var run = 1;
            while (i + run < format.Length && format[i + run] == c)
                run++;

            switch (c)
            {
                case 'y': pattern.Append(run >= 4 ? @"\d{4}" : @"\d{2}"); break;
                case 'M': pattern.Append(run switch { 1 => @"\d{1,2}", 2 => @"\d{2}", 3 => "[A-Za-z]{3}", _ => "[A-Za-z]+" }); break;
                case 'd': pattern.Append(run switch { 1 => @"\d{1,2}", 2 => @"\d{2}", 3 => "[A-Za-z]{3}", _ => "[A-Za-z]+" }); break;
                case 'H' or 'h' or 'm' or 's': pattern.Append(run == 1 ? @"\d{1,2}" : @"\d{2}"); break;
                case 'f': pattern.Append(@"\d{").Append(run).Append('}'); break;
                case 'F': pattern.Append(@"\d{0,").Append(run).Append('}'); break;
                case 't': pattern.Append(run == 1 ? "[AP]" : "[AP]M"); break;
                case 'z': pattern.Append(run switch { 1 => @"[+-]\d{1,2}", 2 => @"[+-]\d{2}", _ => @"[+-]\d{2}:\d{2}" }); break;
                case 'K': pattern.Append(@"(?:Z|[+-]\d{2}:\d{2})?"); break;
                case '\'' or '"':
                    var end = format.IndexOf(c, i + 1);
                    if (end < 0)
                        end = format.Length;
                    pattern.Append(Regex.Escape(format[(i + 1)..end]));
                    i = end + 1;
                    continue;
                case '\\' when i + 1 < format.Length:
                    pattern.Append(Regex.Escape(format[i + 1].ToString()));
                    i += 2;
                    continue;
                default:
                    for (var k = 0; k < run; k++)
                        pattern.Append(Regex.Escape(c.ToString()));
                    break;
            }
            i += run;
        }
        return pattern.ToString();
    }

    private sealed record Token(string? Literal, string? Name, string? Format, bool Alignment);

    /// <summary>Splits a template into literal text and <c>{Name,alignment:format}</c> tokens; <c>{{</c> and <c>}}</c> are literal braces.</summary>
    private static List<Token> Tokenize(string template)
    {
        var tokens = new List<Token>();
        var literal = new StringBuilder();
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if ((c == '{' || c == '}') && i + 1 < template.Length && template[i + 1] == c)
            {
                literal.Append(c);
                i++;
                continue;
            }
            var end = c == '{' ? template.IndexOf('}', i + 1) : -1;
            if (end < 0)
            {
                literal.Append(c);
                continue;
            }

            if (literal.Length > 0)
            {
                tokens.Add(new Token(literal.ToString(), null, null, false));
                literal.Clear();
            }
            var body = template[(i + 1)..end].TrimStart('@', '$');
            var colon = body.IndexOf(':');
            var format = colon >= 0 ? body[(colon + 1)..] : null;
            var nameAndAlignment = colon >= 0 ? body[..colon] : body;
            var comma = nameAndAlignment.IndexOf(',');
            tokens.Add(new Token(null, comma >= 0 ? nameAndAlignment[..comma] : nameAndAlignment, format, comma >= 0));
            i = end;
        }
        if (literal.Length > 0)
            tokens.Add(new Token(literal.ToString(), null, null, false));
        return tokens;
    }
}
