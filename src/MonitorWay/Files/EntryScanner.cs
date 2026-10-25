using System.Buffers;
using Kododo.MonitorWay.Files.Formats;
using Microsoft.Win32.SafeHandles;

namespace Kododo.MonitorWay.Files;

/// <summary>
/// Finds entry boundaries in a file: an entry starts on a line its <see cref="ILogFormat"/> recognizes (or at the
/// start of the file) and runs until the next such line.
/// </summary>
internal static class EntryScanner
{
    private const int BlockSize = 64 * 1024;

    public static bool IsEntryStart(ILogFormat format, ReadOnlySpan<byte> line, long offset) =>
        offset == 0 || format.IsEntryStart(FileIndex.LineContent(line, offset));

    /// <summary>The first entry start at or after <paramref name="position"/>.</summary>
    public static long EntryStartAtOrAfter(SafeFileHandle file, ILogFormat format, long position, long readable)
    {
        if (position == 0 || position >= readable)
            return Math.Min(position, readable);

        // To the start of the next line, unless the position already is one.
        Span<byte> one = stackalloc byte[1];
        RandomAccess.Read(file, one, position - 1);
        var line = one[0] == (byte)'\n' ? position : LineStartAfter(file, position, readable);

        var buffer = ArrayPool<byte>.Shared.Rent(BlockSize);
        try
        {
            while (line < readable)
            {
                var block = buffer.AsSpan(0, RandomAccess.Read(file, buffer.AsSpan(0, (int)Math.Min(buffer.Length, readable - line)), line));
                var complete = block.LastIndexOf((byte)'\n') + 1;
                if (complete == 0)
                {
                    var larger = ArrayPool<byte>.Shared.Rent(buffer.Length * 2);
                    ArrayPool<byte>.Shared.Return(buffer);
                    buffer = larger;
                    continue;
                }
                for (var offset = 0; offset < complete;)
                {
                    var lineEnd = offset + block[offset..].IndexOf((byte)'\n') + 1;
                    if (IsEntryStart(format, block[offset..lineEnd], line + offset))
                        return line + offset;
                    offset = lineEnd;
                }
                line += complete;
            }
            return readable;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static long LineStartAfter(SafeFileHandle file, long position, long readable)
    {
        Span<byte> block = stackalloc byte[4096];
        for (var start = position; start < readable; start += block.Length)
        {
            var read = RandomAccess.Read(file, block[..(int)Math.Min(block.Length, readable - start)], start);
            var newLine = block[..read].IndexOf((byte)'\n');
            if (newLine >= 0)
                return start + newLine + 1;
        }
        return readable;
    }

    /// <summary>
    /// An entry start before <paramref name="position"/>, about one block back. Never goes below
    /// <paramref name="floor"/>, which is an entry boundary (the end of trusted chunks, or the start of the file).
    /// </summary>
    public static long EntryStartBefore(SafeFileHandle file, ILogFormat format, long position, long floor, ref byte[] buffer)
    {
        floor = Math.Min(floor, position);
        for (var size = (long)BlockSize; ; size *= 2)
        {
            var start = Math.Max(floor, position - size);
            if (start == floor)
                return start;

            var block = ReadRange(file, start, position, ref buffer);
            // The block may begin in the middle of a line; the first full line follows the first line break.
            var offset = block.IndexOf((byte)'\n') + 1;
            while (offset > 0 && offset < block.Length)
            {
                var lineEnd = offset + block[offset..].IndexOf((byte)'\n') + 1;
                if (IsEntryStart(format, block[offset..lineEnd], start + offset))
                    return start + offset;
                offset = lineEnd;
            }
        }
    }

    /// <summary>
    /// The start of the entry following the one at <paramref name="position"/> (<paramref name="firstEntryOnly"/>),
    /// or the last entry start about one block forward; <paramref name="readable"/> when the entries reach it.
    /// </summary>
    public static long EntryEndAfter(SafeFileHandle file, ILogFormat format, long position, long readable, ref byte[] buffer, bool firstEntryOnly)
    {
        for (var size = (long)BlockSize; ; size *= 2)
        {
            var end = Math.Min(readable, position + size);
            var block = ReadRange(file, position, end, ref buffer);
            var complete = block.LastIndexOf((byte)'\n') + 1;

            long found = -1;
            var offset = block.IndexOf((byte)'\n') + 1; // Skip the first line: it is the entry at the position.
            while (offset > 0 && offset < complete)
            {
                var lineEnd = offset + block[offset..].IndexOf((byte)'\n') + 1;
                if (IsEntryStart(format, block[offset..lineEnd], position + offset))
                {
                    found = position + offset;
                    if (firstEntryOnly)
                        break;
                }
                offset = lineEnd;
            }

            if (end == readable)
                return firstEntryOnly && found >= 0 ? found : readable;
            if (found >= 0)
                return found;
        }
    }

    public static ReadOnlySpan<byte> ReadRange(SafeFileHandle file, long start, long end, ref byte[] buffer)
    {
        var length = (int)(end - start);
        if (buffer.Length < length)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = ArrayPool<byte>.Shared.Rent(length);
        }
        var read = RandomAccess.Read(file, buffer.AsSpan(0, length), start);
        return buffer.AsSpan(0, read);
    }
}