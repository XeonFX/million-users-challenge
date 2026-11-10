using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace MillionUsers.Epoll.Tests;

/// <summary>The response gzip writer: whatever goes in comes back out of a standard decoder byte for byte, at every
/// edge of the format (empty input, the 258-byte match, the 32 KB window, 9-bit literals, the output bound), across
/// calls that share its hash table, and when its position offset wraps.</summary>
public sealed class GzipTests
{
    static byte[] Gunzip(ReadOnlySpan<byte> gz)
    {
        using var z = new GZipStream(new MemoryStream(gz.ToArray()), CompressionMode.Decompress);
        using var o = new MemoryStream();
        z.CopyTo(o);
        return o.ToArray();
    }

    /// <summary>Compresses into a buffer of exactly <see cref="Gzip.Bound"/> with guard bytes after it, checks the
    /// guard is untouched and the output decodes back to the input; the compressed length.</summary>
    static int RoundTrip(Gzip g, byte[] input)
    {
        int bound = Gzip.Bound(input.Length);
        byte[] output = new byte[bound + 16];
        output.AsSpan(bound).Fill(0xA5);
        int n = g.Compress(input, output.AsSpan(0, bound));
        Assert.InRange(n, 18, bound);
        Assert.True(output.AsSpan(bound).IndexOfAnyExcept((byte)0xA5) < 0, "wrote past the bound");
        Assert.Equal(input, Gunzip(output.AsSpan(0, n)));
        return n;
    }

    static byte[] Random(int length, int seed)
    {
        var b = new byte[length];
        new Random(seed).NextBytes(b);
        return b;
    }

    [Fact]
    public void AnEmptyBodyIsAValidEmptyMember()
    {
        int n = RoundTrip(new Gzip(), []);
        Assert.Equal(20, n); // 10 header, 2 for the empty fixed block, 8 trailer
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(7)]
    [InlineData(8)]
    [InlineData(9)]
    [InlineData(12)]
    public void TinyRepeatedBodiesRoundTrip(int length) => RoundTrip(new Gzip(), Enumerable.Repeat((byte)'a', length).ToArray());

    [Fact]
    public void EveryByteValueRoundTrips()
    {
        byte[] all = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        RoundTrip(new Gzip(), [.. all, .. all, .. all.Reverse()]);
    }

    [Theory]
    [InlineData(258)]
    [InlineData(259)]
    [InlineData(260)]
    [InlineData(516)]
    [InlineData(100_000)]
    public void RunsAroundTheLongestMatchRoundTrip(int length)
    {
        byte[] run = new byte[length + 1];
        run.AsSpan(1).Fill(0x20);
        run[0] = 7;
        int n = RoundTrip(new Gzip(), run);
        if (length == 100_000) Assert.InRange(n, 0, 1_000); // ~388 matches of 258 at about 3 bytes each
    }

    [Theory]
    [InlineData(32_767)]
    [InlineData(32_768)]
    [InlineData(32_769)]
    [InlineData(40_000)]
    public void ARepeatAtTheWindowsEdgeRoundTrips(int distance)
    {
        byte[] noise = Random(distance, distance);
        byte[] input = [.. noise, .. noise.AsSpan(0, 300)];
        RoundTrip(new Gzip(), input);
    }

    [Fact]
    public void IncompressibleBodiesFitTheBound()
    {
        var g = new Gzip();
        foreach (int length in new[] { 1, 100, 1_448, 65_536, 300_000 })
            Assert.True(RoundTrip(g, Random(length, length)) > length, "random bytes don't shrink");
    }

    [Fact]
    public void AnOutputShorterThanTheBoundIsRefusedUntouched()
    {
        byte[] input = Encoding.ASCII.GetBytes(new string('x', 5_000));
        byte[] output = new byte[Gzip.Bound(input.Length) - 1];
        Assert.Equal(0, new Gzip().Compress(input, output));
        Assert.True(output.AsSpan().IndexOfAnyExcept((byte)0) < 0);
    }

    [Fact]
    public void ARealFeedShrinksToOneSegment()
    {
        var sb = new StringBuilder("{\"posts\":[");
        for (int i = 0; i < 20; i++)
        {
            if (i > 0) sb.Append(',');
            string name = $"golden_ember_{1000 + i * 977}";
            sb.Append($"{{\"id\":{502_000 - i},\"body\":\"{name} says hi at 2026-10-31T21:44:{10 + i:D2}.{i * 37 % 1000:D3}Z (VU {10_000 + i * 7_919}, iter {i % 9})\",\"created_at\":\"2026-10-31T21:44:{10 + i:D2}.{i * 41 % 1000:D3}Z\",\"author\":\"{name}\",\"like_count\":{i % 5}}}");
        }
        byte[] feed = Encoding.ASCII.GetBytes(sb.Append("]}").ToString());
        int n = RoundTrip(new Gzip(), feed);
        Assert.True(feed.Length > 3_000);
        Assert.True(n + 130 <= 1_448, $"{n} bytes gzipped, more than a segment with its head");
    }

    [Fact]
    public void CallsSharingTheTableNeverMatchAnEarlierBody()
    {
        // the same words at the same offsets in every call: a stale table entry would look like a match
        var g = new Gzip();
        var rnd = new Random(5);
        for (int k = 0; k < 2_000; k++)
        {
            byte[] body = Encoding.ASCII.GetBytes(string.Join(' ', Enumerable.Range(0, 40).Select(_ => rnd.Next(30).ToString())));
            RoundTrip(g, body);
        }
    }

    [Fact]
    public void ThePositionOffsetWrapsAndStartsOver()
    {
        // each call moves the offset by its length plus 32 KB: ~65,500 small calls reach int.MaxValue and clear the table
        var g = new Gzip();
        byte[] body = Encoding.ASCII.GetBytes("{\"id\":1,\"body\":\"abcabcabcabc\"},{\"id\":2,\"body\":\"abcabcabcabc\"}");
        byte[] output = new byte[Gzip.Bound(body.Length)];
        for (int k = 0; k < 70_000; k++)
        {
            int n = g.Compress(body, output);
            if (k % 1_000 == 0 || (k > 65_000 && k < 66_000)) Assert.Equal(body, Gunzip(output.AsSpan(0, n)));
        }
    }

    /// <summary>Text of a small alphabet with copies of its own earlier pieces (some overlapping themselves, as runs
    /// do): what matches, at every distance and length.</summary>
    static byte[] Compressible(int seed)
    {
        var rnd = new Random(seed);
        ReadOnlySpan<byte> alphabet = "abc{}\":,0123 \xff"u8;
        var b = new byte[rnd.Next(0, 6_000)];
        int i = 0;
        while (i < b.Length)
        {
            if (i > 8 && rnd.Next(3) == 0)
            {
                int from = rnd.Next(i), count = Math.Min(rnd.Next(3, 300), b.Length - i);
                for (int k = 0; k < count; k++) b[i + k] = b[from + k];
                i += count;
            }
            else b[i++] = alphabet[rnd.Next(alphabet.Length)];
        }
        return b;
    }

    [Property(MaxTest = 500)]
    public void AnyCompressibleBodyRoundTrips(PositiveInt seed) => RoundTrip(new Gzip(), Compressible(seed.Get));

    [Property(MaxTest = 300)]
    public void AnyBytesRoundTrip(byte[] body) => RoundTrip(new Gzip(), body);

    // ---- Accept-Encoding

    [Theory]
    [InlineData("gzip", true)]
    [InlineData("GZIP", true)]
    [InlineData("x-gzip", true)]
    [InlineData("gzip, deflate, br", true)]
    [InlineData("br, gzip;q=0.8", true)]
    [InlineData("deflate,gzip", true)]
    [InlineData(" gzip ;q=1.0", true)]
    [InlineData("gzip;q=0.001", true)]
    [InlineData("gzip;q=0", false)]
    [InlineData("gzip; q=0.000", false)]
    [InlineData("gzip;Q=0.0", false)]
    [InlineData("gzip;q=0, deflate", false)]
    [InlineData("deflate, br", false)]
    [InlineData("gzipx", false)]
    [InlineData("*", false)]
    [InlineData("", false)]
    public void AcceptEncodingDecides(string value, bool gzip) =>
        Assert.Equal(gzip, Loop.AcceptsGzip(Encoding.ASCII.GetBytes(value)));

    [Theory]
    [InlineData("GET /feed HTTP/1.1\r\nAccept-Encoding: gzip\r\n\r\n", true)]
    [InlineData("GET /feed HTTP/1.1\r\naccept-encoding:gzip, br\r\n\r\n", true)]
    [InlineData("GET /feed HTTP/1.1\r\nAccept-Encoding: br\r\nAccept-Encoding: gzip\r\n\r\n", true)]
    [InlineData("GET /feed HTTP/1.1\r\nAccept-Encoding: identity\r\n\r\n", false)]
    [InlineData("GET /feed HTTP/1.1\r\nHost: x\r\n\r\n", false)]
    public void TheRequestCarriesTheChoice(string raw, bool gzip)
    {
        Assert.True(Loop.Parse(Encoding.ASCII.GetBytes(raw), out HttpRequest q) > 0);
        Assert.Equal(gzip, q.Gzip);
    }

    [Fact]
    public void AGzippedHeadNamesItsEncodingAndCompressedLength()
    {
        byte[] b = new byte[512];
        int start = Loop.Head(b, 200, 812, close: false, gzip: true);
        Assert.Equal("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Encoding: gzip\r\nContent-Length: 812\r\n\r\n",
            Encoding.ASCII.GetString(b, start, 256 - start));
    }
}
