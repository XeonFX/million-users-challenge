using System;
using System.Globalization;
using System.Linq;
using System.Text;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Xunit;

/// <summary>The vectorized string writer, the 32-bit timestamp writer and the in-place response head, against plain
/// references written from the spec.</summary>
public sealed class FastWriterTests
{
    // JSON's rule: quote, backslash and bytes under 0x20 escaped (short forms for \n \r \t), every other byte as is
    static byte[] Reference(byte[] s)
    {
        var o = new System.Collections.Generic.List<byte> { (byte)'"' };
        foreach (byte c in s)
        {
            string e = c switch
            {
                (byte)'"' => "\\\"",
                (byte)'\\' => "\\\\",
                (byte)'\n' => "\\n",
                (byte)'\r' => "\\r",
                (byte)'\t' => "\\t",
                < 0x20 => "\\u00" + c.ToString("x2", CultureInfo.InvariantCulture),
                _ => "",
            };
            if (e.Length == 0) o.Add(c);
            else o.AddRange(Encoding.ASCII.GetBytes(e));
        }
        o.Add((byte)'"');
        return [.. o];
    }

    static byte[] Written(byte[] s, int offset = 0)
    {
        byte[] b = new byte[offset + s.Length * 6 + 2 + 64];
        b.AsSpan().Fill(0xEE);
        int at = offset;
        Json.String(b, ref at, s);
        Assert.All(b[at..], x => Assert.Equal(0xEE, x)); // nothing written past the end
        return b[offset..at];
    }

    // bytes that matter to the escaper, plus plain ASCII, high bytes and broken UTF-8
    static readonly byte[] Alphabet = [0, 1, 0x08, (byte)'\t', (byte)'\n', (byte)'\r', 0x1F, 0x20, (byte)'"', (byte)'\\', (byte)'a', (byte)'/', 0x7F, 0x80, 0xC3, 0xE2, 0xF0, 0xFF];

    [Property(MaxTest = 3000)]
    public Property AnyBytesAreEscapedAsTheReferenceDoes() =>
        Prop.ForAll(Gen.ArrayOf(Gen.Elements(Alphabet)).Select(a => a).ToArbitrary(), s => Written(s).SequenceEqual(Reference(s)));

    [Property(MaxTest = 500)]
    public bool AnyRawBytesAreEscapedAsTheReferenceDoes(byte[] s) => Written(s).SequenceEqual(Reference(s));

    [Fact]
    public void AnEscapeAtEveryPositionAroundTheVectorEdges()
    {
        foreach (int length in new[] { 1, 15, 16, 17, 31, 32, 33, 63, 64, 65, 70 })
            foreach (byte special in new byte[] { (byte)'"', (byte)'\\', 0, (byte)'\n', 0x1F })
                for (int i = 0; i < length; i++)
                {
                    byte[] s = Enumerable.Repeat((byte)'x', length).ToArray();
                    s[i] = special;
                    Assert.Equal(Reference(s), Written(s));
                    s[length - 1 - i] = special; // a second one, before or after
                    Assert.Equal(Reference(s), Written(s));
                }
    }

    [Fact]
    public void LongAndUnicodeStringsCopyWhole()
    {
        byte[] emoji = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("😀é✓", 500)));
        Assert.Equal(Reference(emoji), Written(emoji));
        byte[] clean = Encoding.ASCII.GetBytes(new string('a', 70_000));
        Assert.Equal(Reference(clean), Written(clean, 3));
        byte[] tailEscape = [.. clean, (byte)'"'];
        Assert.Equal(Reference(tailEscape), Written(tailEscape));
    }

    [Fact]
    public void InvalidUtf8PassesThroughAsIs() => Assert.Equal([(byte)'"', 0xC3, 0x28, 0xFF, (byte)'"'], Written([0xC3, 0x28, 0xFF]));

    [Fact]
    public void AllEscapesInARow() =>
        Assert.Equal(@"""\""\\\n\r\t\u0000\u001f\u0008""",Encoding.ASCII.GetString(Written([(byte)'"', (byte)'\\', 10, 13, 9, 0, 0x1F, 8])));

    const long PgEpochMicros = 946_684_800_000_000;
    static readonly DateTime PgEpoch = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    static string Timestamp(long pgMicros)
    {
        byte[] b = new byte[64];
        int at = 5;
        Json.Timestamp(b, ref at, pgMicros);
        Assert.Equal(31, at);
        return Encoding.ASCII.GetString(b, 5, 26);
    }

    static string Expected(DateTime t) => "\"" + t.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + "\"";

    static long Micros(DateTime t) => (t - PgEpoch).Ticks / 10;

    [Theory]
    [InlineData("0001-01-01T00:00:00.000")]
    [InlineData("9999-12-31T23:59:59.999")]
    [InlineData("1900-02-28T23:59:59.999")]
    [InlineData("1900-03-01T00:00:00.000")]
    [InlineData("2024-02-29T12:00:00.500")]
    [InlineData("2100-03-01T00:00:00.000")]
    [InlineData("2400-02-29T00:00:00.001")]
    [InlineData("2025-12-31T23:59:19.409")]
    [InlineData("1969-12-31T23:59:59.999")]
    public void CalendarEdgesMatchDateTime(string iso)
    {
        DateTime t = DateTime.Parse(iso, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal);
        Assert.Equal(Expected(t), Timestamp(Micros(t)));
        Assert.Equal(Expected(t), Timestamp(Micros(t) + 999)); // microseconds are cut, not rounded
    }

    [Property(MaxTest = 3000)]
    public bool AnyTimestampFromYear1To9999MatchesDateTime(long raw)
    {
        long span = Micros(DateTime.MaxValue) - Micros(DateTime.MinValue);
        long pgMicros = Micros(DateTime.MinValue) + (long)((ulong)raw % (ulong)span);
        long ms = pgMicros / 1000 - (pgMicros % 1000 < 0 ? 1 : 0);
        return Timestamp(pgMicros) == Expected(PgEpoch.AddMilliseconds(ms));
    }

    [Theory]
    [InlineData(long.MaxValue)] // Postgres' infinity
    [InlineData(long.MinValue)] // -infinity
    [InlineData(-PgEpochMicros - 63_200_000_000_000_000)]
    public void OutOfRangeYearsStillWriteTheShape(long pgMicros)
    {
        string s = Timestamp(pgMicros);
        Assert.Matches("^\"[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}\\.[0-9]{3}Z\"$", s);
    }

    static string Head(int status, int length, bool close)
    {
        byte[] b = new byte[512];
        b.AsSpan().Fill(0xEE);
        int start = Loop.Head(b, status, length, close);
        Assert.All(b[..start], x => Assert.Equal(0xEE, x));
        Assert.All(b[256..], x => Assert.Equal(0xEE, x));
        return Encoding.ASCII.GetString(b, start, 256 - start);
    }

    [Theory]
    [InlineData(200, "200 OK")]
    [InlineData(201, "201 Created")]
    [InlineData(400, "400 Bad Request")]
    [InlineData(401, "401 Unauthorized")]
    [InlineData(404, "404 Not Found")]
    [InlineData(503, "503 Service Unavailable")]
    [InlineData(500, "500 Internal Server Error")]
    [InlineData(418, "500 Internal Server Error")]
    public void EachStatusHasItsLine(int status, string line)
    {
        Assert.Equal($"HTTP/1.1 {line}\r\nContent-Type: application/json\r\nContent-Length: 42\r\n\r\n", Head(status, 42, false));
        Assert.Equal($"HTTP/1.1 {line}\r\nContent-Type: application/json\r\nContent-Length: 42\r\nConnection: close\r\n\r\n", Head(status, 42, true));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(9)]
    [InlineData(10)]
    [InlineData(99)]
    [InlineData(100)]
    [InlineData(4_096)]
    [InlineData((1 << 20) - 256)]
    [InlineData(int.MaxValue)]
    public void ContentLengthIsTheBodysLength(int length) =>
        Assert.Equal($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {length.ToString(CultureInfo.InvariantCulture)}\r\n\r\n", Head(200, length, false));
}
