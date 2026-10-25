using System.Text;

namespace Kododo.MonitorWay.Files.Formats;

/// <summary>
/// Turns category names found in raw lines into strings without allocating one per line: an application logs from a
/// handful of categories, so a name is almost always one seen moments ago. Not thread-safe; each indexing thread
/// has its own.
/// </summary>
internal sealed class CategoryNames
{
    private const int Size = 32;
    private const int MaxLength = 512;
    private readonly string?[] _recent = new string?[Size];
    private int _next;

    public string? Get(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty || name.Length > MaxLength)
            return null;
        foreach (var recent in _recent)
        {
            if (recent is not null && name.SequenceEqual(recent))
                return recent;
        }
        var created = name.ToString();
        _recent[_next] = created;
        _next = (_next + 1) % Size;
        return created;
    }

    /// <summary>A name written as a JSON string: plain text, or with escapes, which are rare in type names.</summary>
    public string? GetJson(ReadOnlySpan<byte> quoted)
    {
        if (quoted.IsEmpty || quoted.Length > MaxLength)
            return null;
        if (quoted.IndexOf((byte)'\\') >= 0)
            return Get(System.Text.Json.JsonSerializer.Deserialize<string>($"\"{Encoding.UTF8.GetString(quoted)}\""));
        Span<char> chars = stackalloc char[quoted.Length];
        return Get(chars[..Encoding.UTF8.GetChars(quoted, chars)]);
    }

    /// <summary>
    /// The string value of the first of <paramref name="keys"/> found in a JSON line, by looking for the raw
    /// <c>"key":"</c> bytes. Good enough for counting categories; parsing every line fully would cost far more.
    /// </summary>
    public string? FromJsonLine(ReadOnlySpan<byte> line, byte[][] keys)
    {
        foreach (var key in keys)
        {
            var at = line.IndexOf(key);
            if (at < 0)
                continue;
            var value = line[(at + key.Length)..];
            var end = 0;
            while (end < value.Length && (value[end] != (byte)'"' || end > 0 && value[end - 1] == (byte)'\\'))
                end++;
            return end < value.Length ? GetJson(value[..end]) : null;
        }
        return null;
    }

    public static byte[][] JsonKeys(params string[] names) =>
        names.Select(n => Encoding.UTF8.GetBytes($"\"{n}\":\"")).ToArray();
}
