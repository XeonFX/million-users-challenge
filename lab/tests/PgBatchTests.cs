using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using FsCheck;
using FsCheck.Fluent;
using Xunit;

namespace MillionUsers.Epoll.Tests;

/// <summary>Reads batched under one Sync: what goes on the wire, and how the replies split back into one result per
/// read.</summary>
public sealed class PgBatchTests
{
    static Pg NewPg() => new(new PgSettings("localhost", 5432, "u", "", "d"), writer: false);
    const long T = 1_000_000_000;

    /// <summary>The message types in a run of protocol messages, and each message's body.</summary>
    static List<(char Type, byte[] Body)> Messages(ReadOnlySpan<byte> data)
    {
        var all = new List<(char, byte[])>();
        while (data.Length > 0)
        {
            Assert.True(data.Length >= 5, "a cut message");
            int length = BinaryPrimitives.ReadInt32BigEndian(data[1..]);
            all.Add(((char)data[0], data.Slice(5, length - 4).ToArray()));
            data = data[(1 + length)..];
        }
        return all;
    }

    /// <summary>A Bind message's body, field by field, as Postgres reads it.</summary>
    static (byte Statement, short[] ParameterFormats, List<byte[]> Parameters, short[] ResultFormats) ParseBind(byte[] bind)
    {
        int at = Array.IndexOf(bind, (byte)0) + 1; // the portal's name
        int end = Array.IndexOf(bind, (byte)0, at);
        Assert.Equal(1, end - at); // the statement's one-letter name
        byte statement = bind[at];
        at = end + 1;
        short[] Formats()
        {
            short n = BinaryPrimitives.ReadInt16BigEndian(bind.AsSpan(at));
            at += 2;
            var f = new short[n];
            for (int i = 0; i < n; i++, at += 2) f[i] = BinaryPrimitives.ReadInt16BigEndian(bind.AsSpan(at));
            return f;
        }
        short[] parameterFormats = Formats();
        short count = BinaryPrimitives.ReadInt16BigEndian(bind.AsSpan(at));
        at += 2;
        var parameters = new List<byte[]>();
        for (int i = 0; i < count; i++)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(bind.AsSpan(at));
            parameters.Add(bind.AsSpan(at + 4, length).ToArray());
            at += 4 + length;
        }
        short[] resultFormats = Formats();
        Assert.Equal(bind.Length, at); // nothing after the result formats
        return (statement, parameterFormats, parameters, resultFormats);
    }

    static byte[] Message(char type, params byte[] body)
    {
        var m = new byte[5 + body.Length];
        m[0] = (byte)type;
        BinaryPrimitives.WriteInt32BigEndian(m.AsSpan(1), 4 + body.Length);
        body.CopyTo(m, 5);
        return m;
    }

    static readonly byte[] Bound = Message('2'), Row = Message('D', 0, 1, 0, 0, 0, 1, 7), Done = Message('C', "SELECT 1\0"u8.ToArray()),
        Failed = Message('E', "SERROR\0C57014\0Mcanceled\0\0"u8.ToArray()), Ready = Message('Z', (byte)'I'), Notice = Message('N', 0);

    [Fact]
    public void ReadsInOnePassShareOneSync()
    {
        Pg pg = NewPg();
        pg.Read(Pg.Feed, 0, 10, 1);
        pg.Read(Pg.Post, 42, 11, 1);
        pg.Read(Pg.Health, 0, 12, 1);
        Assert.Equal(3, pg.Open);
        Assert.True(pg.Unsent.IsEmpty); // nothing leaves before the Sync
        Assert.False(pg.WantsWrite);

        pg.Seal(T);
        var sent = Messages(pg.Unsent);
        Assert.Equal("BEBEBES", new string(sent.Select(m => m.Type).ToArray()));
        Assert.Equal(0, pg.Open);
        Assert.True(pg.WantsWrite);
        // no Describe: the rows come back without a RowDescription
        Assert.DoesNotContain(sent, m => m.Type == 'D');

        // the Post bind carries its id, binary; a post's columns come back in the shortest format: id and like_count
        // as text, created_at binary, body and username text
        var feed = ParseBind(sent[0].Body);
        var post = ParseBind(sent[2].Body);
        var health = ParseBind(sent[4].Body);
        Assert.Equal(((byte)Pg.Feed, 0), (feed.Statement, feed.Parameters.Count));
        Assert.Equal((byte)Pg.Post, post.Statement);
        Assert.Equal(42, BinaryPrimitives.ReadInt64BigEndian(Assert.Single(post.Parameters)));
        Assert.Equal([1], post.ParameterFormats);
        Assert.Equal([0, 0, 1, 0, 0], feed.ResultFormats);
        Assert.Equal([0, 0, 1, 0, 0], post.ResultFormats);
        Assert.Equal([1], health.ResultFormats);

        Waiter a = pg.TakeWaiter(), b = pg.TakeWaiter(), c = pg.TakeWaiter();
        Assert.Equal((10, false), (a.Fd, a.Last));
        Assert.Equal((11, 42L, false), (b.Fd, b.Id, b.Last));
        Assert.Equal((12, true), (c.Fd, c.Last));
    }

    [Fact]
    public void SealingNothingSendsNothing()
    {
        Pg pg = NewPg();
        pg.Seal(T);
        Assert.False(pg.Owes);
        Assert.True(pg.Unsent.IsEmpty);
    }

    [Fact]
    public void EachSealIsItsOwnBatch()
    {
        Pg pg = NewPg();
        pg.Read(Pg.Feed, 0, 1, 1);
        pg.Seal(T);
        pg.Read(Pg.Feed, 0, 2, 1);
        pg.Read(Pg.Feed, 0, 3, 1);
        pg.Seal(T);
        Assert.Equal("BESBEBES", new string(Messages(pg.Unsent).Select(m => m.Type).ToArray()));
        Assert.True(pg.Owes);
        Assert.Equal([true, false, true], new[] { pg.TakeWaiter(), pg.TakeWaiter(), pg.TakeWaiter() }.Select(w => w.Last));
    }

    [Fact]
    public void BatchEndsSurviveTheWaiterRingGrowing()
    {
        Pg pg = NewPg();
        // start the ring away from 0 so growing has to unwrap it
        for (int i = 0; i < 700; i++) pg.Read(Pg.Post, i, i, 0);
        pg.Seal(T);
        for (int i = 0; i < 700; i++) pg.TakeWaiter();
        for (int i = 0; i < 3000; i++)
        {
            pg.Read(Pg.Post, i + 1, i, 0);
            if (i % 7 == 6) pg.Seal(T);
        }
        pg.Seal(T);
        for (int i = 0; i < 3000; i++)
        {
            Waiter w = pg.TakeWaiter();
            Assert.Equal(i, w.Fd);
            Assert.Equal(i + 1, w.Id);
            Assert.Equal(i % 7 == 6 || i == 2999, w.Last);
        }
        Assert.Equal(0, pg.Pending);
    }

    [Fact]
    public void RepliesSplitIntoOneResultPerReadThenTheBatchEnd()
    {
        byte[] stream = [.. Bound, .. Row, .. Row, .. Done, .. Notice, .. Bound, .. Done, .. Ready];
        var ends = new List<(char End, int Length)>();
        ReadOnlySpan<byte> rest = stream;
        while (true)
        {
            int n = Pg.ResultLength(rest, out byte end);
            if (n == 0) break;
            ends.Add(((char)end, n));
            rest = rest[n..];
        }
        Assert.Equal([('C', Bound.Length + 2 * Row.Length + Done.Length), ('C', Notice.Length + Bound.Length + Done.Length), ('Z', Ready.Length)], ends);
        Assert.True(rest.IsEmpty);
    }

    [Fact]
    public void AnErrorEndsItsReadsResult()
    {
        byte[] stream = [.. Bound, .. Row, .. Failed, .. Ready];
        Assert.Equal(Bound.Length + Row.Length + Failed.Length, Pg.ResultLength(stream, out byte end));
        Assert.Equal((byte)'E', end);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(-1)]
    public void AResultCutShortIsNotReady(int cut)
    {
        byte[] stream = [.. Bound, .. Row, .. Done];
        int keep = cut < 0 ? stream.Length - 1 : cut;
        Assert.Equal(0, Pg.ResultLength(stream.AsSpan(0, keep), out byte end));
        Assert.Equal(0, end);
    }

    /// <summary>However the bytes arrive, the results come out whole and in order.</summary>
    [Fact]
    public void ResultsAreTheSameHoweverTheBytesArrive()
    {
        byte[][] parts = [Bound, Row, Done, Bound, Done, Bound, Row, Row, Row, Done, Bound, Failed, Ready, Bound, Done, Ready];
        byte[] stream = parts.SelectMany(p => p).ToArray();
        List<(char, int)> Split(IEnumerable<int> chunks)
        {
            var got = new List<(char, int)>();
            var buffer = new List<byte>();
            int at = 0;
            foreach (int c in chunks.Append(stream.Length))
            {
                int take = Math.Min(c, stream.Length - at);
                buffer.AddRange(stream.AsSpan(at, take).ToArray());
                at += take;
                while (true)
                {
                    int n = Pg.ResultLength(buffer.ToArray(), out byte end);
                    if (n == 0) break;
                    got.Add(((char)end, n));
                    buffer.RemoveRange(0, n);
                }
            }
            Assert.Empty(buffer);
            return got;
        }
        var whole = Split([]);
        Assert.Equal("CCCEZCZ", new string(whole.Select(r => r.Item1).ToArray()));
        Prop.ForAll(Gen.Choose(1, 40).ListOf().ToArbitrary(), chunks => Assert.Equal(whole, Split(chunks))).QuickCheckThrowOnFailure();
    }
}
