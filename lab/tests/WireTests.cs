using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace MillionUsers.Epoll.Tests;

/// <summary>The request parse and the response head, without sockets: requests byte for byte as mload and curl send
/// them, split and pipelined, and the malformed ones the server must refuse. Plus the HS256 header shortcut.</summary>
public sealed class WireTests
{
    const string Mload = "Host: 10.0.0.2\r\nUser-Agent: mload\r\nAccept-Encoding: gzip\r\n";
    static readonly string Token = JwtTests.Sign(JwtTests.Payload());
    static readonly string CreateBody = "{\"body\":\"ada says hi at 2026-10-27T12:34:56.789Z (VU 1, iter 0)\"}";
    static readonly string Feed = "GET /feed HTTP/1.1\r\n" + Mload + "\r\n";
    static readonly string Like = "POST /posts/12/like HTTP/1.1\r\n" + Mload + "Authorization: Bearer " + Token + "\r\nContent-Length: 0\r\n\r\n";
    static readonly string Create = "POST /posts HTTP/1.1\r\n" + Mload + "Authorization: Bearer " + Token
        + "\r\nContent-Type: application/json\r\nContent-Length: " + CreateBody.Length + "\r\n\r\n" + CreateBody;

    sealed record Parsed(int Used, string Method, string Path, string? Authorization, string Body, bool Close);

    static Parsed Parse(string raw) => Parse(Encoding.UTF8.GetBytes(raw));

    static Parsed Parse(ReadOnlySpan<byte> raw)
    {
        int used = Loop.Parse(raw, out HttpRequest q);
        return new Parsed(used, Encoding.UTF8.GetString(q.Method), Encoding.UTF8.GetString(q.Path),
            q.HasAuthorization ? Encoding.UTF8.GetString(q.Authorization) : null, Encoding.UTF8.GetString(q.Body), q.Close);
    }

    [Fact]
    public void MloadsFeedRequestIsReadWhole() =>
        Assert.Equal(new Parsed(Feed.Length, "GET", "/feed", null, "", false), Parse(Feed));

    [Fact]
    public void MloadsLikeCarriesItsTokenAndAnEmptyBody() =>
        Assert.Equal(new Parsed(Like.Length, "POST", "/posts/12/like", "Bearer " + Token, "", false), Parse(Like));

    [Fact]
    public void MloadsNewPostCarriesItsBody() =>
        Assert.Equal(new Parsed(Create.Length, "POST", "/posts", "Bearer " + Token, CreateBody, false), Parse(Create));

    [Fact]
    public void CurlsRequestIsReadWhole()
    {
        string raw = "GET /posts/1 HTTP/1.1\r\nHost: 127.0.0.1:3000\r\nUser-Agent: curl/8.7.1\r\nAccept: */*\r\n\r\n";
        Assert.Equal(new Parsed(raw.Length, "GET", "/posts/1", null, "", false), Parse(raw));
    }

    [Fact]
    public void TheQueryStringIsNotPartOfThePath() => Assert.Equal("/feed", Parse("GET /feed?limit=5&x=? HTTP/1.1\r\n\r\n").Path);

    [Fact]
    public void ARequestLineWithoutHeadersIsFine() =>
        Assert.Equal(new Parsed(24, "GET", "/health", null, "", false), Parse("GET /health HTTP/1.1\r\n\r\n"));

    [Fact]
    public void EveryPrefixOfARequestIsIncomplete()
    {
        byte[] raw = Encoding.UTF8.GetBytes(Create);
        for (int k = 0; k < raw.Length; k++)
            Assert.True(Loop.Parse(raw.AsSpan(0, k), out _) == 0, $"a prefix of {k} bytes");
        Assert.Equal(raw.Length, Loop.Parse(raw, out _));
    }

    [Fact]
    public void ABodyStillOnItsWayIsIncomplete() =>
        Assert.Equal(0, Parse("POST /posts HTTP/1.1\r\nContent-Length: 10\r\n\r\n{\"body\":").Used);

    [Fact]
    public void PipelinedRequestsAreReadOneAtATimeInOrder()
    {
        byte[] raw = Encoding.UTF8.GetBytes(Feed + Create + Like + "GET /posts/7 HTTP/1.1\r\n\r\n");
        var paths = new List<string>();
        int at = 0;
        while (at < raw.Length)
        {
            Parsed p = Parse(raw.AsSpan(at));
            Assert.True(p.Used > 0);
            paths.Add(p.Method + " " + p.Path + " " + p.Body.Length);
            at += p.Used;
        }
        Assert.Equal(["GET /feed 0", "POST /posts " + CreateBody.Length, "POST /posts/12/like 0", "GET /posts/7 0"], paths);
    }

    [Property(MaxTest = 300)]
    public bool PipelinedRequestsSplitAnywhereReadTheSame(PositiveInt seed)
    {
        var rng = new Random(seed.Get);
        string[] pool = [Feed, Like, Create, "GET /posts/9 HTTP/1.0\r\n\r\n"];
        string all = string.Concat(Enumerable.Range(0, 1 + rng.Next(5)).Select(_ => pool[rng.Next(pool.Length)]));
        byte[] raw = Encoding.UTF8.GetBytes(all);
        var whole = Requests(raw, raw.Length);
        // as the loop does it: what's held plus the next piece, parsed from the start until a request is incomplete
        int cut = rng.Next(1, raw.Length);
        return whole.SequenceEqual(Requests(raw, cut)) && whole.Count > 0;
    }

    static List<string> Requests(byte[] raw, int firstPiece)
    {
        var got = new List<string>();
        int at = 0, have = firstPiece;
        while (at < raw.Length)
        {
            Parsed p = Parse(raw.AsSpan(at, have - at));
            if (p.Used == 0)
            {
                Assert.True(have < raw.Length, "a whole request was called incomplete");
                have = raw.Length;
                continue;
            }
            Assert.True(p.Used > 0);
            got.Add($"{p.Method} {p.Path} {p.Authorization} {p.Body} {p.Close}");
            at += p.Used;
        }
        return got;
    }

    [Theory]
    [InlineData("GET /feed HTTP/1.1\r\nConnection: close\r\n\r\n", true)]
    [InlineData("GET /feed HTTP/1.1\r\nconnection:Close\r\n\r\n", true)]
    [InlineData("GET /feed HTTP/1.1\r\nConnection: keep-alive\r\n\r\n", false)]
    [InlineData("GET /feed HTTP/1.0\r\n\r\n", true)]
    [InlineData("GET /feed HTTP/1.0\r\nConnection: Keep-Alive\r\n\r\n", false)]
    [InlineData("GET /feed HTTP/1.1\r\nConnection: upgrade\r\n\r\n", false)]
    public void ConnectionCloseAndHttp10Close(string raw, bool close) => Assert.Equal(close, Parse(raw).Close);

    [Theory]
    [InlineData("AUTHORIZATION:  Bearer x \t")]
    [InlineData("authorization:\tBearer x")]
    [InlineData("Authorization:Bearer x")]
    public void AuthorizationIsFoundInAnyCaseAndTrimmed(string header) =>
        Assert.Equal("Bearer x", Parse("POST /posts HTTP/1.1\r\n" + header + "\r\nContent-Length: 0\r\n\r\n").Authorization);

    [Fact]
    public void AnEmptyAuthorizationIsStillThere() => Assert.Equal("", Parse("POST /posts HTTP/1.1\r\nAuthorization:\r\n\r\n").Authorization);

    [Fact]
    public void AHeaderNamedLikeAuthorizationIsNot() =>
        Assert.Null(Parse("POST /posts HTTP/1.1\r\nX-Authorization: Bearer x\r\nAuthorizations: Bearer y\r\n\r\n").Authorization);

    [Fact]
    public void ContentLengthInAnyCaseSizesTheBody() =>
        Assert.Equal("abc", Parse("POST /posts HTTP/1.1\r\ncontent-LENGTH:  3 \r\n\r\nabcGET").Body);

    [Fact]
    public void AHeaderLineWithoutAColonIsIgnored() =>
        Assert.Equal(new Parsed(37, "GET", "/feed", null, "", false), Parse("GET /feed HTTP/1.1\r\nnonsense\r\n: x\r\n\r\n"));

    [Theory]
    [InlineData("GET\r\n\r\n")]
    [InlineData("GET /feed\r\n\r\n")]
    [InlineData(" /feed HTTP/1.1\r\n\r\n")]
    [InlineData("\r\n\r\n")]
    [InlineData("POST /posts HTTP/1.1\r\nContent-Length: abc\r\n\r\n")]
    [InlineData("POST /posts HTTP/1.1\r\nContent-Length: -1\r\n\r\n")]
    [InlineData("POST /posts HTTP/1.1\r\nContent-Length: 1x\r\n\r\n")]
    [InlineData("POST /posts HTTP/1.1\r\nContent-Length: 99999999999\r\n\r\n")]
    [InlineData("POST /posts HTTP/1.1\r\nContent-Length: 65536\r\n\r\n")]
    [InlineData("POST /posts HTTP/1.1\r\nTransfer-Encoding: chunked\r\n\r\n5\r\nhello\r\n0\r\n\r\n")]
    [InlineData("POST /posts HTTP/1.1\r\ntransfer-encoding: identity\r\n\r\n")]
    public void AMalformedRequestIsRefused(string raw) => Assert.Equal(-1, Parse(raw).Used);

    [Fact]
    public void TheLargestRequestFitsAndOneMoreByteDoesNot()
    {
        string head = "POST /posts HTTP/1.1\r\nContent-Length: ";
        int fits = (64 << 10) - (head.Length + 5 + 4);
        string body = new('a', fits);
        Assert.Equal(64 << 10, Parse(head + fits.ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + "\r\n\r\n" + body).Used);
        Assert.Equal(-1, Parse(head + (fits + 1).ToString("D5", System.Globalization.CultureInfo.InvariantCulture) + "\r\n\r\n" + body + "a").Used);
    }

    [Fact]
    public void AHeadWithoutItsEndIsIncomplete() => Assert.Equal(0, Parse("GET /feed HTTP/1.1\r\nHost: x\r\n").Used);

    // ---- the response head

    static string Head(int status, int length, bool close)
    {
        byte[] b = new byte[256]; // the head ends at HeadRoom, where the body starts
        int start = Loop.Head(b, status, length, close);
        return Encoding.ASCII.GetString(b, start, b.Length - start);
    }

    [Fact]
    public void AnOkHeadIsTheThreeLinesTheSpecNeeds() =>
        Assert.Equal("HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: 3650\r\n\r\n", Head(200, 3650, false));

    [Fact]
    public void AClosingHeadSaysSo() =>
        Assert.Equal("HTTP/1.1 400 Bad Request\r\nContent-Type: application/json\r\nContent-Length: 0\r\nConnection: close\r\n\r\n", Head(400, 0, true));

    [Theory]
    [InlineData(200, "200 OK")]
    [InlineData(201, "201 Created")]
    [InlineData(400, "400 Bad Request")]
    [InlineData(401, "401 Unauthorized")]
    [InlineData(404, "404 Not Found")]
    [InlineData(500, "500 Internal Server Error")]
    [InlineData(503, "503 Service Unavailable")]
    public void EachStatusHasItsLine(int status, string line) => Assert.StartsWith("HTTP/1.1 " + line + "\r\n", Head(status, 1, false), StringComparison.Ordinal);

    [Fact]
    public void TheLongestHeadFitsTheRoomBeforeTheBody() => Assert.True(Head(503, 1 << 20, true).Length <= 160);

    // ---- the HS256 header shortcut

    [Theory]
    [InlineData("""{"alg":"HS256","typ":"JWT"} x""")]
    [InlineData("""{"alg":"HS256","typ":"JWT"}{}""")]
    [InlineData("""{"alg":"HS256","typ":"JWT","alg":"none"}""")]
    public void AHeaderThatOnlyStartsLikeTheUsualOneIsCheckedInFull(string header)
    {
        byte[] name = new byte[Jwt.MaxNameBytes];
        var jwt = new Jwt(Encoding.UTF8.GetBytes(JwtTests.Secret));
        Assert.Equal(TokenCheck.Invalid, jwt.Check(Encoding.UTF8.GetBytes("Bearer " + JwtTests.Sign(JwtTests.Payload(), header)), name, out _, out _));
    }

    [Fact]
    public void TheUsualHeaderStillNeedsTheSignatureAndExp()
    {
        byte[] name = new byte[Jwt.MaxNameBytes];
        var jwt = new Jwt(Encoding.UTF8.GetBytes(JwtTests.Secret));
        string expired = JwtTests.Sign(JwtTests.Payload(exp: "1"));
        string forged = JwtTests.Sign(JwtTests.Payload(), secret: "another");
        Assert.StartsWith("eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.", expired, StringComparison.Ordinal);
        Assert.Equal(TokenCheck.Invalid, jwt.Check(Encoding.UTF8.GetBytes("Bearer " + expired), name, out _, out _));
        Assert.Equal(TokenCheck.Invalid, jwt.Check(Encoding.UTF8.GetBytes("Bearer " + forged), name, out _, out _));
        Assert.Equal(TokenCheck.Valid, jwt.Check(Encoding.UTF8.GetBytes("Bearer " + Token), name, out long sub, out _));
        Assert.Equal(42, sub);
    }
}
