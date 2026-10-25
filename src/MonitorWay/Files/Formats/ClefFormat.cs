using Kododo.MonitorWay.Core.Model;
using Microsoft.Extensions.Logging;

namespace Kododo.MonitorWay.Files.Formats;

/// <summary>CLEF: one JSON object per line. See <see cref="Clef"/>.</summary>
internal sealed class ClefFormat : ILogFormat
{
    public static ClefFormat Instance { get; } = new();

    public string Name => "clef";

    /// <summary>Every non-blank line is an entry; a line that is not JSON shows up as raw text.</summary>
    public bool IsEntryStart(ReadOnlySpan<byte> line) => !line.Trim(" \t"u8).IsEmpty;

    public void ReadHeader(ReadOnlySpan<byte> line, out LogLevel level, out DateTimeOffset? timestamp) =>
        Clef.ReadHeader(line, out level, out timestamp);

    public LogEntry Parse(ReadOnlySpan<byte> entry, long id) => Clef.Parse(EntryText.FirstLine(entry), id);

    private static readonly byte[][] CategoryKeys = CategoryNames.JsonKeys("SourceContext");

    public string? ReadCategory(ReadOnlySpan<byte> line, CategoryNames names) => names.FromJsonLine(line, CategoryKeys);
}
