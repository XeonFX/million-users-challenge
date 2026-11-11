using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FsCheck;
using FsCheck.Fluent;
using Xunit;

namespace MillionUsers.Epoll.Tests;

/// <summary>What a loop pass leaves on a Postgres connection: which reader takes its reads, when a connection that
/// stays silent is nudged with a Sync, and how long the loop may wait for the next event.</summary>
public sealed class PassTests
{
    const long T = 1_000_000_000; // a Stopwatch timestamp well past 0
    /// <summary>The silence before the first nudge: 50 ms, in Stopwatch ticks.</summary>
    static readonly long N = Stopwatch.Frequency / 20;

    static Pg Reader() => new(new PgSettings("localhost", 5432, "u", "", "d"), writer: false) { Broken = false };
    static Pg Writer() => new(new PgSettings("localhost", 5432, "u", "", "d"), writer: true) { Broken = false };

    static string Types(ReadOnlySpan<byte> data)
    {
        var types = new List<char>();
        while (data.Length > 0)
        {
            Assert.True(data.Length >= 5, "a cut message");
            types.Add((char)data[0]);
            data = data[(1 + BinaryPrimitives.ReadInt32BigEndian(data[1..]))..];
        }
        return new string([.. types]);
    }

    static void Reads(Pg pg, int n)
    {
        for (int i = 0; i < n; i++) pg.Read(Pg.Post, i + 1, 100 + i, 1);
    }

    static void Take(Pg pg, int n)
    {
        for (int i = 0; i < n; i++) pg.TakeWaiter();
    }

    // ---- which reader takes a pass's reads

    [Fact]
    public void APassGoesToTheReaderWithTheFewestReadsWaiting()
    {
        Pg a = Reader(), b = Reader(), c = Reader();
        Reads(a, 3);
        Reads(b, 1);
        Reads(c, 2);
        Assert.Same(b, Loop.LeastLoaded([a, b, c], 3, T));
        Take(a, 3);
        Assert.Same(a, Loop.LeastLoaded([a, b, c], 3, T));
    }

    [Fact]
    public void AmongEquallyLoadedReadersTheFirstCarriesThePass()
    {
        Pg a = Reader(), b = Reader(), c = Reader();
        Assert.Same(a, Loop.LeastLoaded([a, b, c], 3, T));
        Reads(a, 1);
        Assert.Same(b, Loop.LeastLoaded([a, b, c], 3, T));
        Reads(b, 1);
        Reads(c, 1);
        Assert.Same(a, Loop.LeastLoaded([a, b, c], 3, T));
    }

    [Fact]
    public void ADownReaderIsPassedOverAndNoReaderIsNull()
    {
        Pg a = Reader(), b = Reader();
        Reads(b, 5);
        a.Broken = true;
        Assert.Same(b, Loop.LeastLoaded([a, b], 2, T));
        b.Broken = true;
        Assert.Null(Loop.LeastLoaded([a, b], 2, T));
        Assert.Null(Loop.LeastLoaded([], 0, T));
    }

    [Fact]
    public void TheWritersAfterTheReadersNeverTakeReads()
    {
        Pg reader = Reader(), writer = Writer();
        Reads(reader, 40);
        Assert.Same(reader, Loop.LeastLoaded([reader, writer], 1, T));
    }

    // ---- nudging a silent connection

    [Fact]
    public void AConnectionThatOwesNothingIsNeverNudged()
    {
        Pg pg = Reader();
        Assert.False(pg.Owes);
        Assert.Equal(long.MaxValue, pg.Nudge(T + 1000 * N));
        Assert.True(pg.Unsent.IsEmpty);

        // reads still in the open batch haven't been sent: nothing is owed for them
        Reads(pg, 3);
        Assert.False(pg.Owes);
        Assert.Equal(long.MaxValue, pg.Nudge(T + 1000 * N));
        Assert.True(pg.Unsent.IsEmpty);
    }

    [Fact]
    public void ASilentReaderIsNudgedAfter50MsWithOneSync()
    {
        Pg pg = Reader();
        Reads(pg, 2);
        pg.Seal(T);
        Assert.True(pg.Owes);
        Assert.Equal("BEBES", Types(pg.Unsent));

        Assert.Equal(T + N, pg.Nudge(T));
        Assert.Equal(T + N, pg.Nudge(T + N - 1));
        Assert.Equal("BEBES", Types(pg.Unsent)); // not yet

        long next = pg.Nudge(T + N);
        Assert.Equal("BEBESS", Types(pg.Unsent));
        Assert.True(pg.WantsWrite);
        Assert.Equal(T + N + 2 * N, next);
        // the reads are still waiting, in order
        Assert.Equal(2, pg.Pending);
    }

    [Fact]
    public void EachNudgeWaitsTwiceAsLongAsTheLast()
    {
        Pg pg = Reader();
        Reads(pg, 1);
        pg.Seal(T);
        long now = T, gap = N;
        for (int k = 0; k < 6; k++)
        {
            now += gap;
            Assert.Equal(now, pg.Nudge(now - 1));
            Assert.Equal(now + 2 * gap, pg.Nudge(now));
            gap *= 2;
        }
        Assert.Equal("BES" + new string('S', 6), Types(pg.Unsent));
    }

    [Fact]
    public void ALateNudgeCountsItsSilenceFromWhenItWasSent()
    {
        Pg pg = Reader();
        Reads(pg, 1);
        pg.Seal(T);
        // the loop looked late: the next nudge is due 2 N after this one, not after its deadline
        Assert.Equal(T + 5 * N + 2 * N, pg.Nudge(T + 5 * N));
    }

    [Fact]
    public void ABatchSentBehindAnUnansweredOneDoesNotRestartTheClock()
    {
        Pg pg = Reader();
        Reads(pg, 1);
        pg.Seal(T);
        Reads(pg, 1);
        pg.Seal(T + N / 2);
        Assert.Equal(T + N, pg.Nudge(T + N / 2));
        pg.Nudge(T + N);
        Assert.Equal("BESBESS", Types(pg.Unsent));
    }

    [Fact]
    public void ABatchSentWithNothingOwedStartsTheClock()
    {
        Pg pg = Reader();
        Reads(pg, 1);
        pg.Seal(T);
        Take(pg, 1); // its reply came
        Assert.False(pg.Owes);
        Assert.Equal(long.MaxValue, pg.Nudge(T + 100 * N));

        Reads(pg, 1);
        pg.Seal(T + 200 * N);
        Assert.Equal(T + 200 * N + N, pg.Nudge(T + 200 * N));
    }

    [Fact]
    public void ADownConnectionIsNotNudged()
    {
        Pg pg = Reader();
        Reads(pg, 1);
        pg.Seal(T);
        pg.Broken = true;
        Assert.Equal(long.MaxValue, pg.Nudge(T + 10 * N));
        Assert.Equal("BES", Types(pg.Unsent));
    }

    [Fact]
    public void ClosingForgetsTheBackoffAndWhatWasOwed()
    {
        Pg pg = Reader();
        Reads(pg, 1);
        pg.Seal(T);
        pg.Nudge(T + N);
        pg.Nudge(T + 3 * N);
        pg.Close();
        Assert.True(pg.Unsent.IsEmpty);
        Take(pg, 1); // the loop fails what it owed
        pg.Broken = false; // reconnected
        Reads(pg, 1);
        pg.Seal(T + 100 * N);
        Assert.Equal(T + 100 * N + N, pg.Nudge(T + 100 * N));
    }

    [Fact]
    public void ASilentWriterIsNudgedBehindItsGroup()
    {
        Pg pg = Writer();
        Assert.Equal(long.MaxValue, pg.Nudge(T + 10 * N));
        pg.Group.Add(new WriteJob { Kind = WriteJob.Like, UserId = 7, PostId = 9 });
        pg.Group.Add(new WriteJob { Kind = WriteJob.Create, UserId = 7, BodyLength = 0 });
        pg.SendGroup(T);
        Assert.True(pg.Owes);
        Assert.Equal("BEBES", Types(pg.Unsent));
        Assert.Equal(T + N, pg.Nudge(T + N - 1));
        Assert.Equal(T + 3 * N, pg.Nudge(T + N));
        Assert.Equal("BEBESS", Types(pg.Unsent));
    }

    /// <summary>Whatever a connection is asked to do, what it has queued is whole messages, with one Sync per sealed
    /// batch and one per nudge, and it is nudged only while it owes something and has been silent long enough.</summary>
    [Fact]
    public void WhateverHappensTheWireHoldsWholeMessagesAndOneSyncPerBatchAndNudge()
    {
        var steps = from kind in Gen.Choose(0, 3)
                    from n in Gen.Choose(1, 5)
                    from ms in Gen.Choose(0, 120)
                    select (kind, n, ms);
        Prop.ForAll(steps.ListOf().ToArbitrary(), list =>
        {
            Pg pg = Reader();
            long now = T;
            int syncs = 0, sent = 0, answered = 0;
            foreach (var (kind, n, ms) in list)
            {
                now += ms * Stopwatch.Frequency / 1000;
                switch (kind)
                {
                    case 0:
                        Reads(pg, n);
                        break;
                    case 1:
                        if (pg.Open > 0) syncs++;
                        sent += pg.Open;
                        pg.Seal(now);
                        break;
                    case 2:
                        int take = Math.Min(n, sent - answered);
                        Take(pg, take);
                        answered += take;
                        break;
                    default:
                        bool owed = pg.Owes;
                        int before = pg.Unsent.Length;
                        long due = pg.Nudge(now);
                        bool nudged = pg.Unsent.Length > before;
                        if (nudged) syncs++;
                        Assert.Equal(owed, due != long.MaxValue);
                        if (!owed) Assert.False(nudged);
                        if (owed) Assert.True(due > now, "a nudge that is due is sent, and the next one is later");
                        break;
                }
                Assert.Equal(sent - answered > 0, pg.Owes);
            }
            Assert.Equal(syncs, Types(pg.Unsent).Count(t => t == 'S'));
        }).QuickCheckThrowOnFailure();
    }

    // ---- how long the loop may wait

    [Fact]
    public void TheLoopWaitsForEventsUntilTheNextNudgeAndASecondAtMostWhileAConnectionIsDown()
    {
        long micro = Stopwatch.Frequency / 1_000_000;
        Assert.Equal(-1, Loop.WaitNanos(long.MaxValue, T, false));
        Assert.Equal(1_000_000_000, Loop.WaitNanos(long.MaxValue, T, true));
        Assert.Equal(50_000_000, Loop.WaitNanos(T + N, T, false));
        Assert.Equal(50_000_000, Loop.WaitNanos(T + N, T, true));
        Assert.Equal(1000, Loop.WaitNanos(T + micro, T, false));
        Assert.Equal(0, Loop.WaitNanos(T, T, false));
        Assert.Equal(0, Loop.WaitNanos(T - 5, T, false));
        Assert.Equal(1_000_000_000, Loop.WaitNanos(T + 3600 * Stopwatch.Frequency, T, false));
    }

    [Fact]
    public void AWaitNeverEndsBeforeItsDeadline()
    {
        Prop.ForAll(Gen.Choose(1, 2_000_000).ToArbitrary(), micros =>
        {
            long due = T + micros * Stopwatch.Frequency / 1_000_000;
            long nanos = Loop.WaitNanos(due, T, false);
            // rounded up to the next nanosecond, never down, and a second at most
            Assert.True(nanos * Stopwatch.Frequency >= Math.Min(due - T, Stopwatch.Frequency) * 1_000_000_000L - Stopwatch.Frequency);
            Assert.InRange(nanos, 1, 1_000_000_000);
        }).QuickCheckThrowOnFailure();
    }
}
