using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using FsCheck;
using FsCheck.Fluent;
using Xunit;

namespace MillionUsers.Epoll.Tests;

/// <summary>What a loop pass leaves on a Postgres connection: which reader takes its reads, the guard behind every
/// batch, when a connection that stays silent is nudged with a Sync, how the batch size follows the reads' waits, and
/// how long the loop may wait for the next event.</summary>
public sealed class PassTests
{
    const long T = 1_000_000_000; // a Stopwatch timestamp well past 0
    /// <summary>50 ms in Stopwatch ticks (the waits of <see cref="Loop.WaitNanos"/> are checked against it).</summary>
    static readonly long N = Stopwatch.Frequency / 20;
    /// <summary>The silence before a reader's and a writer's nudge, and before a reader counts as stalled.</summary>
    static readonly long R = Ms(Pg.ReadNudgeAfterMs), W = Ms(Pg.WriteNudgeAfterMs), Stall = Ms(Pg.StallAfterMs);

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

    // ---- the guard behind a batch

    [Fact]
    public void ABatchIsFollowedByOneSyncMoreWhichLeavesInASendOfItsOwn()
    {
        Pg pg = Reader();
        Reads(pg, 2);
        pg.Seal(T);
        Assert.Equal("BEBESS", Types(pg.Unsent));
        Assert.Equal("BEBES", Types(pg.FirstPacket));
        Assert.Equal(pg.Unsent.Length - 5, pg.FirstPacket.Length);
        Assert.True(pg.WantsWrite);
        // the guard asks nothing: the two reads are all that waits
        Assert.Equal(2, pg.Pending);
    }

    [Fact]
    public void AGroupOfWritesIsGuardedTheSameWay()
    {
        Pg pg = Writer();
        pg.Group.Add(new WriteJob { Kind = WriteJob.Like, UserId = 7, PostId = 9 });
        pg.SendGroup(T);
        Assert.Equal("BESS", Types(pg.Unsent));
        Assert.Equal("BES", Types(pg.FirstPacket));
    }

    [Fact]
    public void WhenTwoBatchesWaitForOneFlushTheLastGuardIsTheSendOfItsOwn()
    {
        Pg pg = Reader();
        Reads(pg, 1);
        pg.Seal(T);
        Reads(pg, 2);
        pg.Seal(T + 1);
        Assert.Equal("BESSBEBESS", Types(pg.Unsent));
        Assert.Equal("BESSBEBES", Types(pg.FirstPacket));
    }

    [Fact]
    public void ANudgeBehindAnUnsentGuardGoesWithIt()
    {
        Pg pg = Reader();
        Reads(pg, 1);
        pg.Seal(T);
        pg.Nudge(T + R);
        Assert.Equal("BESSS", Types(pg.Unsent));
        Assert.Equal("BES", Types(pg.FirstPacket));
    }

    [Fact]
    public void SealingStampsEachReadOfTheBatchAndTheReaderWithTheTime()
    {
        Pg pg = Reader();
        Reads(pg, 2);
        pg.Seal(T + 3);
        Reads(pg, 1);
        pg.Seal(T + 9);
        Assert.Equal(T + 9, pg.LastUsed);
        Assert.Equal([T + 3, T + 3, T + 9], new[] { pg.TakeWaiter(), pg.TakeWaiter(), pg.TakeWaiter() }.Select(w => w.Sent));
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

    /// <summary>A writer answers its writes at the ReadyForQuery of their group's Sync and no other: the guard's and
    /// a nudge's ReadyForQuery commit nothing, so they must not be taken for the next group's.</summary>
    [Fact]
    public void AGuardsOrANudgesAnswerIsNeverTakenForAGroupsCommit()
    {
        Pg pg = Writer();
        pg.Group.Add(new WriteJob { Kind = WriteJob.Like, UserId = 7, PostId = 9 });
        pg.SendGroup(T);
        pg.Nudge(T + W); // the group's Sync, its guard, a nudge: three ReadyForQuerys are owed

        Arrives(pg, Bound, Inserted, Ready);
        Assert.True(pg.NextReply(out ReadOnlySpan<byte> reply, out bool group));
        Assert.True(group);
        Assert.Equal(Bound.Length + Inserted.Length + Ready.Length, reply.Length);
        Assert.False(pg.NextReply(out _, out _)); // nothing more has come
        pg.Group.Clear(); // the loop answered the write

        // the next group leaves before the guard's and the nudge's answers are in
        pg.Group.Add(new WriteJob { Kind = WriteJob.Like, UserId = 8, PostId = 9 });
        pg.SendGroup(T + 2 * W);
        Arrives(pg, Ready);
        Assert.True(pg.NextReply(out _, out group));
        Assert.False(group); // the first group's guard
        Arrives(pg, Ready);
        Assert.True(pg.NextReply(out _, out group));
        Assert.False(group); // the nudge
        Arrives(pg, Bound, Inserted, Ready, Ready);
        Assert.True(pg.NextReply(out _, out group));
        Assert.True(group); // the second group's commit
        Assert.True(pg.NextReply(out _, out group));
        Assert.False(group); // and its guard
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
    public void ASilentReaderIsNudgedAfter5MsWithOneSync()
    {
        Pg pg = Reader();
        Reads(pg, 2);
        pg.Seal(T);
        Assert.True(pg.Owes);

        Assert.Equal(T + R, pg.Nudge(T));
        Assert.Equal(T + R, pg.Nudge(T + R - 1));
        Assert.Equal("BEBESS", Types(pg.Unsent)); // not yet

        long next = pg.Nudge(T + R);
        Assert.Equal("BEBESSS", Types(pg.Unsent));
        Assert.Equal(T + 2 * R, next);
        // the reads are still waiting, in order
        Assert.Equal(2, pg.Pending);
    }

    [Fact]
    public void FourNudgesComeAtTheSameIntervalThenItDoublesUpTo80Ms()
    {
        Pg pg = Reader();
        Reads(pg, 1);
        pg.Seal(T);
        // the gaps after the 1st, 2nd, ... nudge, in milliseconds
        long[] gaps = [5, 5, 5, 10, 20, 40, 80, 80, 80];
        long now = T + R;
        foreach (long gap in gaps)
        {
            Assert.Equal(now, pg.Nudge(now - 1)); // not before it is due
            Assert.Equal(now + Ms(gap), pg.Nudge(now));
            now += Ms(gap);
        }
        Assert.Equal("BES" + "S" + new string('S', gaps.Length), Types(pg.Unsent));
    }

    [Fact]
    public void AWriterIsGiven20MsAndItsNudgesBackOffTo80MsToo()
    {
        Pg pg = Writer();
        Assert.Equal(long.MaxValue, pg.Nudge(T + 10 * N));
        pg.Group.Add(new WriteJob { Kind = WriteJob.Like, UserId = 7, PostId = 9 });
        pg.Group.Add(new WriteJob { Kind = WriteJob.Create, UserId = 7, BodyLength = 0 });
        pg.SendGroup(T);
        Assert.True(pg.Owes);
        Assert.Equal(T + W, pg.Nudge(T + R)); // a reader's time is too soon for a commit
        Assert.Equal("BEBESS", Types(pg.Unsent));

        long[] gaps = [20, 20, 20, 40, 80, 80];
        long now = T + W;
        foreach (long gap in gaps)
        {
            Assert.Equal(now + Ms(gap), pg.Nudge(now));
            now += Ms(gap);
        }
        Assert.Equal("BEBESS" + new string('S', gaps.Length), Types(pg.Unsent));
    }

    [Fact]
    public void ALateNudgeCountsItsSilenceFromWhenItWasSent()
    {
        Pg pg = Reader();
        Reads(pg, 1);
        pg.Seal(T);
        // the loop looked late: the next nudge is due an interval after this one, not after its deadline
        Assert.Equal(T + 9 * R + R, pg.Nudge(T + 9 * R));
    }

    [Fact]
    public void ABatchSentBehindAnUnansweredOneDoesNotRestartTheClock()
    {
        Pg pg = Reader();
        Reads(pg, 1);
        pg.Seal(T);
        Reads(pg, 1);
        pg.Seal(T + R / 2);
        Assert.Equal(T + R, pg.Nudge(T + R / 2));
        pg.Nudge(T + R);
        Assert.Equal("BESSBESSS", Types(pg.Unsent));
        // and the reader has been silent since the first batch, not the second
        Assert.Equal(R, pg.SilentFor(T + R));
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
        Assert.Equal(T + 200 * N + R, pg.Nudge(T + 200 * N));
    }

    [Fact]
    public void ADownConnectionIsNotNudged()
    {
        Pg pg = Reader();
        Reads(pg, 1);
        pg.Seal(T);
        pg.Broken = true;
        Assert.Equal(long.MaxValue, pg.Nudge(T + 10 * N));
        Assert.Equal("BESS", Types(pg.Unsent));
    }

    [Fact]
    public void ClosingForgetsTheNudgesTheGuardAndWhatWasOwed()
    {
        Pg pg = Reader();
        Reads(pg, 1);
        pg.Seal(T);
        for (int k = 1; k <= 6; k++) pg.Nudge(T + k * N); // well into the doubled intervals
        pg.Close();
        Assert.True(pg.Unsent.IsEmpty);
        Assert.True(pg.FirstPacket.IsEmpty);
        Take(pg, 1); // the loop fails what it owed
        pg.Broken = false; // reconnected
        Reads(pg, 1);
        pg.Seal(T + 100 * N);
        Assert.Equal("BES", Types(pg.FirstPacket));
        Assert.Equal(T + 100 * N + R, pg.Nudge(T + 100 * N));
        // the first nudges are at the short interval again
        Assert.Equal(T + 100 * N + 2 * R, pg.Nudge(T + 100 * N + R));
    }

    /// <summary>Whatever a connection is asked to do, what it has queued is whole messages, with two Syncs per sealed
    /// batch (its own and its guard) and one per nudge; it is nudged only while it owes something and has been
    /// silent long enough; and its first send never ends in the middle of a batch.</summary>
    [Fact]
    public void WhateverHappensTheWireHoldsWholeMessagesTwoSyncsPerBatchAndOnePerNudge()
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
                        if (pg.Open > 0) syncs += 2;
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
                        if (pg.Open > 0) syncs += 2;
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
                        if (owed) Assert.True(due - now <= Ms(Pg.NudgeCapMs), "nudges are never further apart than the cap");
                        break;
                }
                Assert.Equal(sent - answered > 0, pg.Owes);
                Assert.Equal(sent - answered > 0, pg.SilentFor(now + 1) > 0);
            }
            string all = Types(pg.Unsent), first = Types(pg.FirstPacket);
            Assert.Equal(syncs, all.Count(t => t == 'S'));
            Assert.StartsWith(first, all, StringComparison.Ordinal);
            // what stays for the second send is Syncs only: the last guard and the nudges behind it
            Assert.DoesNotContain(all[first.Length..], c => c != 'S');
            if (all.Length > 0) Assert.EndsWith("S", first, StringComparison.Ordinal);
        }).QuickCheckThrowOnFailure();
    }

    // ---- how many reads a batch holds

    [Theory]
    [InlineData(64, 100, 0, 64)]   // nothing waited long: already the largest
    [InlineData(32, 100, 0, 64)]   // grows back by doubling
    [InlineData(4, 100, 0, 8)]
    [InlineData(64, 100, 1, 64)]   // exactly 1 %: not more than 1 %, and not fewer than 0.25 %
    [InlineData(64, 100, 2, 32)]   // more than 1 %: halved
    [InlineData(64, 199, 2, 32)]
    [InlineData(64, 200, 2, 64)]   // 1 % again
    [InlineData(8, 100, 100, 4)]
    [InlineData(4, 100, 100, 4)]   // never below the smallest
    [InlineData(6, 100, 100, 4)]
    [InlineData(32, 400, 1, 32)]   // exactly 0.25 %: stays
    [InlineData(32, 401, 1, 64)]   // fewer than 0.25 %: doubles
    [InlineData(48, 1000, 0, 64)]  // never above the largest
    [InlineData(16, int.MaxValue, int.MaxValue / 50, 8)] // no overflow in the shares
    [InlineData(16, int.MaxValue, 0, 32)]
    public void ABatchHalvesWhenMoreThanOneInAHundredReadsWaitedLongAndDoublesWhenFewerThanOneInFourHundredDid(int batch, int reads, int late, int next)
    {
        Assert.Equal(next, Loop.NextBatch(batch, reads, late));
    }

    [Fact]
    public void TheBatchSizeStaysBetweenTheSmallestAndTheLargestAndMovesOneStepAtATime()
    {
        var judgments = from batch in Gen.Elements(4, 8, 16, 32, 64)
                        from reads in Gen.Choose(100, 100_000)
                        from late in Gen.Choose(0, 100_000)
                        select (batch, reads, late: Math.Min(late, reads));
        Prop.ForAll(judgments.ToArbitrary(), j =>
        {
            int next = Loop.NextBatch(j.batch, j.reads, j.late);
            Assert.InRange(next, Loop.SmallestBatch, Pg.MaxBatch);
            Assert.Contains(next, new[] { j.batch / 2, j.batch, j.batch * 2 });
            // more late reads never give a larger batch
            Assert.True(Loop.NextBatch(j.batch, j.reads, Math.Min(j.reads, j.late + j.reads / 50 + 1)) <= next);
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
