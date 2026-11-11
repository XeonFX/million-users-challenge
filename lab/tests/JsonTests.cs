using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.Json;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace MillionUsers.Epoll.Tests;

/// <summary>POST /posts' body rules: malformed JSON, a missing, non-string or blank body, over 500 characters.</summary>
public sealed class BodyTests
{
    static (BodyCheck Check, string Body) Parse(string json) => Parse(Encoding.UTF8.GetBytes(json));

    static (BodyCheck Check, string Body) Parse(byte[] json)
    {
        byte[] into = new byte[json.Length + 16];
        BodyCheck check = Json.Body(json, into, out int length);
        return (check, check == BodyCheck.Valid ? Encoding.UTF8.GetString(into, 0, length) : "");
    }

    [Theory]
    [InlineData("""{"body":"x"}""", "x")]
    [InlineData("""{"body":"  hello world \t\r\n"}""", "hello world")]
    [InlineData("""{"other":1,"body":"x","more":[1,{"a":null}]}""", "x")]
    [InlineData(""" { "body" : "x" } """, "x")]
    [InlineData("""{"body":"line\nbreak"}""", "line\nbreak")]
    [InlineData("""{"body":"\u00e9\u2713 \ud83d\ude00"}""", "é✓ 😀")]
    [InlineData("""{"body":"<b>&amp;</b> \"q\" / \\"}""", "<b>&amp;</b> \"q\" / \\")]
    [InlineData("""{"body":5,"body":"x"}""", "x")]
    public void AStringBodyIsTakenUnescapedAndTrimmed(string json, string body) => Assert.Equal((BodyCheck.Valid, body), Parse(json));

    [Theory]
    [InlineData("\u00a0x\u2028")]
    [InlineData("\u000bx\u000c")]
    [InlineData("\u3000x\u2003")]
    [InlineData("\u0085x\u202f")]
    public void UnicodeWhitespaceIsTrimmedAsStringTrimDoes(string raw) =>
        Assert.Equal((BodyCheck.Valid, raw.Trim()), Parse(JsonSerializer.Serialize(new { body = raw })));

    [Theory]
    [InlineData("")]
    [InlineData("{bad json")]
    [InlineData("{\"body\":\"x\"")]
    [InlineData("{\"body\":\"x\"} trailing")]
    [InlineData("{\"body\":\"x\"}{}")]
    [InlineData("{'body':'x'}")]
    [InlineData("{\"body\":\"x\",}")]
    [InlineData("{\"body\":\"\\q\"}")]
    [InlineData("{\"body\":\"tab\there\"}")]
    [InlineData("nul")]
    public void NotJsonIsMalformed(string json) => Assert.Equal(BodyCheck.Malformed, Parse(json).Check);

    [Fact]
    public void InvalidUtf8IsMalformed() => Assert.Equal(BodyCheck.Malformed, Parse([.. "{\"body\":\""u8, 0xC3, 0x28, .. "\"}"u8]).Check);

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"body\":\"\"}")]
    [InlineData("{\"body\":\"   \"}")]
    [InlineData("{\"body\":\" \\n\\t\\r \"}")]
    [InlineData("{\"body\":123}")]
    [InlineData("{\"body\":null}")]
    [InlineData("{\"body\":true}")]
    [InlineData("{\"body\":[\"x\"]}")]
    [InlineData("{\"body\":{\"body\":\"x\"}}")]
    [InlineData("{\"Body\":\"x\"}")]
    [InlineData("{\"body\":\"x\",\"body\":5}")]
    [InlineData("[]")]
    [InlineData("[{\"body\":\"x\"}]")]
    [InlineData("\"x\"")]
    [InlineData("42")]
    [InlineData("null")]
    public void NoStringBodyIsRequired(string json) => Assert.Equal(BodyCheck.Required, Parse(json).Check);

    [Theory]
    [InlineData("a", 500, true)]
    [InlineData("a", 501, false)]
    [InlineData("é", 500, true)]
    [InlineData("é", 501, false)]
    [InlineData("✓", 500, true)]
    [InlineData("✓", 501, false)]
    [InlineData("😀", 500, true)]
    [InlineData("😀", 501, false)]
    public void TheLimitIs500CharactersNotBytes(string ch, int count, bool fits)
    {
        BodyCheck expected = fits ? BodyCheck.Valid : BodyCheck.TooLong;
        string body = string.Concat(Enumerable.Repeat(ch, count));
        var (check, got) = Parse(JsonSerializer.Serialize(new { body = "  " + body + " \n" }));
        Assert.Equal(expected, check);
        if (expected == BodyCheck.Valid) Assert.Equal(body, got);
    }

    [Fact]
    public void ALongBodyThatTrimsTo500Fits() =>
        Assert.Equal(BodyCheck.Valid, Parse(JsonSerializer.Serialize(new { body = new string(' ', 3000) + new string('b', 500) + new string(' ', 3000) })).Check);

    [Property]
    public bool AnyStringIsJudgedAsTheReferenceWould(NonNull<string> s)
    {
        string raw = s.Get;
        if (!WellFormed(raw)) return true;
        var (check, body) = Parse(JsonSerializer.Serialize(new { body = raw }));
        string trimmed = raw.Trim();
        int chars = trimmed.EnumerateRunes().Count();
        BodyCheck expected = trimmed.Length == 0 ? BodyCheck.Required : chars > 500 ? BodyCheck.TooLong : BodyCheck.Valid;
        return check == expected && (expected != BodyCheck.Valid || body == trimmed);
    }

    internal static bool WellFormed(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            else if (char.IsSurrogate(s[i])) return false;
        }
        return true;
    }
}

/// <summary>The writers behind every response: strings, timestamps and post rows.</summary>
public sealed class WriterTests
{
    static string Write(Action<byte[], Ref> write)
    {
        byte[] b = new byte[1 << 16];
        var at = new Ref();
        write(b, at);
        return Encoding.UTF8.GetString(b, 0, at.Value);
    }

    sealed class Ref
    {
        public int Value;
    }

    static string String(string s) => Write((b, at) => Json.String(b, ref at.Value, Encoding.UTF8.GetBytes(s)));

    static string Timestamp(long pgMicros) => Write((b, at) => Json.Timestamp(b, ref at.Value, pgMicros));

    [Theory]
    [InlineData("plain", "\"plain\"")]
    [InlineData("", "\"\"")]
    [InlineData("a\"b\\c", "\"a\\\"b\\\\c\"")]
    [InlineData("\n\r\t", "\"\\n\\r\\t\"")]
    [InlineData("\u0000\u0001\u001f", "\"\\u0000\\u0001\\u001f\"")]
    [InlineData("</script>&", "\"</script>&\"")]
    [InlineData("é✓😀\u007f", "\"é✓😀\u007f\"")]
    public void StringsEscapeOnlyWhatJsonNeeds(string s, string json) => Assert.Equal(json, String(s));

    [Property]
    public bool AnyStringReadsBackTheSame(NonNull<string> s) =>
        !BodyTests.WellFormed(s.Get) || JsonSerializer.Deserialize<string>(String(s.Get)) == s.Get;

    const long PgEpochMicros = 946_684_800_000_000;

    [Theory]
    [InlineData(0L, "\"2000-01-01T00:00:00.000Z\"")]
    [InlineData(999L, "\"2000-01-01T00:00:00.000Z\"")]
    [InlineData(999_999L, "\"2000-01-01T00:00:00.999Z\"")]
    [InlineData(-1L, "\"1999-12-31T23:59:59.999Z\"")]
    [InlineData(-PgEpochMicros, "\"1970-01-01T00:00:00.000Z\"")]
    [InlineData(-PgEpochMicros - 1, "\"1969-12-31T23:59:59.999Z\"")]
    [InlineData(5_184_000_000_000L, "\"2000-03-01T00:00:00.000Z\"")]
    [InlineData(5_183_999_999_999L, "\"2000-02-29T23:59:59.999Z\"")]
    public void TimestampsAreUtcCutToMilliseconds(long pgMicros, string json) => Assert.Equal(json, Timestamp(pgMicros));

    [Property(MaxTest = 2000)]
    public bool AnyTimestampMatchesDateTime(long raw)
    {
        // 1900 to 2200, in microseconds since 2000
        long span = 9_467_280_000_000_000L;
        long pgMicros = (long)((ulong)raw % (ulong)span) - span / 3;
        long ms = pgMicros / 1000 - (pgMicros % 1000 < 0 ? 1 : 0);
        var cut = new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddMilliseconds(ms);
        return Timestamp(pgMicros) == "\"" + cut.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture) + "\"";
    }

    internal static byte[] Row(long id, string body, long createdPg, string username, int likes)
    {
        // the formats the entry asks for: created_at binary, the rest text (Postgres writes an integer's text in plain
        // decimal, with a minus sign when negative)
        var fields = new List<byte[]>();
        byte[] ts = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(ts, createdPg);
        static byte[] Text(long v) => Encoding.ASCII.GetBytes(v.ToString(CultureInfo.InvariantCulture));
        fields.AddRange([Text(id), Encoding.UTF8.GetBytes(body), ts, Encoding.UTF8.GetBytes(username), Text(likes)]);
        var row = new List<byte> { 0, 5 };
        foreach (byte[] f in fields)
        {
            byte[] len = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, f.Length);
            row.AddRange(len);
            row.AddRange(f);
        }
        return [.. row];
    }

    [Fact]
    public void APostRowIsThePostObjectInTheSpecsKeyOrder()
    {
        byte[] row = Row(500_001, "hi \"you\"", 999_999, "ada_1", 3);
        Assert.Equal("""{"id":500001,"body":"hi \"you\"","created_at":"2000-01-01T00:00:00.999Z","author":"ada_1","like_count":3}""",
            Write((b, at) => Json.Post(b, ref at.Value, row)));
    }

    [Fact]
    public void ExtremeValuesFormat()
    {
        byte[] row = Row(long.MaxValue, "", 0, "", int.MaxValue);
        Assert.Equal("""{"id":9223372036854775807,"body":"","created_at":"2000-01-01T00:00:00.000Z","author":"","like_count":2147483647}""",
            Write((b, at) => Json.Post(b, ref at.Value, row)));
    }

    [Property(MaxTest = 2000)]
    public bool AnyPostRowIsValidJsonWithItsValues(long id, int likes, long createdPg, string? body, string? name)
    {
        // what JSON can't carry as is: lone surrogates come from Postgres as text only after UTF-8 encoding replaced them
        body = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(body ?? ""));
        name = Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(name ?? ""));
        long span = 9_467_280_000_000_000L; // 1900 to 2200, as Timestamp's own property keeps to
        createdPg = (long)((ulong)createdPg % (ulong)span) - span / 3;
        string json = Write((b, at) => Json.Post(b, ref at.Value, Row(id, body, createdPg, name, likes)));
        using var doc = JsonDocument.Parse(json);
        JsonElement p = doc.RootElement;
        return string.Join(",", p.EnumerateObject().Select(o => o.Name)) == "id,body,created_at,author,like_count"
            && p.GetProperty("id").GetInt64() == id
            && p.GetProperty("body").GetString() == body
            && p.GetProperty("created_at").GetString() == Timestamp(createdPg).Trim('"')
            && p.GetProperty("author").GetString() == name
            && p.GetProperty("like_count").GetInt32() == likes;
    }

    [Fact]
    public void LongIsPlainDigits()
    {
        Assert.Equal("0", Write((b, at) => Json.Long(b, ref at.Value, 0)));
        Assert.Equal("-12", Write((b, at) => Json.Long(b, ref at.Value, -12)));
    }
}
