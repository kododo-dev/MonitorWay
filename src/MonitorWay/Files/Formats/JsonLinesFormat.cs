using System.Globalization;
using System.Text;
using System.Text.Json;
using Kododo.MonitorWay.Core.Model;
using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.Files.Formats;

/// <summary>
/// One JSON object per line with conventional field names, as written by Serilog's <c>JsonFormatter</c>,
/// NLog's <c>JsonLayout</c> or most structured loggers. A nested <c>Properties</c> object is flattened.
/// </summary>
internal sealed class JsonLinesFormat : ILogFormat
{
    private static readonly string[] TimestampKeys = ["Timestamp", "timestamp", "@timestamp", "time", "Time", "ts", "date", "@t"];
    private static readonly string[] LevelKeys = ["Level", "level", "severity", "Severity", "lvl", "LogLevel", "@l"];
    private static readonly string[] MessageKeys = ["RenderedMessage", "message", "Message", "msg", "@message", "@m"];
    private static readonly string[] TemplateKeys = ["MessageTemplate", "@mt"];
    private static readonly string[] ExceptionKeys = ["Exception", "exception", "error", "stack", "stack_trace", "@x"];
    private static readonly string[] CategoryKeys = ["SourceContext", "logger", "Logger", "category", "Category", "logger_name", "LoggerName"];
    private static readonly string[] TraceKeys = ["TraceId", "traceId", "trace_id", "@tr"];
    private static readonly string[] SpanKeys = ["SpanId", "spanId", "span_id", "@sp"];

    public static JsonLinesFormat Instance { get; } = new();

    private static readonly byte[][] RawCategoryKeys = CategoryNames.JsonKeys(CategoryKeys);

    public string? ReadCategory(ReadOnlySpan<byte> line, CategoryNames names) => names.FromJsonLine(line, RawCategoryKeys);

    public string Name => "json";

    public bool IsEntryStart(ReadOnlySpan<byte> line) => !line.Trim(" \t"u8).IsEmpty;

    /// <summary>Whether the line is a JSON object with a recognizable timestamp or message.</summary>
    public static bool Recognizes(ReadOnlySpan<byte> line)
    {
        line = line.Trim(" \t"u8);
        if (line.IsEmpty || line[0] != (byte)'{')
            return false;
        try
        {
            using var document = JsonDocument.Parse(line.ToArray());
            return document.RootElement.ValueKind == JsonValueKind.Object &&
                   document.RootElement.EnumerateObject().Any(p => TimestampKeys.Contains(p.Name) || MessageKeys.Contains(p.Name));
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public void ReadHeader(ReadOnlySpan<byte> line, out LogLevel level, out DateTimeOffset? timestamp)
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
                var name = reader.GetString()!;
                reader.Read();
                if (TimestampKeys.Contains(name))
                    timestamp = ReadTimestamp(ref reader);
                else if (LevelKeys.Contains(name) && reader.TokenType == JsonTokenType.String)
                    level = LevelNames.Parse(reader.GetString());
                else
                    reader.Skip();
            }
        }
        catch (JsonException)
        {
            level = LogLevel.None;
        }
    }

    public LogEntry Parse(ReadOnlySpan<byte> entry, long id)
    {
        var line = EntryText.FirstLine(entry);
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
        string? message = null, template = null, exception = null, category = null, traceId = null, spanId = null;
        var properties = new List<KeyValuePair<string, string?>>();
        var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal);

        foreach (var property in root.EnumerateObject())
        {
            var name = property.Name;
            var value = property.Value;
            if (TimestampKeys.Contains(name)) timestamp = ToTimestamp(value);
            else if (LevelKeys.Contains(name)) level = LevelNames.Parse(Text(value) ?? "");
            else if (MessageKeys.Contains(name)) message = Text(value);
            else if (TemplateKeys.Contains(name)) template = Text(value);
            else if (ExceptionKeys.Contains(name)) exception = Text(value);
            else if (CategoryKeys.Contains(name)) category = Text(value);
            else if (TraceKeys.Contains(name)) traceId = Text(value);
            else if (SpanKeys.Contains(name)) spanId = Text(value);
            else if (name is "Properties" or "properties" or "fields" && value.ValueKind == JsonValueKind.Object)
            {
                foreach (var nested in value.EnumerateObject())
                {
                    if (CategoryKeys.Contains(nested.Name))
                        category ??= Text(nested.Value);
                    else
                    {
                        properties.Add(new(nested.Name, Text(nested.Value)));
                        values[nested.Name] = nested.Value;
                    }
                }
            }
            else
            {
                properties.Add(new(name, Text(value)));
                values[name] = value;
            }
        }

        message ??= template is null ? "" : Clef.Render(template, values);
        return new LogEntry(id, timestamp, level, category, message, template, exception, properties, traceId, spanId);
    }

    private static DateTimeOffset? ReadTimestamp(ref Utf8JsonReader reader)
    {
        if (reader.TokenType == JsonTokenType.String)
            return reader.TryGetDateTimeOffset(out var value) ? value : null;
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out var number))
            return FromEpoch(number);
        reader.Skip();
        return null;
    }

    private static DateTimeOffset? ToTimestamp(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String when value.TryGetDateTimeOffset(out var parsed) => parsed,
        JsonValueKind.String when DateTimeOffset.TryParse(value.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed) => parsed,
        JsonValueKind.Number => FromEpoch(value.GetDouble()),
        _ => null,
    };

    /// <summary>Unix time in seconds or milliseconds, told apart by magnitude.</summary>
    private static DateTimeOffset FromEpoch(double value) =>
        value > 1e11
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)value)
            : DateTimeOffset.FromUnixTimeMilliseconds((long)(value * 1000));

    private static string? Text(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(),
        JsonValueKind.Null => null,
        _ => value.GetRawText(),
    };
}
