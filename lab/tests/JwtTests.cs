using System;
using System.Buffers.Text;
using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace MillionUsers.Epoll.Tests;

/// <summary>The spec's Auth section: HS256 only, signature and exp checked, sub a positive integer string, username a
/// string used as is. Every case below is a token test.sh doesn't send.</summary>
public sealed class JwtTests
{
    internal const string Secret = "million-users-challenge";
    static readonly Jwt Checker = new(Encoding.UTF8.GetBytes(Secret));
    static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    internal static string Sign(string payload, string header = """{"alg":"HS256","typ":"JWT"}""", string secret = Secret)
    {
        string signed = B64(Encoding.UTF8.GetBytes(header)) + "." + B64(Encoding.UTF8.GetBytes(payload));
        return signed + "." + B64(HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(signed)));
    }

    static string B64(byte[] b) => Base64Url.EncodeToString(b);

    internal static string Payload(string sub = "\"42\"", string username = "\"ada\"", string? exp = null) =>
        $$"""{"sub":{{sub}},"username":{{username}},"iat":{{Now}},"exp":{{exp ?? (Now + 3600).ToString(CultureInfo.InvariantCulture)}}}""";

    static (TokenCheck Check, long Sub, string Name) Check(string header)
    {
        byte[] name = new byte[Jwt.MaxNameBytes];
        TokenCheck check = Checker.Check(Encoding.UTF8.GetBytes(header), name, out long sub, out int length);
        return (check, sub, check == TokenCheck.Valid ? Encoding.UTF8.GetString(name, 0, length) : "");
    }

    [Fact]
    public void AGoodTokenGivesItsUserAndName()
    {
        var (check, sub, name) = Check("Bearer " + Sign(Payload()));
        Assert.Equal((TokenCheck.Valid, 42L, "ada"), (check, sub, name));
    }

    [Theory]
    [InlineData("")]
    [InlineData("Basic abc")]
    [InlineData("bearer abc")]
    [InlineData("Bearer")]
    [InlineData("Bearer ")]
    [InlineData("BearerX abc")]
    [InlineData("Token Bearer abc")]
    public void AHeaderThatIsNotBearerSomethingIsMissing(string header) => Assert.Equal(TokenCheck.Missing, Check(header).Check);

    [Fact]
    public void SpacesAroundTheHeaderAreIgnored() => Assert.Equal(TokenCheck.Valid, Check("  Bearer " + Sign(Payload()) + " \t").Check);

    [Theory]
    [InlineData("abc")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    [InlineData("..")]
    [InlineData("Bearer x")]
    public void GarbageIsInvalid(string token) => Assert.Equal(TokenCheck.Invalid, Check("Bearer " + token).Check);

    [Fact]
    public void TwoSpacesAfterBearerIsInvalid() => Assert.Equal(TokenCheck.Invalid, Check("Bearer  " + Sign(Payload())).Check);

    [Fact]
    public void AnotherSecretIsInvalid() => Assert.Equal(TokenCheck.Invalid, Check("Bearer " + Sign(Payload(), secret: "not-the-secret")).Check);

    [Fact]
    public void AChangedPayloadIsInvalid()
    {
        string[] parts = Sign(Payload()).Split('.');
        string forged = parts[0] + "." + B64(Encoding.UTF8.GetBytes(Payload(sub: "\"1\""))) + "." + parts[2];
        Assert.Equal(TokenCheck.Invalid, Check("Bearer " + forged).Check);
    }

    [Fact]
    public void ATruncatedSignatureIsInvalid()
    {
        string token = Sign(Payload());
        Assert.Equal(TokenCheck.Invalid, Check("Bearer " + token[..^2]).Check);
    }

    [Theory]
    [InlineData("""{"alg":"none","typ":"JWT"}""")]
    [InlineData("""{"alg":"HS512","typ":"JWT"}""")]
    [InlineData("""{"alg":"hs256"}""")]
    [InlineData("""{"typ":"JWT"}""")]
    [InlineData("""{"alg":256}""")]
    [InlineData("""["HS256"]""")]
    [InlineData("""{"alg":"HS256"} x""")]
    public void AnyHeaderButHs256IsInvalidEvenWhenSigned(string header) =>
        Assert.Equal(TokenCheck.Invalid, Check("Bearer " + Sign(Payload(), header)).Check);

    [Fact]
    public void AHeaderWithoutTypIsFine() => Assert.Equal(TokenCheck.Valid, Check("Bearer " + Sign(Payload(), """{"alg":"HS256"}""")).Check);

    [Fact]
    public void AnUnsignedAlgNoneTokenIsInvalid()
    {
        string token = B64("""{"alg":"none"}"""u8.ToArray()) + "." + B64(Encoding.UTF8.GetBytes(Payload())) + ".";
        Assert.Equal(TokenCheck.Invalid, Check("Bearer " + token).Check);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3600)]
    public void AnExpThatIsNowOrPastIsInvalid(long secondsAgo) =>
        Assert.Equal(TokenCheck.Invalid, Check("Bearer " + Sign(Payload(exp: (Now - secondsAgo).ToString(CultureInfo.InvariantCulture)))).Check);

    [Theory]
    [InlineData("\"9999999999\"")]
    [InlineData("null")]
    [InlineData("true")]
    public void AnExpThatIsNotANumberIsInvalid(string exp) =>
        Assert.Equal(TokenCheck.Invalid, Check("Bearer " + Sign(Payload(exp: exp))).Check);

    [Fact]
    public void ATokenWithoutExpIsInvalid() =>
        Assert.Equal(TokenCheck.Invalid, Check("Bearer " + Sign("""{"sub":"42","username":"ada"}""")).Check);

    [Fact]
    public void AFractionalExpInTheFutureIsFine() =>
        Assert.Equal(TokenCheck.Valid, Check("Bearer " + Sign(Payload(exp: (Now + 60) + ".5"))).Check);

    [Fact]
    public void ExpiredWinsOverABadSub() =>
        Assert.Equal(TokenCheck.Invalid, Check("Bearer " + Sign(Payload(sub: "\"abc\"", exp: (Now - 10).ToString(CultureInfo.InvariantCulture)))).Check);

    [Theory]
    [InlineData("\"abc\"")]
    [InlineData("\"0\"")]
    [InlineData("\"-1\"")]
    [InlineData("\"1.5\"")]
    [InlineData("\" 1\"")]
    [InlineData("\"\"")]
    [InlineData("\"9223372036854775808\"")]
    [InlineData("\"99999999999999999999\"")]
    [InlineData("\"\\u0031\"")]
    [InlineData("42")]
    [InlineData("null")]
    [InlineData("[\"42\"]")]
    public void ASubThatIsNotAPositiveIntegerStringIsABadPayload(string sub) =>
        Assert.Equal(TokenCheck.InvalidPayload, Check("Bearer " + Sign(Payload(sub: sub))).Check);

    [Fact]
    public void ALongMaxSubIsFine()
    {
        var (check, sub, _) = Check("Bearer " + Sign(Payload(sub: "\"9223372036854775807\"")));
        Assert.Equal((TokenCheck.Valid, long.MaxValue), (check, sub));
    }

    [Fact]
    public void AMissingSubIsABadPayload() =>
        Assert.Equal(TokenCheck.InvalidPayload, Check("Bearer " + Sign($$"""{"username":"ada","exp":{{Now + 60}}}""")).Check);

    [Theory]
    [InlineData("null")]
    [InlineData("7")]
    [InlineData("{}")]
    [InlineData("[\"ada\"]")]
    public void AUsernameThatIsNotAStringIsABadPayload(string username) =>
        Assert.Equal(TokenCheck.InvalidPayload, Check("Bearer " + Sign(Payload(username: username))).Check);

    [Fact]
    public void AMissingUsernameIsABadPayload() =>
        Assert.Equal(TokenCheck.InvalidPayload, Check("Bearer " + Sign($$"""{"sub":"42","exp":{{Now + 60}}}""")).Check);

    [Fact]
    public void AnEmptyUsernameIsAString()
    {
        var (check, _, name) = Check("Bearer " + Sign(Payload(username: "\"\"")));
        Assert.Equal((TokenCheck.Valid, ""), (check, name));
    }

    [Fact]
    public void AnEscapedUsernameIsUnescaped() =>
        Assert.Equal("é \"q\" ✓", Check("Bearer " + Sign(Payload(username: "\"\\u00e9 \\\"q\\\" \\u2713\""))).Name);

    [Fact]
    public void APayloadThatIsNotAnObjectIsInvalid()
    {
        Assert.Equal(TokenCheck.Invalid, Check("Bearer " + Sign("[1]")).Check);
        Assert.Equal(TokenCheck.Invalid, Check("Bearer " + Sign("{\"sub\":")).Check);
        Assert.Equal(TokenCheck.Invalid, Check("Bearer " + Sign(Payload() + "x")).Check);
    }

    [Fact]
    public void ATokenOverTheLimitIsInvalid()
    {
        string token = Sign(Payload(username: "\"" + new string('a', 4000) + "\""));
        Assert.True(token.Length > Jwt.MaxTokenBytes);
        Assert.Equal(TokenCheck.Invalid, Check("Bearer " + token).Check);
    }

    [Fact]
    public void TheLongestNameThatFitsIsCopiedWhole()
    {
        string name = new('n', 2900);
        var (check, _, got) = Check("Bearer " + Sign(Payload(username: "\"" + name + "\"")));
        Assert.Equal((TokenCheck.Valid, name), (check, got));
    }

    [Property]
    public bool AnySignedUserComesBackAsSent(PositiveInt id, NonNull<string> username)
    {
        string name = username.Get;
        if (name.Length > 500 || !IsWellFormed(name)) return true;
        var (check, sub, got) = Check("Bearer " + Sign(Payload(sub: "\"" + id.Get + "\"", username: System.Text.Json.JsonSerializer.Serialize(name))));
        return check == TokenCheck.Valid && sub == id.Get && got == name;
    }

    static bool IsWellFormed(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            else if (char.IsSurrogate(s[i])) return false;
        }
        return true;
    }

    [Theory]
    [InlineData("1", 1L)]
    [InlineData("007", 7L)]
    [InlineData("9223372036854775807", long.MaxValue)]
    [InlineData("0", null)]
    [InlineData("00", null)]
    [InlineData("", null)]
    [InlineData("9223372036854775808", null)]
    [InlineData("9999999999999999999", null)]
    [InlineData("10000000000000000000", null)]
    [InlineData("+1", null)]
    [InlineData("1 ", null)]
    [InlineData("１", null)]
    public void PositiveIntegerBoundaries(string s, long? expected)
    {
        bool ok = Jwt.PositiveInteger(Encoding.UTF8.GetBytes(s), out long got);
        Assert.Equal(expected, ok ? got : null);
    }

    [Property]
    public bool PositiveIntegerAgreesWithLongParse(NonNull<string> s)
    {
        string v = s.Get;
        bool reference = v.Length > 0 && v.Length <= 19 && v.All(char.IsAsciiDigit) && long.TryParse(v, out long n) && n > 0;
        bool ours = Jwt.PositiveInteger(Encoding.UTF8.GetBytes(v), out long got);
        return ours == reference && (!ours || got == long.Parse(v, CultureInfo.InvariantCulture));
    }

    [Property]
    public bool PositiveIntegerTakesEveryPositiveLong(PositiveInt a, uint b)
    {
        long v = (long)a.Get * b + a.Get;
        return Jwt.PositiveInteger(Encoding.ASCII.GetBytes(v.ToString(CultureInfo.InvariantCulture)), out long got) && got == v;
    }
}

