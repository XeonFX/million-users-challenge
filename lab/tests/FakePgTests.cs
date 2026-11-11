using System;
using System.Globalization;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Xunit;

namespace MillionUsers.Epoll.Tests;

/// <summary>A stand-in for Postgres that the test scripts: it speaks the login and takes the Parses, then answers
/// each Bind and Execute as the real one would, or, while <see cref="Hold"/> is set, only records what each
/// connection sends and answers when the test says so. That is how a slow commit, a lost packet and a busy backend
/// are staged.</summary>
sealed class FakePg : IDisposable
{
    readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    readonly List<FakeConn> _conns = [];
    volatile bool _stopped;
    /// <summary>While set, connections record what arrives and answer nothing.</summary>
    public volatile bool Hold;

    public int Port { get; }
    public string Url => $"postgres://u@127.0.0.1:{Port}/d";

    public FakePg()
    {
        _listener.Start(512);
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        new Thread(Accept) { IsBackground = true }.Start();
    }

    void Accept()
    {
        while (!_stopped)
        {
            Socket s;
            try
            {
                s = _listener.AcceptSocket();
            }
            catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException)
            {
                return; // the listener stopped
            }
            s.NoDelay = true;
            var c = new FakeConn(this, s);
            lock (_conns) _conns.Add(c);
            new Thread(c.Run) { IsBackground = true }.Start();
        }
    }

    public List<FakeConn> Conns
    {
        get
        {
            lock (_conns) return [.. _conns];
        }
    }

    /// <summary>The first connection whose log (what it took since <see cref="Clear"/>) satisfies
    /// <paramref name="match"/>, waiting up to <paramref name="ms"/> for one.</summary>
    public FakeConn WaitFor(Func<string, bool> match, int ms = 3000, string what = "a connection")
    {
        var until = Stopwatch.StartNew();
        while (true)
        {
            foreach (FakeConn c in Conns)
                if (match(c.Log)) return c;
            if (until.ElapsedMilliseconds > ms)
                Assert.Fail($"no connection got {what} within {ms} ms; logs: {string.Join(" | ", Conns.Select(c => c.Log).Where(l => l.Length > 0))}");
            Thread.Sleep(1);
        }
    }

    /// <summary>Forgets what every connection has recorded; held work is dropped unanswered.</summary>
    public void Clear()
    {
        foreach (FakeConn c in Conns) c.Clear();
    }

    public void Dispose()
    {
        _stopped = true;
        _listener.Stop();
        foreach (FakeConn c in Conns) c.Close();
    }
}

/// <summary>One connection of <see cref="FakePg"/>. Its log has a letter per Bind (the statement: h, f, p, c or l) and
/// an S per Sync, in the order they arrived.</summary>
sealed class FakeConn(FakePg pg, Socket socket)
{
    readonly object _gate = new();
    readonly StringBuilder _log = new();
    readonly List<long> _times = [];
    /// <summary>What arrived while held and hasn't been answered: the statement and its post id for a Bind, S for a Sync.</summary>
    readonly Queue<(char Kind, long Id)> _owed = new();
    int _parses;

    public string Log
    {
        get
        {
            lock (_gate) return _log.ToString();
        }
    }

    /// <summary>When the <paramref name="i"/>-th entry of the log arrived, in milliseconds.</summary>
    public double TimeOf(int i)
    {
        lock (_gate) return _times[i] * 1000.0 / Stopwatch.Frequency;
    }

    public void Clear()
    {
        lock (_gate)
        {
            _log.Clear();
            _times.Clear();
            _owed.Clear();
        }
    }

    public void Close() => socket.Dispose();

    internal void Run()
    {
        try
        {
            // the startup message has no type byte: a length, then the protocol and the settings
            byte[] head = Fill(4);
            Fill(BinaryPrimitives.ReadInt32BigEndian(head) - 4);
            Send(Message('R', 0, 0, 0, 0), Ready);
            while (true)
            {
                head = Fill(5);
                byte[] body = Fill(BinaryPrimitives.ReadInt32BigEndian(head.AsSpan(1)) - 4);
                Take((char)head[0], body);
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or System.IO.EndOfStreamException)
        {
        }
    }

    byte[] Fill(int n)
    {
        byte[] b = new byte[n];
        for (int at = 0; at < n;)
        {
            int got = socket.Receive(b, at, n - at, SocketFlags.None);
            if (got == 0) throw new System.IO.EndOfStreamException();
            at += got;
        }
        return b;
    }

    void Take(char type, byte[] body)
    {
        switch (type)
        {
            case 'P':
                _parses++;
                break;
            case 'B':
                // portal (empty), then the statement's one-letter name; a post read's id is its only parameter
                char statement = (char)body[1];
                long id = statement == 'p' ? BinaryPrimitives.ReadInt64BigEndian(body.AsSpan(13)) : 0;
                lock (_gate)
                {
                    _log.Append(statement);
                    _times.Add(Stopwatch.GetTimestamp());
                    _owed.Enqueue((statement, id));
                }
                break;
            case 'S':
                if (_parses > 0)
                {
                    // the Parses at login are answered at once, whatever Hold says
                    Send([.. Enumerable.Repeat(Message('1'), _parses).SelectMany(m => m), .. Ready]);
                    _parses = 0;
                    break;
                }
                lock (_gate)
                {
                    _log.Append('S');
                    _times.Add(Stopwatch.GetTimestamp());
                    _owed.Enqueue(('S', 0));
                }
                if (!pg.Hold) Answer(int.MaxValue);
                break;
        }
    }

    /// <summary>Answers what was held, in order, up to and with its next <paramref name="syncs"/> Syncs: each Bind as
    /// Postgres would, each Sync with ReadyForQuery. Returns how many Syncs it answered.</summary>
    public int Answer(int syncs = 1)
    {
        var reply = new List<byte>();
        int answered = 0;
        lock (_gate)
        {
            while (answered < syncs && _owed.Count > 0)
            {
                var (kind, id) = _owed.Dequeue();
                if (kind == 'S')
                {
                    reply.AddRange(Ready);
                    answered++;
                }
                else reply.AddRange(Result(kind, id));
            }
        }
        if (reply.Count > 0) Send([.. reply]);
        return answered;
    }

    public void Send(params byte[][] messages)
    {
        foreach (byte[] m in messages) socket.Send(m);
    }

    static readonly byte[] Ready = Message('Z', (byte)'I');

    static byte[] Message(char type, params byte[] body)
    {
        byte[] m = new byte[5 + body.Length];
        m[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(m.AsSpan(1), 4 + body.Length);
        body.CopyTo(m, 5);
        return m;
    }

    static byte[] Row(params byte[][] columns)
    {
        var b = new List<byte> { (byte)(columns.Length >> 8), (byte)columns.Length };
        foreach (byte[] c in columns)
        {
            byte[] length = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(length, c.Length);
            b.AddRange(length);
            b.AddRange(c);
        }
        return Message('D', [.. b]);
    }

    static byte[] Int8(long v)
    {
        byte[] b = new byte[8];
        BinaryPrimitives.WriteInt64BigEndian(b, v);
        return b;
    }

    static byte[] Tag(string tag) => Message('C', [.. Encoding.ASCII.GetBytes(tag), 0]);

    /// <summary>What Postgres sends for one Bind and Execute of the entry's statements: BindComplete, the rows and
    /// CommandComplete. A feed is empty; a post is its id with a fixed body and author.</summary>
    static byte[] Result(char statement, long id) => statement switch
    {
        'h' => [.. Message('2'), .. Row([0, 0, 0, 1]), .. Tag("SELECT 1")],
        'f' => [.. Message('2'), .. Tag("SELECT 0")],
        'p' => [.. Message('2'), .. Row(Encoding.ASCII.GetBytes(id.ToString(CultureInfo.InvariantCulture)), "body"u8.ToArray(), Int8(0), "author"u8.ToArray(), "3"u8.ToArray()), .. Tag("SELECT 1")],
        'c' => [.. Message('2'), .. Row(Int8(4242), Int8(0)), .. Tag("INSERT 0 1")],
        _ => [.. Message('2'), .. Tag("INSERT 0 1")],
    };
}

/// <summary>The built server against <see cref="FakePg"/>, held to one loop so every client shares it: what a pass puts
/// on the wire, and what happens while Postgres is slow or a packet is lost. Linux only.</summary>
[Collection("Server")]
public sealed class FakePgTests : IDisposable
{
    static readonly string Token = JwtTests.Sign(JwtTests.Payload(sub: "\"7\"", username: "\"tester_7\""));
    readonly FakePg _pg = new();
    readonly ServerFixture _server;
    readonly List<Client> _clients = [];

    public FakePgTests()
    {
        _server = new ServerFixture(_pg.Url, healthy: true, oneLoop: true);
    }

    public void Dispose()
    {
        foreach (Client c in _clients) c.Dispose();
        _server.Dispose();
        _pg.Dispose();
    }

    Client Connect()
    {
        Assert.SkipWhen(_server.Skip != null, _server.Skip ?? "");
        var c = new Client(_server.Port, timeoutMs: 5_000);
        _clients.Add(c);
        return c;
    }

    /// <summary>Clients connected and known to the loop, with the fake holding its answers from now on.</summary>
    Client[] HeldClients(int n)
    {
        Client[] clients = [.. Enumerable.Range(0, n).Select(_ => Connect())];
        foreach (Client c in clients) Assert.Equal(200, c.Get("/health").Status);
        Thread.Sleep(20); // the loop is back in its wait, with nothing owed
        _pg.Hold = true;
        _pg.Clear();
        return clients;
    }

    static string Like(long post) => Client.Request("POST", $"/posts/{post}/like", Token);

    static int Count(string log, char c) => log.Count(x => x == c);

    /// <summary>The batches and groups in a log: each run of statements with the Sync that ends it. The Syncs behind
    /// it (its guard, then nudges while the fake holds its answer) aren't batches.</summary>
    static string[] Batches(string log) => [.. System.Text.RegularExpressions.Regex.Matches(log, "[a-z]+S").Select(m => m.Value[..^1])];

    /// <summary>The server's connections in one loop: 456 readers and 8 writers.</summary>
    const int Connections = 464;

    [Fact]
    public void TheServerRunsOneLoopWithMostOfPostgresConnections()
    {
        Connect();
        var until = Stopwatch.StartNew();
        while (_pg.Conns.Count < Connections && until.ElapsedMilliseconds < 15_000) Thread.Sleep(10);
        Thread.Sleep(200); // and no more than that
        Assert.Equal(Connections, _pg.Conns.Count);
    }

    [Fact]
    public void RequestsThatArriveDuringAPassLeaveUnderOneSyncOnOneConnection()
    {
        Client[] clients = HeldClients(41);
        // the first request wakes the loop: its pass sends that read alone, then sleeps out the pass. The 40 that arrive
        // meanwhile are all there when it looks again
        clients[0].Send(Client.Request("GET", "/posts/1000"));
        for (int i = 1; i <= 40; i++) clients[i].Send(Client.Request("GET", "/posts/" + (1000 + i)));
        _pg.WaitFor(_ => _pg.Conns.Sum(c => Count(c.Log, 'p')) == 41, what: "all 41 reads");

        string[] logs = [.. _pg.Conns.Select(c => c.Log).Where(l => l.Length > 0)];
        // one batch a pass, not one a read: a few passes at most took them, each on a reader of its own
        Assert.InRange(logs.Sum(l => Batches(l).Length), 1, 4);
        Assert.All(logs, l => Assert.Single(Batches(l)));
        // and behind each batch's Sync comes its guard
        Assert.All(logs, l => Assert.StartsWith(Batches(l)[0] + "SS", l, StringComparison.Ordinal));
        Assert.Contains(logs, l => l.Contains("pppppppppp", StringComparison.Ordinal));

        foreach (FakeConn c in _pg.Conns) c.Answer(int.MaxValue);
        for (int i = 0; i <= 40; i++)
            Assert.Equal(1000 + i, clients[i].Read().Json.GetProperty("post").GetProperty("id").GetInt64());
    }

    [Fact]
    public void ABatchNeverHoldsMoreThan64ReadsAndTheRestGoToTheNextReader()
    {
        Client[] clients = HeldClients(151);
        clients[0].Send(Client.Request("GET", "/posts/1"));
        for (int i = 1; i <= 150; i++) clients[i].Send(Client.Request("GET", "/posts/" + (i + 1)));
        _pg.WaitFor(_ => _pg.Conns.Sum(c => Count(c.Log, 'p')) == 151, what: "all 151 reads");
        foreach (string log in _pg.Conns.Select(c => c.Log))
            foreach (string batch in log.Split('S'))
                Assert.InRange(batch.Length, 0, 64);
        // 150 reads in a pass or two can't fit one reader's batch: more than one took some
        Assert.True(_pg.Conns.Count(c => c.Log.Contains('p', StringComparison.Ordinal)) >= 2);

        foreach (FakeConn c in _pg.Conns) c.Answer(int.MaxValue);
        for (int i = 0; i <= 150; i++)
            Assert.Equal(i + 1, clients[i].Read().Json.GetProperty("post").GetProperty("id").GetInt64());
    }

    [Fact]
    public void ReadsOneAfterAnotherTakeTurnsOverTheReadersEachBatchWithItsGuard()
    {
        Client client = Connect();
        Assert.Equal(200, client.Get("/health").Status);
        Thread.Sleep(20);
        _pg.Clear();
        for (int i = 1; i <= 5; i++)
        {
            client.Send(Client.Request("GET", "/posts/" + i));
            Assert.Equal(i, client.Read().Json.GetProperty("post").GetProperty("id").GetInt64());
            Thread.Sleep(5); // the reader is free again; the next read still goes to one that has waited longer
        }
        string[] logs = [.. _pg.Conns.Select(c => c.Log).Where(l => l.Length > 0)];
        Assert.Equal(["pSS", "pSS", "pSS", "pSS", "pSS"], logs);
    }

    [Fact]
    public void AReaderThatStaysSilentIsNudgedEvery5MsThenLessOftenAndNewReadsGoToAnother()
    {
        Client[] clients = HeldClients(3);
        clients[0].Send(Client.Request("GET", "/posts/11"));
        FakeConn silent = _pg.WaitFor(l => l.StartsWith("pS", StringComparison.Ordinal), what: "the first read");

        // a read that comes while the first reader owes its reply goes to another reader
        Thread.Sleep(10);
        clients[1].Send(Client.Request("GET", "/posts/22"));
        _pg.WaitFor(_ => _pg.Conns.Count(c => c.Log.StartsWith("pS", StringComparison.Ordinal)) == 2, what: "the second read on another reader");
        FakeConn other = _pg.Conns.First(c => c.Log.StartsWith("pS", StringComparison.Ordinal) && !ReferenceEquals(c, silent));
        Assert.Equal(1, other.Answer());
        Assert.Equal(22, clients[1].Read().Json.GetProperty("post").GetProperty("id").GetInt64());

        // the batch's Sync and its guard at once; after 5 ms of silence a lone Sync, and another 5 ms later
        _pg.WaitFor(_ => silent.Log.Length >= 5, 1000, "two nudges");
        Assert.StartsWith("pSSSS", silent.Log, StringComparison.Ordinal);
        Assert.InRange(silent.TimeOf(2) - silent.TimeOf(1), 0, 3); // the guard, right behind
        Assert.InRange(silent.TimeOf(3) - silent.TimeOf(1), 4, 60);
        Assert.InRange(silent.TimeOf(4) - silent.TimeOf(3), 3, 60);
        // then they back off to one every 80 ms: about ten in the first 400 ms, where one every 5 ms would be 80
        Thread.Sleep(400);
        Assert.InRange(Count(silent.Log, 'S') - 2, 6, 20);
        Assert.False(clients[0].Hears(0));

        // the reply and every other ReadyForQuery arrive: the read is answered, once, and the reader works on
        Assert.InRange(silent.Answer(int.MaxValue), 8, 30);
        Assert.Equal(11, clients[0].Read().Json.GetProperty("post").GetProperty("id").GetInt64());
        Assert.False(clients[0].Hears(50));
        _pg.Clear();
        clients[2].Send(Client.Request("GET", "/posts/33"));
        _pg.WaitFor(l => l.StartsWith("pS", StringComparison.Ordinal)).Answer();
        Assert.Equal(33, clients[2].Read().Json.GetProperty("post").GetProperty("id").GetInt64());
    }

    [Fact]
    public void WhileReadsWaitThroughRetransmitTimersNewBatchesAreSmallerAndOnMoreReaders()
    {
        Client[] clients = HeldClients(150);
        // 110 reads whose answers don't come: after 350 ms they count as waiting through timers, and each quarter of
        // a second from then the batch size halves
        for (int i = 0; i < 110; i++) clients[i].Send(Client.Request("GET", "/posts/" + (i + 1)));
        _pg.WaitFor(_ => _pg.Conns.Sum(c => Count(c.Log, 'p')) == 110, what: "the 110 reads");
        Assert.All(_pg.Conns, c => Assert.All(Batches(c.Log), batch => Assert.InRange(batch.Length, 1, 64)));
        Assert.Contains(_pg.Conns, c => Batches(c.Log).Any(batch => batch.Length > 16));
        Thread.Sleep(1300);

        _pg.Clear();
        for (int i = 110; i < 150; i++) clients[i].Send(Client.Request("GET", "/posts/" + (i + 1)));
        _pg.WaitFor(_ => _pg.Conns.Sum(c => Count(c.Log, 'p')) == 40, what: "the 40 reads after");
        string[][] after = [.. _pg.Conns.Select(c => Batches(c.Log)).Where(b => b.Length > 0)];
        // 64 at first; halved at least twice by now, so 40 reads no longer fit one or two batches
        Assert.All(after, batches => Assert.All(batches, batch => Assert.InRange(batch.Length, 1, 16)));
        Assert.True(after.Length >= 3, $"{after.Length} readers took the 40 reads");
        // each on a reader with nothing else waiting: none behind the stalled ones
        Assert.All(after, batches => Assert.Single(batches));

        foreach (FakeConn c in _pg.Conns) c.Answer(int.MaxValue);
        for (int i = 110; i < 150; i++)
            Assert.Equal(i + 1, clients[i].Read().Json.GetProperty("post").GetProperty("id").GetInt64());
    }

    [Fact]
    public void ASlowCommitHoldsUpItsOwnGroupNotTheWritesBehindIt()
    {
        Client[] clients = HeldClients(3);
        clients[0].Send(Like(1));
        FakeConn slow = _pg.WaitFor(l => l.StartsWith("lS", StringComparison.Ordinal), what: "the first like");
        Thread.Sleep(10);
        clients[1].Send(Like(2));
        _pg.WaitFor(_ => _pg.Conns.Count(c => c.Log.StartsWith("lS", StringComparison.Ordinal)) == 2, what: "the second like on another writer");
        FakeConn quick = _pg.Conns.First(c => c.Log.StartsWith("lS", StringComparison.Ordinal) && !ReferenceEquals(c, slow));

        // the second writer's commit comes back first: its like is answered while the first still waits
        Assert.Equal(1, quick.Answer());
        Assert.Equal(201, clients[1].Read().Status);
        Assert.False(clients[0].Hears(20));

        Assert.Equal(1, slow.Answer());
        Response r = clients[0].Read();
        Assert.Equal(201, r.Status);
        Assert.Equal(1, r.Json.GetProperty("post_id").GetInt64());
    }

    [Fact]
    public void AGuardsAndANudgesAnswerAreNotTakenForTheNextGroupsCommit()
    {
        Client[] clients = HeldClients(2);
        clients[0].Send(Like(1));
        FakeConn writer = _pg.WaitFor(l => l.StartsWith("lS", StringComparison.Ordinal), what: "the like");
        // its Sync, its guard, and after 20 ms of silence a nudge
        _pg.WaitFor(_ => writer.Log.StartsWith("lSSS", StringComparison.Ordinal), 1000, "the nudge");
        Assert.InRange(writer.TimeOf(3) - writer.TimeOf(1), 15, 120);

        // the commit's answer arrives; the guard's and the nudge's don't yet
        Assert.Equal(1, writer.Answer());
        Assert.Equal(201, clients[0].Read().Status);
        Thread.Sleep(30);
        int stale = writer.Log.Length - 2; // every Sync but the group's own; no more come once nothing is owed
        Thread.Sleep(60);
        Assert.Equal(stale, writer.Log.Length - 2);

        // the next group goes out on the same writer (the first with nothing out), behind the unanswered Syncs
        clients[1].Send(Like(2));
        _pg.WaitFor(_ => Count(writer.Log, 'l') == 2 && writer.Log.EndsWith("lSS", StringComparison.Ordinal), 1000, "the second like on the same writer");
        // now the stale ReadyForQuerys: they commit nothing, so the second like must keep waiting
        Assert.Equal(stale, writer.Answer(stale));
        Assert.False(clients[1].Hears(100));
        // and its own commit answers it
        Assert.Equal(1, writer.Answer());
        Response r = clients[1].Read();
        Assert.Equal(201, r.Status);
        Assert.Equal(2, r.Json.GetProperty("post_id").GetInt64());
    }

    [Fact]
    public void WhenEveryWriterWaitsForItsCommitTheNextWritesQueueAndLeaveAsOneGroup()
    {
        Client[] clients = HeldClients(14);
        for (int i = 0; i < 8; i++)
        {
            clients[i].Send(Like(i + 1));
            _pg.WaitFor(_ => _pg.Conns.Count(c => c.Log.StartsWith("lS", StringComparison.Ordinal)) == i + 1, what: $"like {i + 1} on its own writer");
        }
        // all eight writers have a group out: six more likes wait in the loop
        for (int i = 8; i < 14; i++) clients[i].Send(Like(i + 1));
        Thread.Sleep(30);
        Assert.Equal(8, _pg.Conns.Sum(c => Count(c.Log, 'l')));

        // one commit comes back: the six leave together on that writer, one transaction
        FakeConn first = _pg.Conns.First(c => c.Log.StartsWith("lS", StringComparison.Ordinal));
        Assert.Equal(1, first.Answer());
        _pg.WaitFor(_ => Batches(first.Log).Length == 2, 1000, "the six queued likes as one group");
        Assert.Equal(["l", "llllll"], Batches(first.Log));
        foreach (FakeConn c in _pg.Conns) c.Answer(int.MaxValue);
        for (int i = 0; i < 14; i++)
        {
            Response r = clients[i].Read();
            Assert.Equal(201, r.Status);
            Assert.Equal(i + 1, r.Json.GetProperty("post_id").GetInt64());
        }
    }

    [Fact]
    public void ReadsNeverWaitBehindACommit()
    {
        Client[] clients = HeldClients(2);
        clients[0].Send(Like(1));
        FakeConn writer = _pg.WaitFor(l => l.StartsWith("lS", StringComparison.Ordinal), what: "the like");
        clients[1].Send(Client.Request("GET", "/posts/5"));
        FakeConn reader = _pg.WaitFor(l => l.StartsWith("pS", StringComparison.Ordinal), what: "the read");
        Assert.NotSame(writer, reader);
        Assert.Equal(1, reader.Answer());
        Assert.Equal(5, clients[1].Read().Json.GetProperty("post").GetProperty("id").GetInt64());
        Assert.False(clients[0].Hears(20));
        writer.Answer(int.MaxValue);
        Assert.Equal(201, clients[0].Read().Status);
    }

    [Fact]
    public void AWriterLostMidCommitFailsItsGroupAndTheOthersCarryOn()
    {
        Client[] clients = HeldClients(3);
        clients[0].Send(Like(1));
        FakeConn doomed = _pg.WaitFor(l => l.StartsWith("lS", StringComparison.Ordinal), what: "the first like");
        Thread.Sleep(10);
        clients[1].Send(Like(2));
        _pg.WaitFor(_ => _pg.Conns.Count(c => c.Log.StartsWith("lS", StringComparison.Ordinal)) == 2, what: "the second like on another writer");
        FakeConn other = _pg.Conns.First(c => c.Log.StartsWith("lS", StringComparison.Ordinal) && !ReferenceEquals(c, doomed));

        doomed.Close();
        Assert.Equal(500, clients[0].Read().Status);
        Assert.False(clients[1].Hears(20));
        Assert.Equal(1, other.Answer());
        Assert.Equal(201, clients[1].Read().Status);

        // the lost writer's slot reconnects within a second or so, and writes go on meanwhile
        _pg.Hold = false;
        clients[2].Send(Like(3));
        Assert.Equal(201, clients[2].Read().Status);
        var until = Stopwatch.StartNew();
        while (_pg.Conns.Count < Connections + 1 && until.ElapsedMilliseconds < 5000) Thread.Sleep(20);
        Assert.Equal(Connections + 1, _pg.Conns.Count);
    }
}
