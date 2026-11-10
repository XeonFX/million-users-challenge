using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Xunit;

namespace MillionUsers.Epoll.Tests;

/// <summary>
/// The built server (bin/app/feed.dll, from build.sh) on a fresh copy of the seed, over raw sockets: what curl in
/// test.sh can't do. Linux only. MU_DATABASE_URL is the database it serves (a fresh copy of feed_seed), and
/// MU_ADMIN_URL a superuser connection to the same server, for killing the server's sessions.
/// </summary>
public sealed class ServerFixture : IDisposable
{
    public int Port { get; } = FreePort();
    public int ExtraPort { get; } = FreePort();
    public string? Skip { get; }
    readonly Process? _server;
    public StringBuilder Log { get; } = new();
    public int Pid => _server?.Id ?? 0;

    public ServerFixture() : this(Environment.GetEnvironmentVariable("MU_DATABASE_URL"), healthy: true)
    {
    }

    /// <summary>A server on <paramref name="db"/>; it is up when /health answers, with 200 when <paramref name="healthy"/>.
    /// With <paramref name="oneLoop"/> it is held to one CPU, so it runs a single loop and every client shares it.</summary>
    internal ServerFixture(string? db, bool healthy, bool oneLoop = false)
    {
        string entry = Environment.GetEnvironmentVariable("MILLION_USERS_ENTRY")
            ?? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../../million-users-challenge/submissions/csharp-epoll-xeonfx"));
        // build.sh makes the NativeAOT binary bin/app/feed, or with FEED_JIT=1 bin/app/feed.dll
        string dll = Path.Combine(entry, "bin/app/feed.dll"), native = Path.Combine(entry, "bin/app/feed");
        bool jit = File.Exists(dll);
        if (!OperatingSystem.IsLinux()) Skip = "the server runs on Linux only";
        else if (db == null) Skip = "MU_DATABASE_URL isn't set";
        else if (!jit && !File.Exists(native)) Skip = $"{native} isn't built (run build.sh)";
        if (Skip != null) return;

        string command = jit ? "dotnet" : native, arguments = jit ? dll : "";
        if (oneLoop) (command, arguments) = ("taskset", $"-c 0 {command} {arguments}".TrimEnd());
        var start = new ProcessStartInfo(command, arguments) { RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment["DATABASE_URL"] = db;
        start.Environment["JWT_SECRET"] = JwtTests.Secret;
        start.Environment["HOST"] = "127.0.0.1";
        start.Environment["PORT"] = Port.ToString(CultureInfo.InvariantCulture);
        start.Environment["EXTRA_PORT"] = ExtraPort.ToString(CultureInfo.InvariantCulture);
        _server = Process.Start(start)!;
        _server.OutputDataReceived += (_, e) => { lock (Log) Log.AppendLine(e.Data); };
        _server.ErrorDataReceived += (_, e) => { lock (Log) Log.AppendLine(e.Data); };
        _server.BeginOutputReadLine();
        _server.BeginErrorReadLine();
        var until = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < until)
        {
            try
            {
                using var c = new Client(Port);
                int status = c.Get("/health").Status;
                if (status == 200 || (!healthy && status > 0)) return;
            }
            catch (SocketException)
            {
            }
            Thread.Sleep(200);
        }
        throw new InvalidOperationException("the server didn't come up: " + Log);
    }

    static int FreePort()
    {
        using var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        return ((IPEndPoint)l.LocalEndpoint).Port;
    }

    public void Dispose()
    {
        if (_server is { HasExited: false }) _server.Kill();
        _server?.Dispose();
    }
}

/// <summary>A raw HTTP/1.1 connection: sends exactly the bytes given and reads responses by Content-Length.</summary>
public sealed class Client : IDisposable
{
    readonly Socket _s;
    readonly byte[] _buf = new byte[1 << 20];
    int _start, _end;

    public Client(int port, int timeoutMs = 10_000, int receiveBuffer = 0)
    {
        _s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true, ReceiveTimeout = timeoutMs };
        if (receiveBuffer > 0) _s.ReceiveBufferSize = receiveBuffer;
        _s.Connect(IPAddress.Loopback, port);
    }

    /// <summary>Drops the connection with a reset, as a client that crashed does: the server sees an error at once.</summary>
    public void Reset()
    {
        _s.LingerState = new LingerOption(true, 0);
        _s.Close();
    }

    public void Send(string raw) => Send(Encoding.UTF8.GetBytes(raw));

    public void Send(byte[] raw) => _s.Send(raw);

    public void ShutdownSend() => _s.Shutdown(SocketShutdown.Send);

    /// <summary>Whether anything arrives from the server within <paramref name="ms"/>.</summary>
    public bool Hears(int ms) => _start < _end || _s.Poll(ms * 1000, SelectMode.SelectRead);

    public static string Request(string method, string path, string? token = null, string? body = null, string extra = "", bool gzip = true)
    {
        var r = new StringBuilder($"{method} {path} HTTP/1.1\r\nHost: test\r\nUser-Agent: mload\r\n");
        if (gzip) r.Append("Accept-Encoding: gzip\r\n");
        if (token != null) r.Append("Authorization: Bearer ").Append(token).Append("\r\n");
        if (body != null) r.Append("Content-Type: application/json\r\nContent-Length: ").Append(Encoding.UTF8.GetByteCount(body)).Append("\r\n");
        else if (method == "POST") r.Append("Content-Length: 0\r\n");
        r.Append(extra).Append("\r\n").Append(body);
        return r.ToString();
    }

    public Response Get(string path, bool gzip = true)
    {
        Send(Request("GET", path, gzip: gzip));
        return Read();
    }

    public Response Read()
    {
        int headEnd;
        while ((headEnd = _buf.AsSpan(_start, _end - _start).IndexOf("\r\n\r\n"u8)) < 0) Fill();
        string head = Encoding.ASCII.GetString(_buf, _start, headEnd);
        string[] lines = head.Split("\r\n");
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string l in lines.Skip(1)) headers[l[..l.IndexOf(':', StringComparison.Ordinal)]] = l[(l.IndexOf(':', StringComparison.Ordinal) + 1)..].Trim();
        int length = int.Parse(headers["Content-Length"], CultureInfo.InvariantCulture);
        _start += headEnd + 4;
        while (_end - _start < length) Fill();
        // a gzipped body is read as mload reads it: unzipped (Content-Length counts the compressed bytes)
        bool gzipped = headers.TryGetValue("Content-Encoding", out string? encoding) && encoding == "gzip";
        string body = gzipped ? Encoding.UTF8.GetString(Gunzip(_buf.AsSpan(_start, length))) : Encoding.UTF8.GetString(_buf, _start, length);
        _start += length;
        return new Response(int.Parse(lines[0].Split(' ')[1], CultureInfo.InvariantCulture), headers, body, lines[0], length);
    }

    static byte[] Gunzip(ReadOnlySpan<byte> gz)
    {
        using var z = new GZipStream(new MemoryStream(gz.ToArray()), CompressionMode.Decompress);
        using var o = new MemoryStream();
        z.CopyTo(o);
        return o.ToArray();
    }

    void Fill()
    {
        if (_start == _end) _start = _end = 0;
        else if (_end == _buf.Length)
        {
            _buf.AsSpan(_start, _end - _start).CopyTo(_buf);
            _end -= _start;
            _start = 0;
        }
        int n = _s.Receive(_buf, _end, _buf.Length - _end, SocketFlags.None);
        if (n == 0) throw new EndOfStreamException("the server closed the connection");
        _end += n;
    }

    /// <summary>Whether the server closes the connection (and sends nothing more) within the timeout.</summary>
    public bool Closes()
    {
        try
        {
            return _start == _end && _s.Receive(new byte[1]) == 0;
        }
        catch (SocketException)
        {
            return true;
        }
    }

    public void Dispose() => _s.Dispose();
}

/// <summary>A response with its body as text (unzipped when it came gzipped) and the bytes it took on the wire.</summary>
public sealed record Response(int Status, Dictionary<string, string> Headers, string Body, string StatusLine, int WireLength = 0)
{
    public JsonElement Json => JsonDocument.Parse(Body).RootElement;
}

[Collection("Server")]
public sealed class ServerTests(ServerFixture server) : IClassFixture<ServerFixture>
{
    static readonly string Token = JwtTests.Sign(JwtTests.Payload(sub: "\"7\"", username: "\"tester_7\""));

    static string TokenFor(long user, string name) => JwtTests.Sign(JwtTests.Payload(sub: $"\"{user}\"", username: JsonSerializer.Serialize(name)));

    Client Connect(int? port = null, int receiveBuffer = 0)
    {
        Assert.SkipWhen(server.Skip != null, server.Skip ?? "");
        return new Client(port ?? server.Port, receiveBuffer: receiveBuffer);
    }

    long CreatePost(Client c, string body = "a post", string? token = null)
    {
        c.Send(Client.Request("POST", "/posts", token ?? Token, JsonSerializer.Serialize(new { body })));
        Response r = c.Read();
        Assert.Equal(201, r.Status);
        return r.Json.GetProperty("post").GetProperty("id").GetInt64();
    }

    [Fact]
    public void BothPortsServeTheSameApi()
    {
        using Client a = Connect(), b = Connect(server.ExtraPort);
        Assert.Equal(a.Get("/posts/1").Body, b.Get("/posts/1").Body);
        Assert.Equal(200, b.Get("/feed").Status);
    }

    [Fact]
    public void ResponsesAreJsonWithAnExactLength()
    {
        using Client c = Connect();
        Response r = c.Get("/feed");
        Assert.StartsWith("application/json", r.Headers["Content-Type"], StringComparison.Ordinal);
        Assert.Equal(20, r.Json.GetProperty("posts").GetArrayLength());
        Assert.Equal("HTTP/1.1 200 OK", r.StatusLine);
        Assert.False(r.Headers.ContainsKey("Connection"));
    }

    [Fact]
    public void TheFeedIsGzippedForClientsThatTakeItAndOnlyThen()
    {
        using Client c = Connect();
        for (int i = 0; i < 20; i++) CreatePost(c, $"tester_7 says hi at 2026-10-31T21:44:{i:D2}.000Z (VU {i}, iter 0)");
        Response plain = c.Get("/feed", gzip: false);
        Response gzipped = c.Get("/feed");
        Assert.False(plain.Headers.ContainsKey("Content-Encoding"));
        Assert.Equal("gzip", gzipped.Headers["Content-Encoding"]);
        Assert.Equal(plain.Body, gzipped.Body);
        Assert.True(gzipped.WireLength * 3 < plain.WireLength, $"{gzipped.WireLength} gzipped against {plain.WireLength} plain");

        // q=0 refuses it; a short body (one post, an error) isn't worth it
        c.Send(Client.Request("GET", "/feed", gzip: false, extra: "Accept-Encoding: gzip;q=0\r\n"));
        Assert.False(c.Read().Headers.ContainsKey("Content-Encoding"));
        Assert.False(c.Get("/posts/1").Headers.ContainsKey("Content-Encoding"));
        Assert.False(c.Get("/nope").Headers.ContainsKey("Content-Encoding"));
        Assert.Equal(200, c.Get("/feed").Status); // the connection still serves after all of them
    }

    [Fact]
    public void PipelinedFeedsAlternatingEncodingsEachGetTheirOwn()
    {
        using Client c = Connect();
        var batch = new StringBuilder();
        for (int i = 0; i < 6; i++) batch.Append(Client.Request("GET", "/feed", gzip: i % 2 == 0));
        c.Send(batch.ToString());
        for (int i = 0; i < 6; i++)
        {
            Response r = c.Read();
            Assert.Equal(i % 2 == 0, r.Headers.ContainsKey("Content-Encoding"));
            Assert.Equal(20, r.Json.GetProperty("posts").GetArrayLength());
        }
    }

    [Fact]
    public void ManyRequestsOnOneConnectionKeepItOpen()
    {
        using Client c = Connect();
        for (int i = 1; i <= 200; i++) Assert.Equal(i, c.Get("/posts/" + i).Json.GetProperty("post").GetProperty("id").GetInt64());
    }

    [Fact]
    public void AClientSilentPastTheAcceptDeferIsStillServed()
    {
        // the listeners defer accept by a second; a connection that sends nothing for longer is handed over anyway,
        // with nothing to read yet, and its request must still be answered when it comes
        using Client c = Connect();
        Thread.Sleep(3_500);
        Assert.False(c.Hears(0));
        Assert.Equal(1, c.Get("/posts/1").Json.GetProperty("post").GetProperty("id").GetInt64());
        Assert.Equal(200, c.Get("/feed").Status);
    }

    [Fact]
    public void ManyFreshConnectionsAreEachAnsweredAtOnce()
    {
        // each connection's first request is read straight from accept; none may be left waiting for a later event
        var clients = new List<Client>();
        try
        {
            for (int i = 0; i < 300; i++)
            {
                var c = Connect();
                clients.Add(c);
                c.Send(Client.Request("GET", "/posts/" + (i % 50 + 1)));
            }
            for (int i = 0; i < clients.Count; i++)
                Assert.Equal(i % 50 + 1, clients[i].Read().Json.GetProperty("post").GetProperty("id").GetInt64());
        }
        finally
        {
            foreach (Client c in clients) c.Dispose();
        }
    }

    [Fact]
    public void PipelinedRequestsAreAnsweredInOrder()
    {
        using Client c = Connect();
        var requests = new List<(string Raw, int Status)>();
        for (int i = 1; i <= 40; i++)
        {
            requests.Add((Client.Request("GET", "/posts/" + i), 200));
            requests.Add((Client.Request("GET", "/nope"), 404));
            requests.Add((Client.Request("GET", "/posts/x" + i), 400));
            requests.Add((Client.Request("POST", "/posts", body: "{\"body\":\"x\"}"), 401));
            requests.Add((Client.Request("GET", "/feed"), 200));
            requests.Add((Client.Request("GET", "/health"), 200));
        }
        c.Send(string.Concat(requests.Select(r => r.Raw)));
        for (int i = 0; i < requests.Count; i++)
        {
            Response r = c.Read();
            Assert.Equal(requests[i].Status, r.Status);
            if (i % 6 == 0) Assert.Equal(i / 6 + 1, r.Json.GetProperty("post").GetProperty("id").GetInt64());
        }
    }

    [Fact]
    public void AClientThatShutsDownAfterSendingStillGetsEveryAnswer()
    {
        using Client c = Connect();
        c.Send(Client.Request("GET", "/health") + Client.Request("GET", "/posts/1") + Client.Request("GET", "/feed"));
        c.ShutdownSend();
        Assert.Equal([200, 200, 200], new[] { c.Read().Status, c.Read().Status, c.Read().Status });
        Assert.True(c.Closes());
    }

    [Fact]
    public void ARequestArrivingOneByteAtATimeIsServed()
    {
        using Client c = Connect();
        byte[] raw = Encoding.UTF8.GetBytes(Client.Request("POST", "/posts", Token, "{\"body\":\"  byte by byte  \"}"));
        foreach (byte b in raw)
        {
            c.Send([b]);
            Thread.Sleep(1);
        }
        Response r = c.Read();
        Assert.Equal(201, r.Status);
        Assert.Equal("byte by byte", r.Json.GetProperty("post").GetProperty("body").GetString());
    }

    [Fact]
    public void RequestsSplitAnywhereAreServed()
    {
        var rng = new Random(12345);
        byte[] raw = Encoding.UTF8.GetBytes(Client.Request("GET", "/posts/3") + Client.Request("POST", "/posts/3/like", Token) + Client.Request("GET", "/feed"));
        for (int round = 0; round < 30; round++)
        {
            using Client c = Connect();
            for (int at = 0; at < raw.Length;)
            {
                int n = Math.Min(raw.Length - at, rng.Next(1, 120));
                c.Send(raw[at..(at + n)]);
                at += n;
                if (rng.Next(3) == 0) Thread.Sleep(2);
            }
            Assert.Equal(200, c.Read().Status);
            Assert.Contains(c.Read().Status, new[] { 200, 201 });
            Assert.Equal(200, c.Read().Status);
        }
    }

    [Fact]
    public void ConnectionCloseAndHttp10CloseAfterTheAnswer()
    {
        using (Client c = Connect())
        {
            c.Send(Client.Request("GET", "/posts/1", extra: "Connection: close\r\n"));
            Response r = c.Read();
            Assert.Equal((200, "close"), (r.Status, r.Headers["Connection"]));
            Assert.True(c.Closes());
        }
        using (Client c = Connect())
        {
            c.Send("GET /health HTTP/1.0\r\nHost: x\r\n\r\n");
            Assert.Equal(200, c.Read().Status);
            Assert.True(c.Closes());
        }
    }

    [Fact]
    public void QueryStringsAreIgnored()
    {
        using Client c = Connect();
        Assert.Equal(200, c.Get("/feed?page=2").Status);
        Assert.Equal(1, c.Get("/posts/1?x=y").Json.GetProperty("post").GetProperty("id").GetInt64());
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/posts")]
    [InlineData("/posts/")]
    [InlineData("/posts/1/like")]
    [InlineData("/feed/")]
    [InlineData("/FEED")]
    [InlineData("/healthz")]
    public void UnknownGetsAre404(string path)
    {
        using Client c = Connect();
        Response r = c.Get(path);
        Assert.Equal((path == "/posts/" ? 400 : 404), r.Status);
    }

    [Theory]
    [InlineData("Content-Length: abc\r\n")]
    [InlineData("Content-Length: -1\r\n")]
    [InlineData("Content-Length: 100000\r\n")]
    [InlineData("Transfer-Encoding: chunked\r\n")]
    public void ABadRequestIsRefusedAndClosed(string header)
    {
        using Client c = Connect();
        c.Send("POST /posts HTTP/1.1\r\nHost: x\r\n" + header + "\r\n");
        Assert.Equal(400, c.Read().Status);
        Assert.True(c.Closes());
    }

    [Fact]
    public void AHeadThatNeverEndsIsCutOff()
    {
        using Client c = Connect();
        c.Send("GET /feed HTTP/1.1\r\nX-Long: " + new string('a', 70_000));
        Assert.True(c.Closes() || c.Read().Status == 400);
    }

    [Fact]
    public void ALikeIsCountedOncePerUserUnderAStorm()
    {
        long post;
        using (Client c = Connect()) post = CreatePost(c);
        string token = TokenFor(9, "storm");
        var statuses = new int[100];
        Parallel.For(0, statuses.Length, new ParallelOptions { MaxDegreeOfParallelism = 32 }, i =>
        {
            using Client c = Connect();
            c.Send(Client.Request("POST", $"/posts/{post}/like", token));
            Response r = c.Read();
            statuses[i] = r.Status;
            Assert.Equal(r.Status == 201 ? "false" : "true", r.Json.GetProperty("already_liked").GetRawText());
            Assert.Equal(post, r.Json.GetProperty("post_id").GetInt64());
        });
        Assert.Equal(1, statuses.Count(s => s == 201));
        Assert.Equal(statuses.Length - 1, statuses.Count(s => s == 200));
        using Client check = Connect();
        Assert.Equal(1, check.Get($"/posts/{post}").Json.GetProperty("post").GetProperty("like_count").GetInt32());
    }

    [Fact]
    public void WritesThatShareACommitWithAFailingOneAllLand()
    {
        long post;
        using (Client c = Connect()) post = CreatePost(c);
        // creates, likes of a real post and likes of a missing post, all at once so they share groups
        var results = new (string Kind, int Status, long Id)[300];
        Parallel.For(0, results.Length, new ParallelOptions { MaxDegreeOfParallelism = 64 }, i =>
        {
            using Client c = Connect();
            string token = TokenFor(1000 + i, "user_" + i);
            switch (i % 3)
            {
                case 0:
                    c.Send(Client.Request("POST", "/posts", token, JsonSerializer.Serialize(new { body = "group " + i })));
                    Response r = c.Read();
                    results[i] = ("create", r.Status, r.Status == 201 ? r.Json.GetProperty("post").GetProperty("id").GetInt64() : 0);
                    break;
                case 1:
                    c.Send(Client.Request("POST", $"/posts/{post}/like", token));
                    results[i] = ("like", c.Read().Status, 0);
                    break;
                default:
                    c.Send(Client.Request("POST", "/posts/987654321/like", token));
                    results[i] = ("missing", c.Read().Status, 0);
                    break;
            }
        });
        Assert.All(results.Where(r => r.Kind == "create"), r => Assert.Equal(201, r.Status));
        Assert.All(results.Where(r => r.Kind == "like"), r => Assert.Equal(201, r.Status));
        Assert.All(results.Where(r => r.Kind == "missing"), r => Assert.Equal(404, r.Status));
        long[] ids = results.Where(r => r.Kind == "create").Select(r => r.Id).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
        using Client check = Connect();
        Assert.Equal(100, check.Get($"/posts/{post}").Json.GetProperty("post").GetProperty("like_count").GetInt32());
        foreach (long id in ids.Take(20))
        {
            JsonElement p = check.Get("/posts/" + id).Json.GetProperty("post");
            Assert.StartsWith("group ", p.GetProperty("body").GetString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public void ANewPostEchoesTheTokensNameThenReadsShowTheUsersName()
    {
        using Client c = Connect();
        string name = "名前 \"quoted\" \\ é";
        c.Send(Client.Request("POST", "/posts", TokenFor(11, name), JsonSerializer.Serialize(new { body = " top\t" })));
        Response r = c.Read();
        JsonElement post = r.Json.GetProperty("post");
        Assert.Equal((201, "top", name, 0), (r.Status, post.GetProperty("body").GetString(), post.GetProperty("author").GetString(), post.GetProperty("like_count").GetInt32()));
        // reads name the author from users, not from the token
        JsonElement top = c.Get("/feed").Json.GetProperty("posts")[0];
        foreach (string key in new[] { "id", "body", "created_at", "like_count" })
            Assert.Equal(post.GetProperty(key).GetRawText(), top.GetProperty(key).GetRawText());
        Assert.Equal("sunny_raven_11", top.GetProperty("author").GetString());
    }

    [Fact]
    public void ClientsThatVanishMidRequestDontConfuseTheirSuccessors()
    {
        // each request is abandoned before its answer; the fds come straight back for new clients, whose answers
        // must be their own and never a reply meant for the client before them
        for (int round = 0; round < 300; round++)
        {
            using (Client gone = Connect())
            {
                gone.Send(Client.Request("GET", "/feed") + Client.Request("POST", "/posts", Token, "{\"body\":\"gone\"}"));
                gone.Reset();
            }
            using Client c = Connect();
            long id = round % 500 + 1;
            Assert.Equal(id, c.Get("/posts/" + id).Json.GetProperty("post").GetProperty("id").GetInt64());
        }
    }

    [Fact]
    public void ASlowReaderGetsEveryByte()
    {
        // a small receive window, so the server's socket fills and it must keep what it couldn't send
        using Client c = Connect(receiveBuffer: 4096);
        int n = 1000;
        c.Send(string.Concat(Enumerable.Repeat(Client.Request("GET", "/feed"), n)));
        Thread.Sleep(1500); // the server's send buffer fills and the rest waits
        for (int i = 0; i < n; i++)
        {
            Response r = c.Read();
            Assert.Equal(200, r.Status);
            Assert.Equal(20, r.Json.GetProperty("posts").GetArrayLength());
        }
        Assert.Equal(200, c.Get("/health").Status);
    }

    [Fact]
    public void ThousandsOfConnectionsAreServedAtOnce()
    {
        var clients = new List<Client>();
        try
        {
            for (int i = 0; i < 3000; i++) clients.Add(Connect(i % 2 == 0 ? server.Port : server.ExtraPort));
            foreach (var (c, i) in clients.Select((c, i) => (c, i))) c.Send(Client.Request("GET", "/posts/" + (i % 1000 + 1)));
            foreach (var (c, i) in clients.Select((c, i) => (c, i)))
                Assert.Equal(i % 1000 + 1, c.Read().Json.GetProperty("post").GetProperty("id").GetInt64());
        }
        finally
        {
            foreach (Client c in clients) c.Dispose();
        }
    }

    [Fact]
    public void ADatabaseHostThatNeverAnswersDoesNotHoldTheLoops()
    {
        Assert.SkipWhen(server.Skip != null, server.Skip ?? "");
        // TEST-NET-1: routed nowhere, so a connect gets no answer at all, as when the database box vanishes
        using var down = new ServerFixture("postgres://feed:feedpass@192.0.2.1:5432/feed", healthy: false);
        var until = DateTime.UtcNow.AddSeconds(6);
        while (DateTime.UtcNow < until)
        {
            using var c = new Client(down.Port, timeoutMs: 5_000);
            var sw = Stopwatch.StartNew();
            Response health = c.Get("/health");
            Assert.Equal(503, health.Status);
            Assert.Contains("\"db\":\"unreachable\"", health.Body, StringComparison.Ordinal);
            Assert.Equal(500, c.Get("/posts/1").Status);
            Assert.True(sw.ElapsedMilliseconds < 500, $"answered after {sw.ElapsedMilliseconds} ms");
            Thread.Sleep(150);
        }
    }

    [Fact]
    public void ServingWakesNoThreadPoolWorkers()
    {
        using Client c = Connect();
        Assert.SkipUnless(OperatingSystem.IsLinux(), "thread names come from /proc");
        for (int i = 1; i <= 100; i++)
        {
            Assert.Equal(200, c.Get("/posts/" + i).Status);
            Assert.Equal(200, c.Get("/feed").Status);
        }
        CreatePost(c, "no pool");
        string[] names = [.. Directory.GetDirectories($"/proc/{server.Pid}/task").Select(t => File.ReadAllText(t + "/comm").Trim())];
        Assert.DoesNotContain(names, n => n.StartsWith(".NET TP", StringComparison.Ordinal));
    }

    [Fact]
    public void LosingPostgresConnectionsFailsFastThenRecovers()
    {
        using Client c = Connect();
        string? admin = Environment.GetEnvironmentVariable("MU_ADMIN_URL");
        Assert.SkipWhen(admin == null, "MU_ADMIN_URL isn't set");
        string db = new Uri(Environment.GetEnvironmentVariable("MU_DATABASE_URL")!).AbsolutePath.TrimStart('/');
        using (var psql = Process.Start(new ProcessStartInfo("psql", [admin!, "-qXAtc",
                   $"SELECT count(pg_terminate_backend(pid)) FROM pg_stat_activity WHERE datname = '{db}' AND pid <> pg_backend_pid()"]) { RedirectStandardOutput = true })!)
        {
            Assert.True(int.Parse(psql.StandardOutput.ReadToEnd().Trim(), CultureInfo.InvariantCulture) > 0);
            psql.WaitForExit();
        }
        Thread.Sleep(200);
        // until the loops reconnect, a request either fails cleanly or already works; never hangs
        var sw = Stopwatch.StartNew();
        Response during = c.Get("/posts/1");
        Assert.Contains(during.Status, new[] { 200, 500 });
        Assert.True(sw.ElapsedMilliseconds < 3000);
        var until = DateTime.UtcNow.AddSeconds(10);
        int ok = 0;
        while (DateTime.UtcNow < until && ok < 50)
        {
            using Client fresh = Connect();
            ok = fresh.Get("/health").Status == 200 && fresh.Get("/posts/1").Status == 200 && fresh.Get("/feed").Status == 200 ? ok + 1 : 0;
            if (ok == 0) Thread.Sleep(100);
        }
        Assert.Equal(50, ok);
        using Client w = Connect();
        CreatePost(w, "after reconnect");
    }
}
