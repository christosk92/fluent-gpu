using System.Collections.Generic;
using System.Linq;
using Wavee.Backend;
using Wavee.Core;
using Xunit;

namespace Wavee.Tests;

// Foundation — the pure PlaybackSession semantics (queue-rework-proposal §4 + §12 ¶1). No I/O; every §4 numbered
// semantic is pinned here: id stability, q-uid minting, the three skip-to targets, next-order + delimiter-stop,
// keepUserQueue matrix, shuffle anchor, revision monotonicity.
public class QueueSessionTests
{
    static Track T(string id) => new(id, "spotify:track:" + id, "T-" + id,
        System.Array.Empty<ArtistRef>(), new AlbumRef("", "", ""), 1000, false, null);

    static QueuedTrack Q(string id, string uid = "", string provider = "context",
        QueueRowKind kind = QueueRowKind.Playable) => new(T(id), uid, provider, null, kind);

    static IReadOnlyList<QueuedTrack> Ctx(params string[] ids) => ids.Select(x => Q(x, "u-" + x)).ToList();

    static bool IsTrack(QueueEntry e, string id) => e.Track.Uri == "spotify:track:" + id;

    // ── §4.3 — skip-to-upcoming keeps the user queue; previous current → history; skipped rows do NOT enter history ──
    [Fact]
    public void SkipToUpcoming_KeepsUserQueue_AndDoesNotHistorizeSkipped()
    {
        var s = new PlaybackSession();
        s.SetContext("spotify:playlist:p", Ctx("a", "b", "c", "d"), 0);
        s.EnqueueUser(new[] { Q("q1") });

        var target = s.Snapshot().Upcoming.Single(e => IsTrack(e, "c"));
        var snap = s.SkipToItem(target.ItemId);

        Assert.NotNull(snap);
        Assert.True(IsTrack(snap!.Current!, "c"));
        Assert.Single(snap.UserQueue);                                  // user queue untouched
        Assert.Contains(snap.History, e => IsTrack(e, "a"));            // previous current historized
        Assert.DoesNotContain(snap.History, e => IsTrack(e, "b"));      // skipped row NOT historized
        Assert.DoesNotContain(snap.Upcoming, e => IsTrack(e, "b"));     // and it left Upcoming
    }

    // ── §4.4 — skip-to-queue-row drains predecessors (drop); later rows remain; context cursor unmoved ──
    [Fact]
    public void SkipToUserQueueRow_DrainsPredecessors_ContextCursorUnmoved()
    {
        var s = new PlaybackSession();
        s.SetContext("spotify:playlist:p", Ctx("a", "b"), 0);
        s.EnqueueUser(new[] { Q("q1"), Q("q2"), Q("q3") });

        var target = s.Snapshot().UserQueue[1];                         // second queued row (q2)
        var snap = s.SkipToItem(target.ItemId);

        Assert.NotNull(snap);
        Assert.True(IsTrack(snap!.Current!, "q2"));
        Assert.Equal(QueueProvider.Queue, snap.Current!.Provider);
        Assert.Single(snap.UserQueue);                                  // predecessor dropped; only q3 remains
        Assert.True(IsTrack(snap.UserQueue[0], "q3"));
        Assert.Contains(snap.Upcoming, e => IsTrack(e, "b"));           // context cursor did not move
    }

    // ── §4.5 — history-row click is a cursor-back: entry becomes current, history truncates above it, Upcoming re-derives
    [Fact]
    public void SkipToHistoryRow_CursorBack_TruncatesHistory_RederivesUpcoming()
    {
        var s = new PlaybackSession();
        s.SetContext("spotify:playlist:p", Ctx("a", "b", "c"), 0);
        s.Next();   // a → b, history [a]
        s.Next();   // b → c, history [a,b]

        var hEntry = s.Snapshot().History.Single(e => IsTrack(e, "a"));
        var snap = s.SkipToItem(hEntry.ItemId);

        Assert.NotNull(snap);
        Assert.True(IsTrack(snap!.Current!, "a"));
        Assert.Empty(snap.History);                                     // truncated at/above the target
        Assert.Equal(2, snap.Upcoming.Length);                         // b, c re-derived from a's slot
        Assert.True(IsTrack(snap.Upcoming[0], "b"));
    }

    // ── §4.6 — next-order: user queue → context → autoplay tail → delimiter-stop; markers/delimiters never surfaced ──
    [Fact]
    public void Next_Order_QueueThenContextThenAutoplayThenDelimiterStop()
    {
        var s = new PlaybackSession();
        s.SetContext("spotify:playlist:p", Ctx("a", "b"), 0);
        s.AppendContextPage(new[] { Q("s1", "us1", "autoplay"), Q("s2", "us2", "autoplay") },
            QueueProvider.Autoplay, "spotify:station:x");
        s.AppendContextPage(new[] { Q("delim", "", "context", QueueRowKind.Delimiter) }, QueueProvider.Context, null);
        s.EnqueueUser(new[] { Q("q1") });

        var up = s.Snapshot().Upcoming;
        Assert.Contains(up, e => IsTrack(e, "s1") && e.Provider == QueueProvider.Autoplay);
        Assert.DoesNotContain(up, e => IsTrack(e, "delim"));            // delimiter never surfaced
        Assert.Equal("spotify:station:x", s.Snapshot().AutoplayContextUri);

        Assert.True(IsTrack(s.Snapshot().Current!, "a"));
        Assert.True(IsTrack(s.Next()!.Current!, "q1"));                 // user queue drains first
        Assert.True(IsTrack(s.Next()!.Current!, "b"));                  // then context after cursor
        Assert.True(IsTrack(s.Next()!.Current!, "s1"));                 // then autoplay tail
        Assert.True(IsTrack(s.Next()!.Current!, "s2"));
        Assert.Null(s.Next()!.Current);                                 // delimiter stops advance
    }

    // ── §4.1 — ids minted once; survive reorder / remove / continuation-append; EntryId is derived "i{id}" ──
    [Fact]
    public void ItemIds_AreStable_AcrossReorderRemoveAppend()
    {
        var s = new PlaybackSession();
        s.SetContext("spotify:playlist:p", Ctx("a", "b", "c"), 0);
        var idB = s.Snapshot().Upcoming.Single(e => IsTrack(e, "b")).ItemId;
        Assert.False(idB.IsNone);

        s.EnqueueUser(new[] { Q("q1"), Q("q2") });
        s.AppendContextPage(Ctx("d"), QueueProvider.Context, "spotify:playlist:p");
        var idQ2 = s.Snapshot().UserQueue[1].ItemId;
        s.MoveUserItem(idQ2, 0);                                        // reorder the user queue
        s.RemoveItem(s.Snapshot().UserQueue[1].ItemId);                 // remove the other queued row

        var after = s.Snapshot();
        Assert.Equal(idB, after.Upcoming.Single(e => IsTrack(e, "b")).ItemId);   // context id unchanged
        Assert.Equal(idQ2, after.UserQueue.Single().ItemId);                     // queue id survived reorder+remove
        Assert.Equal("i" + idB.Value, after.Upcoming.Single(e => IsTrack(e, "b")).EntryId);

        // all live ids are unique
        var allIds = after.Upcoming.Concat(after.UserQueue).Concat(after.History).Select(e => e.ItemId.Value).ToList();
        if (after.Current is { } cur) allIds.Add(cur.ItemId.Value);
        Assert.Equal(allIds.Count, allIds.Distinct().Count());
    }

    // ── §4.2 / §7.4 — q-uids minted "q{n}" at add; existing uids preserved; mint cursor stays ahead ──
    [Fact]
    public void QueueUids_MintedSequentially_ExistingPreserved()
    {
        var s = new PlaybackSession();
        s.SetContext("spotify:playlist:p", Ctx("a"), 0);
        s.EnqueueUser(new[] { Q("q1"), Q("q2") });

        var uq = s.Snapshot().UserQueue;
        Assert.Equal("q0", uq[0].Uid);
        Assert.Equal("q1", uq[1].Uid);

        s.EnqueueUser(new[] { new QueuedTrack(T("q3"), "q7", "queue") });   // pre-minted uid preserved
        Assert.Equal("q7", s.Snapshot().UserQueue[2].Uid);

        s.EnqueueUser(new[] { Q("q4") });                                   // next mint continues past 7
        Assert.Equal("q8", s.Snapshot().UserQueue[3].Uid);
    }

    // ── §4 shuffle — anchored: current stays put; OFF restores natural order ──
    [Fact]
    public void Shuffle_AnchorsCurrent_RestoreReturnsNaturalOrder()
    {
        var s = new PlaybackSession();
        s.SetContext("spotify:playlist:p", Ctx("a", "b", "c", "d", "e"), 2);   // current = c
        var current = s.Snapshot().Current!.Track.Uri;

        var on = s.SetShuffle(true);
        Assert.True(on.Shuffle);
        Assert.Equal(current, on.Current!.Track.Uri);                          // anchor unchanged
        Assert.Equal(4, on.Upcoming.Length);                                   // the other four trail the anchor

        var off = s.SetShuffle(false);
        Assert.False(off.Shuffle);
        Assert.Equal(current, off.Current!.Track.Uri);
        Assert.True(IsTrack(off.Upcoming[0], "d"));                            // natural order resumes from c
    }

    // ── §4.7 — keepUserQueue matrix: default true keeps the queue across contexts; transfer-in (false) clears it ──
    [Fact]
    public void KeepUserQueue_DefaultTrue_FalseOnlyClears()
    {
        var s = new PlaybackSession();
        s.SetContext("spotify:playlist:p", Ctx("a"), 0);
        s.EnqueueUser(new[] { Q("q1") });

        s.SetContext("spotify:playlist:p2", Ctx("x", "y"), 0);                 // default keepUserQueue: true
        Assert.Single(s.Snapshot().UserQueue);

        s.SetContext("spotify:playlist:p3", Ctx("m"), 0, keepUserQueue: false);
        Assert.Empty(s.Snapshot().UserQueue);
    }

    // ── §4 revision — bumped on every mutation, strictly monotonic ──
    [Fact]
    public void Revision_IsStrictlyMonotonic()
    {
        var s = new PlaybackSession();
        var revs = new List<long>();
        revs.Add(s.SetContext("spotify:playlist:p", Ctx("a", "b", "c"), 0).Revision);
        revs.Add(s.EnqueueUser(new[] { Q("q1") }).Revision);
        revs.Add(s.Next()!.Revision);
        revs.Add(s.SetShuffle(true).Revision);
        revs.Add(s.SetRepeat(RepeatMode.Context).Revision);
        for (int i = 1; i < revs.Count; i++) Assert.True(revs[i] > revs[i - 1], $"rev[{i}]={revs[i]} !> rev[{i - 1}]={revs[i - 1]}");
    }

    // ── §4 SkipToUid — inbound next_track / remote clicks resolve by uid then uri ──
    [Fact]
    public void SkipToUid_ResolvesByUid_ThenUriFallback()
    {
        var s = new PlaybackSession();
        s.SetContext("spotify:playlist:p", Ctx("a", "b", "c"), 0);          // uids u-a, u-b, u-c

        var byUid = s.SkipToUid("u-c", null);
        Assert.NotNull(byUid);
        Assert.True(IsTrack(byUid!.Current!, "c"));

        var s2 = new PlaybackSession();
        s2.SetContext("spotify:playlist:p", new[] { Q("a", ""), Q("b", ""), Q("c", "") }, 0);   // no uids
        var byUri = s2.SkipToUid("", "spotify:track:b");
        Assert.NotNull(byUri);
        Assert.True(IsTrack(byUri!.Current!, "b"));

        Assert.Null(s2.SkipToUid("missing-uid", "spotify:track:zzz"));      // identity miss → null (caller patches)
    }
}
