using System.Text.Json;
using System.Text.Json.Nodes;
using Google.Protobuf;

namespace Kododo.MonitorWay.Otlp;

/// <summary>
/// Reads OTLP/JSON. It is the protobuf JSON mapping with one difference: trace and span ids are hexadecimal, not base64,
/// so they are converted before the protobuf parser reads the message. Enums may be numbers, and unknown fields are
/// ignored, as the specification asks.
/// </summary>
internal static class OtlpJson
{
    private static readonly JsonParser Parser = new(JsonParser.Settings.Default.WithIgnoreUnknownFields(true));
    private static readonly HashSet<string> IdFields = ["traceId", "spanId", "parentSpanId", "trace_id", "span_id", "parent_span_id"];

    /// <exception cref="FormatException">The text is not JSON, or an id is not hexadecimal.</exception>
    /// <exception cref="InvalidProtocolBufferException">The JSON is not a message of the type.</exception>
    public static T Parse<T>(ReadOnlySpan<byte> json) where T : IMessage<T>, new()
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(json);
        }
        catch (JsonException e)
        {
            throw new FormatException($"Not JSON: {e.Message}");
        }
        if (node is null)
            return new T();
        HexIdsToBase64(node);
        return Parser.Parse<T>(node.ToJsonString());
    }

    private static void HexIdsToBase64(JsonNode node)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (name, value) in obj.ToArray())
                {
                    if (IdFields.Contains(name) && value is JsonValue id && id.TryGetValue<string>(out var hex))
                        obj[name] = ToBase64(hex);
                    else if (value is not null)
                        HexIdsToBase64(value);
                }
                break;
            case JsonArray array:
                foreach (var item in array)
                {
                    if (item is not null)
                        HexIdsToBase64(item);
                }
                break;
        }
    }

    private static string ToBase64(string hex)
    {
        if (hex.Length == 0)
            return "";
        try
        {
            return Convert.ToBase64String(Convert.FromHexString(hex));
        }
        catch (FormatException)
        {
            throw new FormatException($"'{hex}' is not a hexadecimal trace or span id.");
        }
    }
}
