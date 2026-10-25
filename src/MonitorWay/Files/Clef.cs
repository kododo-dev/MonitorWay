using System.Text;
using System.Text.Json;
using Kododo.MonitorWay.Core.Model;
using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.Files;

/// <summary>
/// Compact Log Event Format: one JSON object per line, as written by Serilog's <c>CompactJsonFormatter</c>.
/// </summary>
/// <remarks>
/// Reserved properties start with <c>@</c> (<c>@t</c> timestamp, <c>@m</c> message, <c>@mt</c> template,
/// <c>@l</c> level, <c>@x</c> exception, <c>@i</c> event id, <c>@tr</c>/<c>@sp</c> trace and span);
/// an application property starting with <c>@</c> is escaped as <c>@@</c>. A missing <c>@l</c> means Information.
/// </remarks>
internal static class Clef
{
    /// <summary>Whether the line looks like CLEF: a JSON object with a <c>@t</c> property.</summary>
    public static bool IsClef(ReadOnlySpan<byte> line)
    {
        line = line.Trim((byte)' ');
        return line.Length > 0 && line[0] == (byte)'{' && line.IndexOf("\"@t\""u8) >= 0;
    }

    /// <summary>
    /// Reads only the timestamp and level, which is enough to index the line. Stops at the first
    /// application property, because the reserved ones come first in CLEF written by Serilog.
    /// </summary>
    public static void ReadHeader(ReadOnlySpan<byte> line, out LogLevel level, out DateTimeOffset? timestamp)
    {
        level = LogLevel.Information;
        timestamp = null;
        try
        {
            var reader = new Utf8JsonReader(line);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
            {
                level = LogLevel.None;
                return;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                if (reader.ValueTextEquals("@t"u8))
                {
                    reader.Read();
                    if (reader.TokenType == JsonTokenType.String && reader.TryGetDateTimeOffset(out var value))
                        timestamp = value;
                }
                else if (reader.ValueTextEquals("@l"u8))
                {
                    reader.Read();
                    level = ParseLevel(reader.GetString());
                }
                else if (reader.ValueSpan.Length > 0 && reader.ValueSpan[0] == (byte)'@' && !reader.ValueSpan.StartsWith("@@"u8))
                {
                    reader.Skip();
                }
                else
                {
                    return;
                }
            }
        }
        catch (JsonException)
        {
            level = LogLevel.None;
        }
    }

    /// <summary>Parses a whole line. A line that is not valid JSON becomes an entry of unknown level with the raw text.</summary>
    public static LogEntry Parse(ReadOnlySpan<byte> line, long id)
    {
        try
        {
            using var document = JsonDocument.Parse(line.ToArray());
            if (document.RootElement.ValueKind == JsonValueKind.Object)
                return FromObject(document.RootElement, id);
        }
        catch (JsonException)
        {
        }
        return new LogEntry(id, null, LogLevel.None, null, Encoding.UTF8.GetString(line), null, null, [], null, null);
    }

    private static LogEntry FromObject(JsonElement root, long id)
    {
        DateTimeOffset? timestamp = null;
        var level = LogLevel.Information;
        string? message = null, template = null, exception = null, traceId = null, spanId = null, category = null;
        var properties = new List<KeyValuePair<string, string?>>();
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var property in root.EnumerateObject())
        {
            switch (property.Name)
            {
                case "@t":
                    if (property.Value.ValueKind == JsonValueKind.String && property.Value.TryGetDateTimeOffset(out var t))
                        timestamp = t;
                    break;
                case "@l": level = ParseLevel(AsString(property.Value)); break;
                case "@m": message = AsString(property.Value); break;
                case "@mt": template = AsString(property.Value); break;
                case "@x": exception = AsString(property.Value); break;
                case "@tr": traceId = AsString(property.Value); break;
                case "@sp": spanId = AsString(property.Value); break;
                case "@i": properties.Add(new("EventId", Text(property.Value))); break;
                case "SourceContext": category = AsString(property.Value); break;
                default:
                    if (property.Name.StartsWith("@@", StringComparison.Ordinal))
                    {
                        var name = property.Name[1..];
                        properties.Add(new(name, Text(property.Value)));
                        values[name] = property.Value;
                    }
                    else if (!property.Name.StartsWith('@'))
                    {
                        properties.Add(new(property.Name, Text(property.Value)));
                        values[property.Name] = property.Value;
                    }
                    break;
            }
        }

        message ??= template is null ? "" : Render(template, values);
        return new LogEntry(id, timestamp, level, category, message, template, exception, properties, traceId, spanId);
    }

    /// <summary>
    /// Renders a message template the way Serilog does by default: strings in quotes unless formatted
    /// with <c>:l</c>, other values as they appear in JSON.
    /// </summary>
    internal static string Render(string template, IReadOnlyDictionary<string, JsonElement> values)
    {
        var result = new StringBuilder(template.Length + 32);
        for (var i = 0; i < template.Length; i++)
        {
            var c = template[i];
            if ((c == '{' || c == '}') && i + 1 < template.Length && template[i + 1] == c)
            {
                result.Append(c);
                i++;
                continue;
            }
            if (c != '{')
            {
                result.Append(c);
                continue;
            }

            var end = template.IndexOf('}', i + 1);
            if (end < 0)
            {
                result.Append(template, i, template.Length - i);
                break;
            }

            var token = template.AsSpan(i + 1, end - i - 1);
            var nameSpan = token.TrimStart("@$");
            var formatStart = nameSpan.IndexOfAny(':', ',');
            var name = (formatStart < 0 ? nameSpan : nameSpan[..formatStart]).ToString();
            var literal = formatStart >= 0 && nameSpan[formatStart] == ':' && nameSpan[(formatStart + 1)..].Contains('l');

            if (values.TryGetValue(name, out var value))
                result.Append(value.ValueKind == JsonValueKind.String && !literal ? $"\"{value.GetString()}\"" : Text(value));
            else
                result.Append(template, i, end - i + 1);
            i = end;
        }
        return result.ToString();
    }

    internal static LogLevel ParseLevel(string? level) =>
        level is null ? LogLevel.Information : Formats.LevelNames.Parse(level);

    private static string? AsString(JsonElement value) =>
        value.ValueKind == JsonValueKind.String ? value.GetString() : Text(value);

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Null => null,
        _ => value.GetRawText(),
    };
}
