using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Kododo.MonitorWay.Core.Model;
using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.Files.Formats;

/// <summary>
/// Logs of a container written by Docker's <c>json-file</c> driver: every line of the container's output is a JSON
/// object of its own,
/// <code>
/// {"log":"info: Shop.Orders[0]\n","stream":"stdout","time":"2026-10-13T10:15:02.123456789Z"}
/// </code>
/// The format unwraps <c>log</c> and hands it to the format the application writes (<see cref="Inner"/>), so its
/// entries, levels and multi-line stack traces read as they would from a plain file. Ids stay offsets of the Docker
/// lines, and Docker's <c>time</c> stands in for the entry's time when the application writes none.
/// </summary>
/// <remarks>
/// The raw-text search prefilter works on the JSON lines as they are: it looks only for words of ASCII letters and
/// digits, which JSON never escapes. Docker splits lines longer than 16 KB into parts without a line break; a part
/// is read as a line of its own.
/// </remarks>
internal sealed class DockerFormat(ILogFormat inner) : ILogFormat
{
    /// <summary>The application's own format, applied to the unwrapped output.</summary>
    public ILogFormat Inner { get; } = inner;

    public string Name => $"docker · {Inner.Name}";

    /// <summary>Whether the line looks like Docker's <c>json-file</c> output: a JSON object starting with <c>"log"</c>.</summary>
    public static bool Recognizes(ReadOnlySpan<byte> line)
    {
        line = line.TrimStart(" \t"u8);
        return line.StartsWith("{\"log\":"u8) && line.IndexOf("\"time\":"u8) > 0;
    }

    public bool IsEntryStart(ReadOnlySpan<byte> line)
    {
        using var unwrapped = new Unwrapped(line, stackalloc byte[StackLimit]);
        return Inner.IsEntryStart(unwrapped.Log);
    }

    public void ReadHeader(ReadOnlySpan<byte> line, out LogLevel level, out DateTimeOffset? timestamp)
    {
        using var unwrapped = new Unwrapped(line, stackalloc byte[StackLimit]);
        Inner.ReadHeader(unwrapped.Log, out level, out timestamp);
        timestamp ??= unwrapped.Time;
    }

    public bool TryReadEntryStart(ReadOnlySpan<byte> line, out LogLevel level, out DateTimeOffset? timestamp)
    {
        using var unwrapped = new Unwrapped(line, stackalloc byte[StackLimit]);
        if (!Inner.TryReadEntryStart(unwrapped.Log, out level, out timestamp))
            return false;
        timestamp ??= unwrapped.Time;
        return true;
    }

    public string? ReadCategory(ReadOnlySpan<byte> line, CategoryNames names)
    {
        using var unwrapped = new Unwrapped(line, stackalloc byte[StackLimit]);
        return Inner.ReadCategory(unwrapped.Log, names);
    }

    public ILogFormat ForThread()
    {
        var inner = Inner.ForThread();
        return ReferenceEquals(inner, Inner) ? this : new DockerFormat(inner);
    }

    public LogEntry Parse(ReadOnlySpan<byte> entry, long id)
    {
        // The application's output of the entry: the unwrapped lines, joined where Docker ended them.
        var output = new ArrayBufferWriter<byte>(Math.Max(entry.Length, 1));
        DateTimeOffset? time = null;
        string? stream = null;
        var first = true;
        Span<byte> stack = stackalloc byte[StackLimit];
        while (!entry.IsEmpty)
        {
            var end = entry.IndexOf((byte)'\n');
            var line = (end < 0 ? entry : entry[..end]).TrimEnd("\r"u8);
            entry = end < 0 ? default : entry[(end + 1)..];
            if (line.IsEmpty)
                continue;
            using var unwrapped = new Unwrapped(line, stack);
            if (first)
            {
                time = unwrapped.Time;
                stream = unwrapped.Stream;
                first = false;
            }
            output.Write(unwrapped.Log);
            if (unwrapped.EndsLine)
                output.Write("\n"u8);
        }

        var parsed = Inner.Parse(output.WrittenSpan, id);
        return parsed with
        {
            Timestamp = parsed.Timestamp ?? time,
            Properties = stream is null ? parsed.Properties : [.. parsed.Properties, new("Stream", stream)],
        };
    }

    /// <summary>
    /// The application's output of all complete lines of a sample of the file, for detecting its format; lines that
    /// are not Docker's are kept as they are.
    /// </summary>
    public static byte[] UnwrapSample(ReadOnlySpan<byte> sample)
    {
        var output = new ArrayBufferWriter<byte>(Math.Max(sample.Length, 1));
        Span<byte> stack = stackalloc byte[StackLimit];
        while (sample.IndexOf((byte)'\n') is var end and >= 0)
        {
            using (var unwrapped = new Unwrapped(sample[..end].TrimEnd("\r"u8), stack))
            {
                output.Write(unwrapped.Log);
                if (unwrapped.EndsLine)
                    output.Write("\n"u8);
            }
            sample = sample[(end + 1)..];
        }
        return output.WrittenSpan.ToArray();
    }

    private const int StackLimit = 1024;

    /// <summary>
    /// One Docker line taken apart: the unescaped output (without its line break), the stream and the time. A line
    /// that is not Docker's JSON is the output itself, so a damaged file still shows its text.
    /// </summary>
    private ref struct Unwrapped
    {
        private byte[]? _rented;

        public ReadOnlySpan<byte> Log { get; }
        public bool EndsLine { get; }
        public string? Stream { get; }
        public DateTimeOffset? Time { get; }

        public Unwrapped(ReadOnlySpan<byte> line, Span<byte> stack)
        {
            Log = line;
            EndsLine = true;
            try
            {
                var reader = new Utf8JsonReader(line);
                if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject)
                    return;

                ReadOnlySpan<byte> log = default;
                var found = false;
                while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
                {
                    if (reader.ValueTextEquals("log"u8) && reader.Read() && reader.TokenType == JsonTokenType.String)
                    {
                        // Unescaped text is never longer than the escaped one.
                        var buffer = reader.ValueSpan.Length <= stack.Length
                            ? stack
                            : _rented = ArrayPool<byte>.Shared.Rent(reader.ValueSpan.Length);
                        var length = reader.ValueIsEscaped ? reader.CopyString(buffer) : Copy(reader.ValueSpan, buffer);
                        log = buffer[..length];
                        found = true;
                    }
                    else if (reader.ValueTextEquals("stream"u8) && reader.Read())
                        Stream = reader.ValueTextEquals("stderr"u8) ? "stderr" : reader.ValueTextEquals("stdout"u8) ? "stdout" : reader.GetString();
                    else if (reader.ValueTextEquals("time"u8) && reader.Read() && reader.TokenType == JsonTokenType.String)
                        Time = ParseTime(reader.ValueSpan);
                    else
                        reader.Skip();
                }

                if (!found)
                    return;
                EndsLine = log.Length > 0 && log[^1] == (byte)'\n';
                Log = EndsLine ? log[..^1].TrimEnd("\r"u8) : log;
            }
            catch (JsonException)
            {
                Log = line;
                EndsLine = true;
                Stream = null;
                Time = null;
            }
        }

        public void Dispose()
        {
            if (_rented is not null)
                ArrayPool<byte>.Shared.Return(_rented);
        }

        private static int Copy(ReadOnlySpan<byte> source, Span<byte> destination)
        {
            source.CopyTo(destination);
            return source.Length;
        }
    }

    /// <summary>
    /// Docker writes RFC 3339 with nanoseconds (<c>2026-10-13T10:15:02.123456789Z</c>); .NET reads at most seven
    /// fractional digits, so the rest are dropped.
    /// </summary>
    internal static DateTimeOffset? ParseTime(ReadOnlySpan<byte> text)
    {
        if (text.Length > 40)
            return null;
        Span<char> chars = stackalloc char[text.Length];
        var length = 0;
        var digits = -1;
        foreach (var b in text)
        {
            if (b == (byte)'.')
                digits = 0;
            else if (digits >= 0 && b is >= (byte)'0' and <= (byte)'9')
            {
                if (++digits > 7)
                    continue;
            }
            else
                digits = -1;
            chars[length++] = (char)b;
        }
        return DateTimeOffset.TryParse(chars[..length], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var value)
            ? value
            : null;
    }
}
