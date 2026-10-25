using System.Text;

namespace Kododo.MonitorWay.Benchmarks;

/// <summary>Synthetic logs of a shop, in CLEF and in the Serilog default text layout.</summary>
internal static class SampleLogs
{
    public const string TextTemplate = "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}";

    private static readonly string[] Products = ["keyboard", "mouse", "monitor", "headset", "webcam", "dock", "cable"];
    private static readonly DateTimeOffset Start = new(2026, 10, 1, 0, 0, 0, TimeSpan.FromHours(2));

    /// <summary>Writes about <paramref name="bytes"/> of logs; one entry in ~1000 is critical, one in ~40 has a stack trace.</summary>
    public static void Write(string path, long bytes, bool text)
    {
        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);
        var random = new Random(42);
        var line = new StringBuilder();
        long written = 0;
        for (long i = 0; written < bytes; i++)
        {
            line.Clear();
            var time = Start.AddMilliseconds(i * 250);
            var roll = random.Next(1000);
            var product = Products[random.Next(Products.Length)];
            var (level, short_, message, exception) = roll switch
            {
                0 => ("Fatal", "FTL", "Database connection pool exhausted", (string?)null),
                < 25 => ("Error", "ERR", $"Payment for order {i} failed at Stripe",
                    "System.TimeoutException: Stripe did not answer\n   at Shop.Payments.Charge()\n   at Shop.Orders.Place()"),
                < 80 => ("Warning", "WRN", $"PayU responded slowly for order {i}", null),
                < 400 => ("Information", "INF", $"Order {i} placed: {random.Next(1, 4)} x {product}", null),
                < 700 => ("Debug", "DBG", $"Executed DbCommand ({random.Next(1, 40)}ms)", null),
                _ => ("Verbose", "VRB", $"Cache lookup for {product}: hit={random.Next(4) != 0}", null),
            };

            if (text)
            {
                line.Append(time.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append(" [").Append(short_).Append("] ").Append(message).Append('\n');
                if (exception is not null)
                    line.Append(exception).Append('\n');
            }
            else
            {
                line.Append("{\"@t\":\"").Append(time.ToString("O")).Append("\",\"@m\":\"").Append(message).Append('"');
                if (level != "Information")
                    line.Append(",\"@l\":\"").Append(level).Append('"');
                if (exception is not null)
                    line.Append(",\"@x\":\"").Append(exception.Replace("\n", "\\n")).Append('"');
                line.Append(",\"OrderId\":").Append(i).Append(",\"Product\":\"").Append(product)
                    .Append("\",\"SourceContext\":\"Shop.Orders\"}\n");
            }

            var data = Encoding.UTF8.GetBytes(line.ToString());
            stream.Write(data);
            written += data.Length;
        }
    }

    /// <summary>Puts a line containing <paramref name="word"/> near the start of a CLEF file, for the worst-case search.</summary>
    public static void PlantNearStart(string path, string word)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite);
        var buffer = new byte[4096];
        stream.ReadExactly(buffer);
        var lineEnd = Array.IndexOf(buffer, (byte)'\n');
        var lineStart = Array.IndexOf(buffer, (byte)'\n', lineEnd + 1) + 1; // the third line
        var messageAt = Encoding.UTF8.GetString(buffer).IndexOf("\"@m\":\"", lineStart, StringComparison.Ordinal) + 6;
        stream.Position = messageAt;
        stream.Write(Encoding.UTF8.GetBytes(word));
    }
}
