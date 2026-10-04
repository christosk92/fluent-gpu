using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Forms;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Input;
using FluentGpu.Layout;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Controls;
using FluentGpu.Render;
using FluentGpu.Rhi;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;




// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────
// video-smooth-switching-implementation.md §6 — headless gates for the video-engine seam (VideoEngineSeam.cs):
// the seqlock snapshot buffer (alloc-free reads, no torn reads under a concurrent publisher) and the coalesced
// command queue (last-wins per kind, alloc-free Post, single-wake-per-drain-cycle). No GPU/window — pure BCL types.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────

static class MediaSeamSuite
{
    public static void Run(StringTable strings)
    {
        SnapshotAllocFreeCheck();
        SnapshotNoTearCheck();
        CommandCoalesceCheck();
        CommandLastWinsTransportCheck();
        WakeCoalesceCheck();
        RequestWakeCoalesceCheck();
        MultiProducerNoTearCheck();
    }

    // ── gate.media.seam.snapshot-alloc-free ───────────────────────────────────────────────────────────────────────

    static void SnapshotAllocFreeCheck()
    {
        var buf = new VideoSnapshotBuffer();
        buf.Publish(new VideoEngineSnapshot
        {
            SourceEpoch = 1,
            Flags = VideoEngineFlags.MetadataLoaded | VideoEngineFlags.CanPlay,
            ReadyState = 4,
            NaturalW = 1920,
            NaturalH = 1080,
            DurationSeconds = 123.4,
        });

        // Warm up first: JIT the Read() path (and any first-touch struct-copy paths) before measuring.
        long sink = 0;
        for (int i = 0; i < 64; i++) sink += buf.Read().SourceEpoch;

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++) sink += buf.Read().SourceEpoch;
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Check("gate.media.seam.snapshot-alloc-free — 10,000 VideoSnapshotBuffer.Read() calls allocate 0 managed bytes",
            delta == 0, $"delta={delta} bytes over 10000 reads (sink={sink})");
    }

    // ── gate.media.seam.snapshot-no-tear ──────────────────────────────────────────────────────────────────────────

    static void SnapshotNoTearCheck()
    {
        var buf = new VideoSnapshotBuffer();
        buf.Publish(new VideoEngineSnapshot { SourceEpoch = 0, ReadyState = 0, DurationSeconds = 0 });

        var sw = Stopwatch.StartNew();
        const long durationMs = 50;
        bool tornDetected = false;
        long reads = 0, writes = 0;

        var writer = new Thread(() =>
        {
            int i = 0;
            while (sw.ElapsedMilliseconds < durationMs)
            {
                i++;
                // Every field encodes the same counter value — any Read() that observes a mix of old/new values
                // across fields is a torn read.
                buf.Publish(new VideoEngineSnapshot
                {
                    SourceEpoch = i,
                    ReadyState = (uint)i,
                    DurationSeconds = i,
                });
                writes++;
            }
        });
        var reader = new Thread(() =>
        {
            while (sw.ElapsedMilliseconds < durationMs)
            {
                var s = buf.Read();
                reads++;
                if (s.SourceEpoch != (int)s.ReadyState || s.SourceEpoch != (int)s.DurationSeconds)
                {
                    tornDetected = true;
                    return;
                }
            }
        });

        writer.Start();
        reader.Start();
        writer.Join();
        reader.Join();

        Check("gate.media.seam.snapshot-no-tear — a reader thread never observes a torn VideoEngineSnapshot under a concurrent publisher",
            !tornDetected && reads > 0 && writes > 0,
            $"reads={reads} writes={writes} torn={tornDetected}");
    }

    // ── gate.media.seam.command-coalesce ──────────────────────────────────────────────────────────────────────────

    static void CommandCoalesceCheck()
    {
        var q = new VideoEngineCommandQueue();
        for (int i = 0; i < 1000; i++) q.Post(VideoCommandKind.Seek, a: i);

        bool took = q.TryTake(VideoCommandKind.Seek, out double a, out _, out _, out _);
        bool drainedAgain = q.TryTake(VideoCommandKind.Seek, out _, out _, out _, out _);
        Check("gate.media.seam.command-coalesce — 1000 same-kind posts coalesce to exactly one TryTake carrying the last-posted payload",
            took && a == 999 && !drainedAgain,
            $"took={took} a={a} drainedAgain={drainedAgain}");

        // Distinct kinds posted interleaved must survive independently — coalescing is per-kind, not global.
        var q2 = new VideoEngineCommandQueue();
        q2.Post(VideoCommandKind.Seek, a: 5);
        q2.Post(VideoCommandKind.Volume, a: 0.5);
        q2.Post(VideoCommandKind.Seek, a: 7);
        q2.Post(VideoCommandKind.Muted, i: 1);
        q2.Post(VideoCommandKind.Volume, a: 0.75);
        q2.Post(VideoCommandKind.Rate, a: 2.0);

        bool seekOk = q2.TryTake(VideoCommandKind.Seek, out double seekA, out _, out _, out _) && seekA == 7;
        bool volOk = q2.TryTake(VideoCommandKind.Volume, out double volA, out _, out _, out _) && volA == 0.75;
        bool mutedOk = q2.TryTake(VideoCommandKind.Muted, out _, out int mutedI, out _, out _) && mutedI == 1;
        bool rateOk = q2.TryTake(VideoCommandKind.Rate, out double rateA, out _, out _, out _) && rateA == 2.0;
        bool transportEmpty = !q2.TryTake(VideoCommandKind.Transport, out _, out _, out _, out _);
        Check("gate.media.seam.command-coalesce — interleaved distinct kinds each keep their own last-wins payload",
            seekOk && volOk && mutedOk && rateOk && transportEmpty,
            $"seek={seekOk} vol={volOk} muted={mutedOk} rate={rateOk} transportEmpty={transportEmpty}");
    }

    // ── gate.media.seam.command-last-wins-transport ───────────────────────────────────────────────────────────────

    static void CommandLastWinsTransportCheck()
    {
        var q = new VideoEngineCommandQueue();
        q.Post(VideoCommandKind.Transport, i: 1);   // play
        q.Post(VideoCommandKind.Transport, i: 0);   // pause
        q.Post(VideoCommandKind.Transport, i: 1);   // play

        bool took = q.TryTake(VideoCommandKind.Transport, out _, out int i, out _, out _);
        bool drainedAgain = q.TryTake(VideoCommandKind.Transport, out _, out _, out _, out _);
        Check("gate.media.seam.command-last-wins-transport — play/pause/play drains to exactly one Transport command with i==1 (play)",
            took && i == 1 && !drainedAgain,
            $"took={took} i={i} drainedAgain={drainedAgain}");
    }

    // ── gate.media.seam.wake-coalesce ─────────────────────────────────────────────────────────────────────────────

    static void WakeCoalesceCheck()
    {
        var q = new VideoEngineCommandQueue();
        int wakeCount = 0;
        q.Wake = () => Interlocked.Increment(ref wakeCount);

        // N posts (mixed kinds) before BeginDrain must coalesce to exactly one Wake invocation.
        q.Post(VideoCommandKind.Seek, a: 1);
        q.Post(VideoCommandKind.Volume, a: 0.25);
        q.Post(VideoCommandKind.Rate, a: 1.5);
        q.Post(VideoCommandKind.Seek, a: 2);
        bool wokeOnceBeforeDrain = wakeCount == 1;

        // Engine thread starts its drain: BeginDrain re-opens the wake gate.
        q.BeginDrain();
        q.TryTake(VideoCommandKind.Seek, out _, out _, out _, out _);
        q.TryTake(VideoCommandKind.Volume, out _, out _, out _, out _);
        q.TryTake(VideoCommandKind.Rate, out _, out _, out _, out _);
        bool noExtraWakeFromDrainItself = wakeCount == 1;

        // A post during/after the drain must produce exactly one more Wake.
        q.Post(VideoCommandKind.Muted, i: 1);
        q.Post(VideoCommandKind.Loop, i: 1);
        bool wokeOnceMoreAfterDrainStart = wakeCount == 2;

        Check("gate.media.seam.wake-coalesce — N posts before BeginDrain wake exactly once, and a post during/after drain wakes exactly once more",
            wokeOnceBeforeDrain && noExtraWakeFromDrainItself && wokeOnceMoreAfterDrainStart,
            $"wakeCount={wakeCount} beforeDrain={wokeOnceBeforeDrain} drainItself={noExtraWakeFromDrainItself} afterDrain={wokeOnceMoreAfterDrainStart}");
    }

    // ── gate.media.seam.request-wake-coalesce ─────────────────────────────────────────────────────────────────────

    static void RequestWakeCoalesceCheck()
    {
        var q = new VideoEngineCommandQueue();
        int wakeCount = 0;
        q.Wake = () => Interlocked.Increment(ref wakeCount);

        // A burst of native-event wakes wakes exactly once, and a command post inside the same drain cycle rides the same gate.
        for (int i = 0; i < 50; i++) q.RequestWake();
        q.Post(VideoCommandKind.Seek, a: 1);
        bool onceForBurst = wakeCount == 1;

        q.BeginDrain();
        q.RequestWake();
        q.RequestWake();
        q.Post(VideoCommandKind.Volume, a: 0.5);
        bool onceMoreAfterDrain = wakeCount == 2;

        // A bare RequestWake carries no payload: nothing becomes pending.
        var q2 = new VideoEngineCommandQueue();
        q2.RequestWake();
        bool noPayload = !q2.TryTake(VideoCommandKind.Seek, out _, out _, out _, out _)
                         && !q2.TryTake(VideoCommandKind.Transport, out _, out _, out _, out _);

        Check("gate.media.seam.request-wake-coalesce — event wakes and command posts share one gate: a burst wakes once per drain cycle, with no payload",
            onceForBurst && onceMoreAfterDrain && noPayload,
            $"wakeCount={wakeCount} burst={onceForBurst} afterDrain={onceMoreAfterDrain} noPayload={noPayload}");
    }

    // ── gate.media.seam.multi-producer ────────────────────────────────────────────────────────────────────────────

    static void MultiProducerNoTearCheck()
    {
        const int Producers = 4, PostsEach = 200_000;
        var q = new VideoEngineCommandQueue();
        var tags = new object[Producers];
        for (int p = 0; p < Producers; p++) tags[p] = new object();

        long tears = 0, duplicates = 0, takes = 0;
        int running = Producers;
        var consumer = new Thread(() =>
        {
            var last = new int[Producers];   // last sequence taken per producer (strictly increasing, or it was applied twice)
            Array.Fill(last, -1);
            while (true)
            {
                if (!q.TryTake(VideoCommandKind.Seek, out double a, out int i, out int j, out object? obj))
                {
                    if (Volatile.Read(ref running) == 0) break;
                    Thread.Yield();
                    continue;
                }
                takes++;
                // Payload from producer p, post n: a == n, i == n, j == p, obj == tags[p]. Anything else is a mix of two posts.
                int p = j;
                if ((uint)p >= Producers || !ReferenceEquals(obj, tags[p]) || a != i) { tears++; continue; }
                if (i <= last[p]) duplicates++;
                last[p] = i;
            }
        }) { IsBackground = true };

        var producers = new Thread[Producers];
        for (int p = 0; p < Producers; p++)
        {
            int id = p;
            producers[p] = new Thread(() =>
            {
                for (int n = 0; n < PostsEach; n++) q.Post(VideoCommandKind.Seek, a: n, i: n, j: id, obj: tags[id]);
                Interlocked.Decrement(ref running);
            }) { IsBackground = true };
        }

        consumer.Start();
        foreach (Thread t in producers) t.Start();
        foreach (Thread t in producers) t.Join();
        consumer.Join();

        Check("gate.media.seam.multi-producer — concurrent Post from several threads never tears a payload and never applies one twice",
            tears == 0 && duplicates == 0 && takes > 0,
            $"takes={takes} tears={tears} duplicates={duplicates}");
    }
}
