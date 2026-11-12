using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FsCheck;
using FsCheck.Fluent;
using Xunit;

namespace MillionUsers.Epoll.Tests;

/// <summary>What a loop pass leaves on a Postgres connection: which reader takes its reads, how many readers take
/// turns, when a connection that stays silent is nudged with a Sync, the order connections come back in, and how
/// long the loop may wait for the next event.</summary>
public sealed class PassTests
{
    const long T = 1_000_000_000; // a Stopwatch timestamp well past 0
    /// <summary>50 ms in Stopwatch ticks (the waits of <see cref="Loop.WaitNanos"/> are checked against it).</summary>
    static readonly long N = Stopwatch.Frequency / 20;
    /// <summary>The silence before a reader counts as stalled.</summary>
    static readonly long Stall = Ms(Pg.StallAfterMs);

    static long Ms(long ms) => ms * Stopwatch.Frequency / 1000;

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

    /// <summary>A reader whose last batch, of <paramref name="reads"/> reads, was sealed at <paramref name="at"/>;
    /// answered, so it is free again, unless <paramref name="answered"/> says not.</summary>
    static Pg Used(long at, int reads = 1, bool answered = true)
    {
        Pg pg = Reader();
        Reads(pg, reads);
        pg.Seal(at);
        if (answered) Take(pg, reads);
        return pg;
    }

    // ---- which reader takes a pass's reads

    [Fact]
    public void FreeReadersTakeTurnsTheOneUsedLongestAgoFirst()
    {
        Pg a = Used(T + 30), b = Used(T + 10), c = Used(T + 20);
        Assert.Same(b, Loop.NextReader([a, b, c], 3, T + 100, 3));
        Reads(b, 1);
        b.Seal(T + 100);
        Take(b, 1);
        Assert.Same(c, Loop.NextReader([a, b, c], 3, T + 101, 3));
    }

    [Fact]
    public void AReaderNeverUsedGoesBeforeAnyThatWasAndTheFirstOfEqualsWins()
    {
        Pg a = Used(T), b = Reader(), c = Reader();
        Assert.Same(b, Loop.NextReader([a, b, c], 3, T + 1, 3));
    }

    [Fact]
    public void OnlyTheFirstReadersTakeTurnsAndTheRestAreTakenWhenThoseAreBusy()
    {
        Pg a = Used(T + 5), b = Used(T + 9), c = Reader(), d = Reader();
        // c and d were never used, but only two readers take turns: the older of a and b
        Assert.Same(a, Loop.NextReader([a, b, c, d], 4, T + 20, 2));
        Reads(a, 1);
        a.Seal(T + 20);
        Assert.Same(b, Loop.NextReader([a, b, c, d], 4, T + 21, 2));
        Reads(b, 1);
        b.Seal(T + 21);
        // both busy: the first free one past them, not the least loaded of the busy
        Assert.Same(c, Loop.NextReader([a, b, c, d], 4, T + 22, 2));
        Reads(c, 2);
        c.Seal(T + 22);
        Assert.Same(d, Loop.NextReader([a, b, c, d], 4, T + 23, 2));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(1000)]
    public void HoweverManyTakeTurnsAFreeReaderIsFoundWhileThereIsOne(int turns)
    {
        Pg a = Used(T, answered: false), b = Used(T, answered: false), c = Used(T + 1), d = Used(T, answered: false);
        Assert.Same(c, Loop.NextReader([a, b, c, d], 4, T + 2, turns));
    }

    [Fact]
    public void WhenNoReaderIsFreeTheOneWithTheFewestReadsWaitingTakesThePassTheFirstOfEquals()
    {
        Pg a = Used(T, 3, answered: false), b = Used(T, 1, answered: false), c = Used(T, 2, answered: false);
        Assert.Same(b, Loop.NextReader([a, b, c], 3, T + 1, 3));
        Reads(b, 2);
        b.Seal(T + 1); // a and b both hold 3 now
        Assert.Same(c, Loop.NextReader([a, b, c], 3, T + 2, 3));
        Reads(c, 1);
        c.Seal(T + 2); // all three hold 3
        Assert.Same(a, Loop.NextReader([a, b, c], 3, T + 3, 3));
    }

    [Fact]
    public void AStalledReaderIsPassedOverForOneThatAnswersHoweverFewReadsItHolds()
    {
        // a went silent on one read; b holds five but was sent its batch just now
        Pg a = Used(T, 1, answered: false);
        long now = T + Stall + 1;
        Pg b = Used(now, 5, answered: false);
        Assert.True(a.Stalled(now));
        Assert.False(b.Stalled(now));
        Assert.Same(b, Loop.NextReader([a, b], 2, now, 2));
    }

    [Fact]
    public void AReaderSilentForExactlyTheStallTimeIsNotYetStalled()
    {
        Pg a = Used(T, 1, answered: false);
        Pg b = Used(T + Stall, 5, answered: false);
        Assert.False(a.Stalled(T + Stall));
        Assert.Same(a, Loop.NextReader([a, b], 2, T + Stall, 2));
        Assert.True(a.Stalled(T + Stall + 1));
        Assert.Same(b, Loop.NextReader([a, b], 2, T + Stall + 1, 2));
    }

    [Fact]
    public void WhenEveryReaderIsStalledTheFewestWaitingTakesThePass()
    {
        Pg a = Used(T, 4, answered: false), b = Used(T, 2, answered: false);
        long now = T + 10 * Stall;
        Assert.True(a.Stalled(now) && b.Stalled(now));
        Assert.Same(b, Loop.NextReader([a, b], 2, now, 2));
    }

    [Fact]
    public void ADownReaderIsPassedOverAndNoReaderIsNull()
    {
        Pg a = Reader(), b = Used(T, 5, answered: false);
        a.Broken = true;
        Assert.Same(b, Loop.NextReader([a, b], 2, T + 1, 2));
        b.Broken = true;
        Assert.Null(Loop.NextReader([a, b], 2, T + 1, 2));
        Assert.Null(Loop.NextReader([], 0, T + 1, 8));
    }

    [Fact]
    public void TheWritersAfterTheReadersNeverTakeReads()
    {
        Pg reader = Used(T, 40, answered: false), writer = Writer();
        Assert.Same(reader, Loop.NextReader([reader, writer], 1, T + 1, 8));
    }

    // ---- a stalled reader

    [Fact]
    public void OnlyAReaderThatOwesRepliesCanBeSilentOrStalled()
    {
        Pg pg = Reader();
        Assert.Equal(0, pg.SilentFor(T + 100 * Stall));
        Assert.False(pg.Stalled(T + 100 * Stall));

        // reads still in the open batch haven't been sent: nothing is owed for them
        Reads(pg, 2);
        Assert.Equal(0, pg.SilentFor(T + 100 * Stall));

        pg.Seal(T);
        Assert.Equal(7, pg.SilentFor(T + 7));
        Assert.True(pg.Stalled(T + Stall + 1));

        Take(pg, 2); // answered
        Assert.Equal(0, pg.SilentFor(T + 100 * Stall));
        Assert.False(pg.Stalled(T + 100 * Stall));

        Reads(pg, 1);
        pg.Seal(T + 500 * Stall);
        pg.Broken = true;
        Assert.False(pg.Stalled(T + 900 * Stall));
    }

    // ---- what sealing a batch leaves

    [Fact]
    public void SealingStampsTheReaderWithTheTimeOfItsLastBatch()
    {
        Pg pg = Reader();
        Assert.Equal(0, pg.LastUsed);
        Reads(pg, 2);
        Assert.Equal(0, pg.LastUsed); // an open batch hasn't been sent
        pg.Seal(T + 3);
        Assert.Equal(T + 3, pg.LastUsed);
        pg.Seal(T + 5); // nothing to seal
        Assert.Equal(T + 3, pg.LastUsed);
        Reads(pg, 1);
        pg.Seal(T + 9);
        Assert.Equal(T + 9, pg.LastUsed);
    }

    /// <summary>Puts what Postgres sent into the connection's receive buffer, as its Receive does from the socket.</summary>
    static void Arrives(Pg pg, params byte[][] messages)
    {
        const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var buffer = (byte[])typeof(Pg).GetField("_in", Private)!.GetValue(pg)!;
        var end = typeof(Pg).GetField("_inEnd", Private)!;
        int at = (int)end.GetValue(pg)!;
        foreach (byte[] m in messages)
        {
            m.CopyTo(buffer, at);
            at += m.Length;
        }
        end.SetValue(pg, at);
    }

    static readonly byte[] Bound = [(byte)'2', 0, 0, 0, 4], Inserted = [(byte)'C', 0, 0, 0, 15, .. "INSERT 0 1\0"u8],
        Ready = [(byte)'Z', 0, 0, 0, 5, (byte)'I'];

    /// <summary>A writer answers its writes at the ReadyForQuery of their group's Sync and no other: a nudge's
    /// ReadyForQuery commits nothing, so it must not be taken for the next group's.</summary>
    [Fact]
    public void ANudgesAnswerIsNeverTakenForAGroupsCommit()
    {
        Pg pg = Writer();
        pg.Group.Add(new WriteJob { Kind = WriteJob.Like, UserId = 7, PostId = 9 });
        pg.SendGroup(T);
        pg.Nudge(T + N);
        pg.Nudge(T + 3 * N); // the group's Sync and two nudges: three ReadyForQuerys are owed

        Arrives(pg, Bound, Inserted, Ready);
        Assert.True(pg.NextReply(out ReadOnlySpan<byte> reply, out bool group));
        Assert.True(group);
        Assert.Equal(Bound.Length + Inserted.Length + Ready.Length, reply.Length);
        Assert.False(pg.NextReply(out _, out _)); // nothing more has come
        pg.Group.Clear(); // the loop answered the write

        // the next group leaves before the nudges' answers are in, and is nudged once itself
        pg.Group.Add(new WriteJob { Kind = WriteJob.Like, UserId = 8, PostId = 9 });
        pg.SendGroup(T + 4 * N);
        pg.Nudge(T + 5 * N);
        Arrives(pg, Ready);
        Assert.True(pg.NextReply(out _, out group));
        Assert.False(group); // the first nudge
        Arrives(pg, Ready);
        Assert.True(pg.NextReply(out _, out group));
        Assert.False(group); // the second
        Arrives(pg, Bound, Inserted, Ready, Ready);
        Assert.True(pg.NextReply(out _, out group));
        Assert.True(group); // the second group's commit
        Assert.True(pg.NextReply(out _, out group));
        Assert.False(group); // and its nudge
        Assert.False(pg.NextReply(out _, out _));
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
    public void ANudgeDoesNotCountAsHearingFromPostgres()
    {
        Pg pg = Reader();
        Reads(pg, 1);
        pg.Seal(T);
        pg.Nudge(T + N);
        // silent since the batch left, however many Syncs went after it: the reader is still passed over
        Assert.Equal(N + 7, pg.SilentFor(T + N + 7));
        Assert.True(pg.Stalled(T + N + 7));
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
        // and the reader has been silent since the first batch, not the second
        Assert.Equal(N, pg.SilentFor(T + N));
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
                        // the loop ends a pass by sealing its batch, then nudges: no batch is open at a nudge
                        if (pg.Open > 0) syncs++;
                        sent += pg.Open;
                        pg.Seal(now);
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
                Assert.Equal(sent - answered > 0, pg.SilentFor(now + 1) > 0);
            }
            string all = Types(pg.Unsent);
            Assert.Equal(syncs, all.Count(t => t == 'S'));
            if (all.Length > 0) Assert.EndsWith("S", all, StringComparison.Ordinal);
        }).QuickCheckThrowOnFailure();
    }

    // ---- how many readers take turns

    [Theory]
    [InlineData(0, 8)]    // a quiet loop: the fewest
    [InlineData(1, 8)]
    [InlineData(2, 8)]
    [InlineData(3, 10)]   // from here, twice the busy ones and four more
    [InlineData(10, 24)]
    [InlineData(112, 228)] // more than there are: NextReader takes it as all of them
    public void TwiceAsManyReadersTakeTurnsAsWereBusyLatelyAndFourMoreEightAtLeast(int busyPeak, int turns)
    {
        Assert.Equal(turns, Loop.Turns(busyPeak));
    }

    [Fact]
    public void TheBusyPeakFollowsABurstAtOnceAndFadesByAQuarterEachCountAfterIt()
    {
        Assert.Equal(40, Loop.Peak(40, 0));    // up at once
        Assert.Equal(40, Loop.Peak(40, 12));
        Assert.Equal(30, Loop.Peak(2, 40));    // then three quarters of the peak, while that is more than now
        Assert.Equal(22, Loop.Peak(2, 30));
        Assert.Equal(16, Loop.Peak(16, 20));   // or the count now
        Assert.Equal(0, Loop.Peak(0, 1));      // and down to nothing
        // a burst of 40 busy readers on a loop that then has 2: the turns are back at the fewest within ten counts
        int peak = 40, counts = 0;
        while (Loop.Turns(peak) > Loop.FewestTurns)
        {
            peak = Loop.Peak(2, peak);
            counts++;
        }
        Assert.InRange(counts, 5, 10);
    }

    // ---- the order connections come back in

    [Fact]
    public void WritersReconnectBeforeReadersEachInItsOwnPlace()
    {
        // three readers, then two writers
        Assert.Equal([2, 3, 4, 0, 1], Enumerable.Range(0, 5).Select(i => Loop.ReconnectRank(i, readers: 3, all: 5)));
        Prop.ForAll((from readers in Gen.Choose(1, 500) from writers in Gen.Choose(1, 16) select (readers, writers)).ToArbitrary(), c =>
        {
            int all = c.readers + c.writers;
            int[] ranks = [.. Enumerable.Range(0, all).Select(i => Loop.ReconnectRank(i, c.readers, all))];
            // every place once, and every writer ahead of every reader
            Assert.Equal(Enumerable.Range(0, all), ranks.Order());
            Assert.True(ranks.Skip(c.readers).Max() < ranks.Take(c.readers).Min());
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
