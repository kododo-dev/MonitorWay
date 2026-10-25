namespace Kododo.MonitorWay.Files.Formats;

/// <summary>
/// Parses timestamps written with a .NET custom date format without allocating, which matters when every line of a
/// large file is indexed. Supports numeric parts, fractions, AM/PM, offsets and literals; a format with month or day
/// names is not supported (<see cref="TryCreate"/> returns <c>null</c>) and is left to <c>TryParseExact</c>.
/// </summary>
internal sealed class DateFormatParser
{
    private enum Part { Literal, Year, Month, Day, Hour24, Hour12, Minute, Second, Fraction, OptionalFraction, AmPm, Offset, OffsetOrZ }

    private readonly (Part Part, int Width, char Literal)[] _parts;
    private readonly DateOnly _defaultDate;

    private DateFormatParser((Part, int, char)[] parts, DateOnly defaultDate)
    {
        _parts = parts;
        _defaultDate = defaultDate;
    }

    /// <param name="format">A .NET custom date format.</param>
    /// <param name="defaultDate">The date for formats that write only the time of day; today when not given.</param>
    public static DateFormatParser? TryCreate(string format, DateOnly? defaultDate = null)
    {
        var parts = new List<(Part, int, char)>();
        for (var i = 0; i < format.Length;)
        {
            var c = format[i];
            var run = 1;
            while (i + run < format.Length && format[i + run] == c)
                run++;

            switch (c)
            {
                case 'y': parts.Add((Part.Year, run >= 4 ? 4 : 2, '\0')); break;
                case 'M' when run <= 2: parts.Add((Part.Month, run, '\0')); break;
                case 'd' when run <= 2: parts.Add((Part.Day, run, '\0')); break;
                case 'H': parts.Add((Part.Hour24, run, '\0')); break;
                case 'h': parts.Add((Part.Hour12, run, '\0')); break;
                case 'm': parts.Add((Part.Minute, run, '\0')); break;
                case 's': parts.Add((Part.Second, run, '\0')); break;
                case 'f': parts.Add((Part.Fraction, run, '\0')); break;
                case 'F': parts.Add((Part.OptionalFraction, run, '\0')); break;
                case 't' when run == 2: parts.Add((Part.AmPm, 2, '\0')); break;
                case 'z' when run == 3: parts.Add((Part.Offset, 3, '\0')); break;
                case 'K': parts.Add((Part.OffsetOrZ, 1, '\0')); break;
                case 'M' or 'd' or 't' or 'z' or 'g' or 'G': return null; // Names, one-letter AM/PM, short offsets, eras.
                case '\'' or '"':
                    var end = format.IndexOf(c, i + 1);
                    if (end < 0)
                        return null;
                    foreach (var literal in format.AsSpan(i + 1, end - i - 1))
                        parts.Add((Part.Literal, 1, literal));
                    i = end + 1;
                    continue;
                case '\\':
                    if (i + 1 >= format.Length)
                        return null;
                    parts.Add((Part.Literal, 1, format[i + 1]));
                    i += 2;
                    continue;
                default:
                    for (var k = 0; k < run; k++)
                        parts.Add((Part.Literal, 1, c));
                    break;
            }
            i += run;
        }
        return new DateFormatParser(parts.ToArray(), defaultDate ?? DateOnly.FromDateTime(DateTime.Today));
    }

    public bool TryParse(ReadOnlySpan<char> text, out DateTimeOffset value)
    {
        value = default;
        int year = _defaultDate.Year, month = _defaultDate.Month, day = _defaultDate.Day, hour = 0, minute = 0, second = 0;
        long ticks = 0;
        bool? pm = null;
        TimeSpan? offset = null;
        var position = 0;

        for (var index = 0; index < _parts.Length; index++)
        {
            var (part, width, literal) = _parts[index];
            switch (part)
            {
                // Like ParseExact, the separator before an optional fraction may be missing along with the fraction.
                case Part.Literal when index + 1 < _parts.Length && _parts[index + 1].Part == Part.OptionalFraction &&
                                       (position >= text.Length || text[position] != literal):
                    break;
                case Part.Literal:
                    if (position >= text.Length || text[position] != literal)
                        return false;
                    position++;
                    break;
                case Part.Fraction or Part.OptionalFraction:
                    var digits = 0;
                    long fraction = 0;
                    while (digits < width && position < text.Length && char.IsAsciiDigit(text[position]))
                    {
                        fraction = fraction * 10 + (text[position++] - '0');
                        digits++;
                    }
                    if (part == Part.Fraction && digits != width)
                        return false;
                    for (var d = digits; d < 7; d++)
                        fraction *= 10;
                    ticks = fraction;
                    break;
                case Part.AmPm:
                    if (position + 2 > text.Length || char.ToUpperInvariant(text[position + 1]) != 'M')
                        return false;
                    pm = char.ToUpperInvariant(text[position]) switch { 'A' => false, 'P' => true, _ => (bool?)null };
                    if (pm is null)
                        return false;
                    position += 2;
                    break;
                case Part.Offset or Part.OffsetOrZ:
                    if (part == Part.OffsetOrZ && position < text.Length && text[position] == 'Z')
                    {
                        offset = TimeSpan.Zero;
                        position++;
                        break;
                    }
                    if (part == Part.OffsetOrZ && position >= text.Length)
                        break;
                    if (position + 6 > text.Length || text[position] is not ('+' or '-') || text[position + 3] != ':' ||
                        !TryNumber(text.Slice(position + 1, 2), out var hours) || !TryNumber(text.Slice(position + 4, 2), out var minutes))
                        return false;
                    offset = new TimeSpan(hours, minutes, 0) * (text[position] == '-' ? -1 : 1);
                    position += 6;
                    break;
                default:
                    // A one-letter numeric part takes one or two digits; a longer one exactly that many.
                    var length = width == 1 && position + 1 < text.Length && char.IsAsciiDigit(text[position + 1]) ? 2 : width;
                    if (position + length > text.Length || !TryNumber(text.Slice(position, length), out var number))
                        return false;
                    position += length;
                    switch (part)
                    {
                        case Part.Year: year = width == 2 ? 2000 + number : number; break;
                        case Part.Month: month = number; break;
                        case Part.Day: day = number; break;
                        case Part.Hour24 or Part.Hour12: hour = number; break;
                        case Part.Minute: minute = number; break;
                        case Part.Second: second = number; break;
                    }
                    break;
            }
        }
        if (position != text.Length)
            return false;
        if (pm is { } afternoon)
            hour = hour % 12 + (afternoon ? 12 : 0);

        if (month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
            return false;
        var dateTime = new DateTime(year, month, day, hour, minute, second).AddTicks(ticks);
        value = new DateTimeOffset(dateTime, offset ?? TimeZoneInfo.Local.GetUtcOffset(dateTime));
        return true;
    }

    private static bool TryNumber(ReadOnlySpan<char> text, out int value)
    {
        value = 0;
        foreach (var c in text)
        {
            if (!char.IsAsciiDigit(c))
                return false;
            value = value * 10 + (c - '0');
        }
        return true;
    }
}
