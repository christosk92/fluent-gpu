using System;
using System.Buffers;
using System.Collections.Generic;
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




static class DiagnosticsSuite
{
    public static void Run(StringTable strings)
    {
        DiagnosticsLeakGateChecks(strings);
        PaletteContrastChecks();
        LocalizationKitChecks(strings);
    }

    static void LocalizationKitChecks(StringTable strings)
    {
        // Clean, deterministic starting point: no culture tables loaded → only the kit's baked-in neutral floor exists.
        FluentGpu.Localization.Localization.Clear();
        FluentGpu.Localization.Localization.SetCulture("en-US");

        // ── gate.loc.kit-fallback — the zero-config contract (neutral floor resolves via the generated keys). ──
        bool fbOk    = FluentGpu.Localization.Loc.Get(FluentGpu.Controls.Strings.Dialog.Ok) == "OK";
        bool fbOff   = FluentGpu.Localization.Loc.Get(FluentGpu.Controls.Strings.Media.Off) == "Off";
        bool fbClose = FluentGpu.Localization.Loc.Get(FluentGpu.Controls.Strings.InfoBar.Close) == "Close";
        bool fbFmt   = FluentGpu.Localization.Loc.Format(FluentGpu.Controls.Strings.Media.CaptionsIndexedKey, ("n", 2)) == "Captions 2";
        Check("gate.loc.kit-fallback: kit keys resolve to neutral strings with NO tables loaded",
            fbOk && fbOff && fbClose && fbFmt,
            $"ok={fbOk} off={fbOff} close={fbClose} fmt={fbFmt}");

        // ── gate.loc.validation-neutral-floor (G7 item 6) — the ENGINE's built-in validation rule messages (Rules.*)
        // resolve to their ship-with-the-assembly neutral strings with NO culture table loaded, so a form error never
        // surfaces the visible-missing [validation.*] marker on the hot path (Engine-side RegisterNeutral floor). ──
        bool vReq = FluentGpu.Localization.Loc.Get("validation.required") == "Required.";
        bool vMin = FluentGpu.Localization.Loc.Get("validation.minlen")   == "Too short.";
        bool vMax = FluentGpu.Localization.Loc.Get("validation.maxlen")   == "Too long.";
        bool vRng = FluentGpu.Localization.Loc.Get("validation.range")    == "Out of range.";
        Check("gate.loc.validation-neutral-floor: engine validation.* keys resolve to the neutral floor (no [key] marker)",
            vReq && vMin && vMax && vRng, $"req={vReq} min={vMin} max={vMax} range={vRng}");

        // ── gate.loc.kit-pseudo — pseudo transforms the neutral floor; switching back restores it. ──
        FluentGpu.Localization.Localization.SetCulture(FluentGpu.Localization.PseudoLocalizer.PseudoCulture);
        string psOk    = FluentGpu.Localization.Loc.Get(FluentGpu.Controls.Strings.Dialog.Ok);
        string psOff   = FluentGpu.Localization.Loc.Get(FluentGpu.Controls.Strings.Media.Off);
        string psClose = FluentGpu.Localization.Loc.Get(FluentGpu.Controls.Strings.InfoBar.Close);
        bool psTransformed =
            psOk == FluentGpu.Localization.PseudoLocalizer.Transform("OK") && psOk.StartsWith("⟦") &&
            psOff == FluentGpu.Localization.PseudoLocalizer.Transform("Off") &&
            psClose == FluentGpu.Localization.PseudoLocalizer.Transform("Close");
        FluentGpu.Localization.Localization.SetCulture("en-US");
        bool backToNeutral = FluentGpu.Localization.Loc.Get(FluentGpu.Controls.Strings.Dialog.Ok) == "OK";
        Check("gate.loc.kit-pseudo: pseudo-locale transforms kit keys; switching back re-resolves neutral",
            psTransformed && backToNeutral,
            $"pseudoOk='{psOk}' transformed={psTransformed} back={backToNeutral}");

        // ── gate.loc.no-hardcoded-kit-strings — render a real kit control and prove its text goes through loc. ──
        // A DatePicker with no date draws "day"/"month"/"year" faces. Under pseudo EVERY leaf must be bracketed; an
        // un-keyed hardcoded literal would render as plain English and fail the pseudo assertion (pseudo-loc's purpose).
        var dev = new HeadlessGpuDevice();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("loc-kit", new Size2(420, 320), 1f));
        window.Show();
        using (app)
        using (var host = new AppHost(app, window, dev, new HeadlessFontSystem(strings), strings, new LocDatePickerProbe()))
        {
            host.RunFrame();
            bool neutralFace = HasGlyph(dev, strings, "day") && HasGlyph(dev, strings, "month") && HasGlyph(dev, strings, "year");

            FluentGpu.Localization.Localization.SetCulture(FluentGpu.Localization.PseudoLocalizer.PseudoCulture);
            host.RunFrame();
            bool pseudoFace =
                HasGlyph(dev, strings, FluentGpu.Localization.PseudoLocalizer.Transform("day")) &&
                HasGlyph(dev, strings, FluentGpu.Localization.PseudoLocalizer.Transform("month")) &&
                HasGlyph(dev, strings, FluentGpu.Localization.PseudoLocalizer.Transform("year")) &&
                !HasGlyph(dev, strings, "day");   // the raw English literal must NOT leak through under pseudo

            FluentGpu.Localization.Localization.SetCulture("en-US");
            var quiet = host.RunFrame();                 // re-resolves back to neutral on the culture bump
            bool restored = HasGlyph(dev, strings, "day");
            var quiet2 = host.RunFrame();                // now truly quiet — the loc bind must not allocate in paint
            bool zeroAlloc = quiet2.HotPhaseAllocBytes == 0;

            Check("gate.loc.no-hardcoded-kit-strings: DatePicker faces render loc-resolved text (neutral↔pseudo), quiet frame 0-alloc",
                neutralFace && pseudoFace && restored && zeroAlloc,
                $"neutral={neutralFace} pseudo={pseudoFace} restored={restored} quietAlloc={quiet2.HotPhaseAllocBytes}B");
        }

        // Leave global localization state clean for any later phase.
        FluentGpu.Localization.Localization.SetCulture("en-US");
    }



    static void DiagnosticsLeakGateChecks(StringTable strings)
    {
        // Drive a host to quiescence: HasActiveWork goes false (the loop idles) or a 200-frame cap trips. Returns the
        // frame count consumed (== cap on a host that never settles, which the caller asserts against).
        static int PumpToQuiescent(AppHost host, int cap = 200)
        {
            int n = 0;
            for (; n < cap; n++)
            {
                if (!host.HasActiveWork) break;
                host.RunFrame();
            }
            return n;
        }

        // ── census.settle: a non-trivial app (list + a couple of controls) mounts and the loop reaches idle. ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("gate-settle", new Size2(360, 420), 1f)); window.Show();
            var settleChecked = new Signal<CheckState>(CheckState.Checked);   // stable instance (no per-render churn → the loop settles)
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
                new W0fStaticProbe
                {
                    Build = () => new BoxEl
                    {
                        Direction = 1, Width = 320, Height = 380, Gap = 8, Padding = Edges4.All(12),
                        Children =
                        [
                            new TextEl("settle") { Size = 16f },
                            Button.Accent("ok", () => { }),
                            CheckBox.Create("opt", settleChecked),
                            (Element)Repeater.ItemsRepeater(64, i => new BoxEl { Height = 32f, Fill = ColorF.FromRgba(28, 28, 28) },
                                RepeatLayout.Stack(32f), keyOf: i => "s" + i),
                        ],
                    },
                });
            host.RunFrame();
            int frames = PumpToQuiescent(host);
            bool idled = !host.HasActiveWork && frames < 200;
            Check("gate.census.settle a list+controls host runs to quiescence (HasActiveWork goes false within the 200-frame cap)",
                idled, $"framesToIdle={frames} active={host.HasActiveWork} wake={host.CurrentWakeReasons}");
        }

        // ── census.return-to-baseline: a signal swaps the root between a tiny baseline view and a heavy view; after
        // swapping back and draining, every LIVE census count returns to the baseline snapshot. High-water residuals
        // (SceneCapacity slab, StringIdHighWater — both monotonic by design) are deliberately NOT asserted. ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("gate-baseline", new Size2(360, 480), 1f)); window.Show();
            var heavy = new Signal<bool>(false);
            const int HeavyRows = 200;
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
                new W0fStaticProbe
                {
                    Build = () =>
                    {
                        bool big = heavy.Value;   // subscribe: a flip re-renders the owner and restructures the tree
                        Element view;
                        if (!big)
                            view = new BoxEl
                            {
                                Direction = 1, Gap = 8, Padding = Edges4.All(12),
                                Children = [new TextEl("baseline") { Size = 16f }, Button.Accent("idle", () => { })],
                            };
                        else
                        {
                            // Heavy: a few hundred rows (SceneLive + StringMap), a bound Fill per row (NodeBindings),
                            // and a couple of embedded components (Components) — drives every LIVE census column up.
                            var rows = new Element[HeavyRows];
                            for (int i = 0; i < HeavyRows; i++)
                            {
                                int idx = i;
                                rows[i] = new BoxEl
                                {
                                    Height = 24f, Direction = 0, Gap = 4,
                                    Fill = Prop.Of(() => ColorF.FromRgba(20, 20, (byte)(idx % 2 == 0 ? 28 : 40))),
                                    Children =
                                    [
                                        new TextEl("") { Size = 12f, Text = Prop.Of(() => "heavy row " + idx) },
                                        .. (idx < 3 ? new Element[] { Embed.Comp(() => new NestChild()) } : Array.Empty<Element>()),
                                    ],
                                };
                            }
                            view = new BoxEl { Direction = 1, Children = rows };
                        }
                        // KEY the swapped subtree so a baseline↔heavy swap REMOUNTS it rather than reusing nodes
                        // positionally — otherwise the static-Fill Button and a bound-Fill heavy row collide at the same
                        // slot (a bound↔static channel flip on a reused node, which the BindContract tripwire flags).
                        return new BoxEl
                        {
                            Direction = 1, Width = 320, Height = 480,
                            Children = [view with { Key = big ? "heavy" : "baseline" }],
                        };
                    },
                });
            host.RunFrame();
            PumpToQuiescent(host);
            var a = CensusSnapshot.Capture(host);   // A — quiescent baseline

            heavy.Value = true;                     // → heavy view
            host.RunFrame();
            PumpToQuiescent(host);
            var heavySnap = CensusSnapshot.Capture(host);
            bool grew = heavySnap.SceneLive > a.SceneLive + HeavyRows
                        && heavySnap.Components > a.Components
                        && heavySnap.NodeBindings > a.NodeBindings
                        && heavySnap.StringMap > a.StringMap;

            heavy.Value = false;                    // → back to baseline
            host.RunFrame();
            PumpToQuiescent(host);
            for (int i = 0; i < 8; i++) host.RunFrame();   // orphan reclaim + string Tick quarantine settle margin
            var b = CensusSnapshot.Capture(host);   // B — should equal A on the LIVE counts

            bool scene = b.SceneLive == a.SceneLive;
            bool comps = b.Components == a.Components;
            bool binds = b.NodeBindings == a.NodeBindings;
            bool strs = b.StringMap == a.StringMap;
            bool tracks = b.AnimTracks == a.AnimTracks;
            Check("gate.census.return-to-baseline heavy view → back: scene live / components / node-bindings / strings / anim tracks all return to the baseline snapshot",
                grew && scene && comps && binds && strs && tracks,
                $"grew={grew} | live {a.SceneLive}->{heavySnap.SceneLive}->{b.SceneLive} comps {a.Components}->{heavySnap.Components}->{b.Components} "
                + $"binds {a.NodeBindings}->{heavySnap.NodeBindings}->{b.NodeBindings} strMap {a.StringMap}->{heavySnap.StringMap}->{b.StringMap} "
                + $"tracks {a.AnimTracks}->{b.AnimTracks} (cap {a.SceneCapacity}->{b.SceneCapacity}, idHW {a.StringIdHighWater}->{b.StringIdHighWater} high-water)");

            // mem-02: the SceneCapacity slab ratchets to the heavy high-water and (by design) never shrinks on the
            // idle path on its own — TrimExcessCapacity gives the all-free TAIL back. After the unmount-and-quiesce
            // above, trim and re-snapshot: capacity must shrink TOWARD the baseline (never grow), and every live handle
            // must keep its index (LiveCount unchanged + the host still renders a correct frame afterwards). The exact
            // reclaimed extent depends on where the surviving baseline nodes landed under LIFO slot reuse, so the
            // guarantee asserted is precisely what the tail-trim promises: capacity <= the grown high-water, never below
            // its floor, index-stable. (A deterministic max-trim case is proven in gate.scene.trim-tail below.)
            int reclaimed = host.Scene.TrimExcessCapacity();
            var t = CensusSnapshot.Capture(host);
            host.RunFrame();                              // index stability: a post-trim frame records cleanly off the shrunk slab
            var afterTrim = CensusSnapshot.Capture(host);
            bool capShrankToBaseline = t.SceneCapacity <= heavySnap.SceneCapacity;   // trim never grows the slab
            bool indexStable = t.SceneLive == b.SceneLive && afterTrim.SceneLive == b.SceneLive;   // every live handle survived
            bool floorRespected = t.SceneCapacity >= 256;   // the trim floor (never shrink below a sane working set)
            Check("gate.census.trim-tail TrimExcessCapacity gives the all-free slab tail back: capacity shrinks toward the baseline (never grows, never below the floor) and every live handle keeps its index",
                capShrankToBaseline && indexStable && floorRespected,
                $"reclaimed={reclaimed} cap heavy={heavySnap.SceneCapacity} -> baseline={b.SceneCapacity} -> trimmed={t.SceneCapacity} (floor 256) | live b={b.SceneLive} trimmed={t.SceneLive} afterFrame={afterTrim.SceneLive}");
        }

        // ── scene.trim-tail: a deterministic SceneStore tail-trim — fill a wide slab, free everything except two LOW
        // -index nodes, and trim. Capacity must collapse to the floor (the highest live index is tiny), the survivors
        // must stay live with intact column data (index stability), the freed tail's freelist entries must be dropped
        // (a fresh CreateNode still returns a valid in-bounds live handle), and a second trim must be a no-op. ──
        {
            var store = new SceneStore(64);
            var keep = new NodeHandle[2];
            var doomed = new System.Collections.Generic.List<NodeHandle>();
            // Two survivors first → they take the lowest indices (1,2); then a wide block that grows the slab.
            keep[0] = store.CreateNode(7); store.Layout(keep[0]).Width = 11f;
            keep[1] = store.CreateNode(9); store.Layout(keep[1]).Width = 22f;
            for (int i = 0; i < 600; i++) doomed.Add(store.CreateNode(3));
            int grownCap = store.Capacity;
            foreach (var d in doomed) store.FreeSubtree(d);   // free the whole tail; survivors keep indices 1,2

            int reclaimed = store.TrimExcessCapacity();
            bool shrank = store.Capacity < grownCap && store.Capacity == 256;   // floor (highest live index is 2 ⇒ pow2(3)=4, raised to the 256 floor)
            bool survivorsLive = store.IsLive(keep[0]) && store.IsLive(keep[1])
                                 && store.Layout(keep[0]).Width == 11f && store.Layout(keep[1]).Width == 22f
                                 && store.ElementTypeId(keep[0]) == 7 && store.ElementTypeId(keep[1]) == 9;
            // Freelist survived the trim: a fresh node is in-bounds (< the shrunk capacity) and live.
            var fresh = store.CreateNode(5);
            bool freshOk = store.IsLive(fresh) && fresh.Raw.Index < (uint)store.Capacity;
            bool secondTrimNoop = store.TrimExcessCapacity() == 0;   // already at the floor / packed → nothing to give back
            Check("gate.scene.trim-tail SceneStore tail-trim collapses an all-free slab tail to the floor, keeps live handles' indices+data, drops the trimmed freelist entries, and is idempotent",
                reclaimed == grownCap - 256 && shrank && survivorsLive && freshOk && secondTrimNoop,
                $"reclaimed={reclaimed} cap {grownCap}->{store.Capacity} survivorsLive={survivorsLive} freshIdx={fresh.Raw.Index}<cap freshOk={freshOk} secondTrimNoop={secondTrimNoop}");
        }

        // ── wake.idle-attribution: at idle CurrentWakeReasons is None; a live AnimEngine track lights exactly the
        // Anim bit; after it finishes and the loop drains, the mask returns to None. ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("gate-wake", new Size2(240, 200), 1f)); window.Show();
            NodeHandle box = default;
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
                new W0fStaticProbe
                {
                    Build = () => new BoxEl { Width = 80, Height = 40, Fill = ColorF.FromRgba(0, 120, 255), OnRealized = h => box = h },
                });
            host.RunFrame();
            PumpToQuiescent(host);
            bool idleNone = host.CurrentWakeReasons == WakeReasons.None;

            // Cheapest live track: seed a finite compositor tween straight on the host node. HasActive => the Anim bit.
            host.Animation.Animate(box, AnimChannel.TranslateX, 0f, 40f, 250f, Easing.Linear);
            bool animBit = (host.CurrentWakeReasons & WakeReasons.Anim) != 0;

            int drained = PumpToQuiescent(host);   // tick the track to completion; it is removed at settle
            bool backToNone = host.CurrentWakeReasons == WakeReasons.None && drained < 200;
            Check("gate.wake.idle-attribution idle mask is None; a live anim track lights the Anim bit; after it finishes the mask returns to None",
                idleNone && animBit && backToNone,
                $"idle={host.CurrentWakeReasons} (was None={idleNone}) animBit={animBit} drained={drained} final={host.CurrentWakeReasons}");
        }

        // ── alloc.steady-zero: a compositor-only bound-Transform write each frame exercises the hot path (phases 3–13
        // run: HasActiveWork is true via the pending bound flush, the binding rewrites the transform, record+present
        // run) — and every such frame must allocate 0 managed bytes on the hot half. (Distinct from the existing
        // cp2.*alloc checks, which assert a NON-rendered steady frame is 0-alloc; this drives the hot path 20×.) ──
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("gate-alloc", new Size2(240, 200), 1f)); window.Show();
            var tx = new Signal<float>(0f);
            NodeHandle box = default;
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
                new W0fStaticProbe
                {
                    Build = () => new BoxEl
                    {
                        Width = 80, Height = 40, Fill = ColorF.FromRgba(0, 160, 90),
                        Transform = Prop.Of(() => Affine2D.Translation(tx.Value, 0f)), OnRealized = h => box = h,
                    },
                });
            host.RunFrame();
            // Warm: JIT the bound-flush/record/present path and let any first-touch growth settle outside the window.
            for (int i = 0; i < 8; i++) { tx.Value = i + 1; host.RunFrame(); }

            long worstAlloc = 0;
            bool appliedEvery = true;
            int idx = 0;
            for (int i = 0; i < 20; i++)
            {
                float want = 100f + i;   // a fresh value each frame ⇒ a real compositor (no-reconcile) write
                tx.Value = want;
                var f = host.RunFrame();
                if (f.HotPhaseAllocBytes > worstAlloc) { worstAlloc = f.HotPhaseAllocBytes; idx = i; }
                if (!Near(host.Scene.Paint(box).LocalTransform.Dx, want, 0.001f)) appliedEvery = false;   // proof the hot path ran
            }
            Check("gate.alloc.steady-zero 20 compositor-only bound-Transform frames each allocate 0 managed bytes on the hot half (and the bound write actually applied each frame)",
                worstAlloc == 0 && appliedEvery, $"worstHotAlloc={worstAlloc}B @frame{idx} appliedEvery={appliedEvery}");
        }

        // ── seam.publish-consume (render-thread seam, Cut A, Step 1 — single-thread foundation): the publish/consume
        // ordering + the consume-gated quarantine are the concurrency-critical logic, verified here BEFORE the render
        // thread exists (the plan's "single-thread-correct first"). Asserts (1) a published frame round-trips
        // byte-identically, (2) the consumer is handed the LATEST publish when several coalesce (DropOldest, §11), and
        // (3) the QuarantineLedger reclaims a slot freed at seq p ONLY once LastConsumedSeq passes p + Quarantine-1 (§5). ──
        {
            FluentGpu.Hosting.Threading.ThreadGuard.BindCurrent(FluentGpu.Hosting.Threading.ThreadGuard.ThreadRole.Ui);
            var seam = new FluentGpu.Hosting.Threading.SceneFramePublisher();

            // (1) round-trip byte-identity (the publisher owns the per-slot arena; Bytes(rf) reads the acquired slot)
            Span<byte> cmds = stackalloc byte[] { 1, 2, 3, 4, 5, 6, 7 };
            Span<ulong> keys = stackalloc ulong[] { 0xAABB, 0xCCDD };
            ulong s0 = seam.Publish(cmds, keys, default);
            bool acq = seam.TryAcquire(out var f0);
            bool bytesMatch = acq && seam.Bytes(f0).SequenceEqual(cmds) && seam.SortKeys(f0).SequenceEqual(keys);
            bool seqMatch = f0.PublishSeq == s0 && seam.LastConsumedSeq == s0;

            // (2) DropOldest coalesce — two publishes without a consume → acquire returns the newest
            ReadOnlySpan<ulong> noKeys = default;
            ulong s1 = seam.Publish(stackalloc byte[] { 11 }, noKeys, default);
            ulong s2 = seam.Publish(stackalloc byte[] { 22 }, noKeys, default);
            bool latest = seam.TryAcquire(out var f2) && f2.PublishSeq == s2 && s2 == s1 + 1 && s1 == s0 + 1;

            // (3) consume-gated quarantine — freed at p, reclaimable only when LastConsumedSeq > p + Quarantine-1
            var ledger = new FluentGpu.Hosting.Threading.QuarantineLedger(16);
            ulong pFree = seam.PublishSeq;                 // freed while producing the just-published frame (== LastConsumedSeq now)
            ledger.OnFreed(42, pFree);
            bool gatedBefore = !ledger.TryReclaim(seam.LastConsumedSeq, out _);   // consumer has not moved strictly past p yet
            Span<byte> one = stackalloc byte[] { 0 };
            for (ulong need = pFree + (ulong)FluentGpu.Hosting.Threading.QuarantinePolicy.Quarantine; seam.LastConsumedSeq < need;)
            { seam.Publish(one, noKeys, default); seam.TryAcquire(out _); }
            bool reclaimsAfter = ledger.TryReclaim(seam.LastConsumedSeq, out int reclaimed) && reclaimed == 42 && ledger.PendingCount == 0;

            Check("gate.seam.publish-consume (Cut A) round-trips a published frame byte-identically, hands the consumer the LATEST publish (DropOldest coalesce), and the consume-gated QuarantineLedger reclaims a freed slot ONLY after LastConsumedSeq passes its free-seq + Quarantine-1",
                bytesMatch && seqMatch && latest && gatedBefore && reclaimsAfter,
                $"bytes={bytesMatch} seq=({s0},{s1},{s2}) latest={latest} gatedBefore={gatedBefore} reclaimsAfter={reclaimsAfter} pending={ledger.PendingCount}");
        }

        // ── seam.race (deterministic real-thread soak): the consume-gated quarantine (§5.3) must hold ACROSS the volatile
        // publish/consume handshake under a STALLED reader — the invariant the async flip (Step 5) depends on. A consumer
        // thread (bound Render) is event-gated so the producer controls exactly when a consume happens (no sleeps ⇒
        // deterministic, not flaky): while the reader is held, a slot freed at seq p is NOT reclaimed; once the reader's
        // TryAcquire advances LastConsumedSeq past p + Quarantine-1, it is. A second real thread exercises the
        // cross-thread Volatile release/acquire, which single-thread logic cannot. ──
        {
            var raceSeam = new FluentGpu.Hosting.Threading.SceneFramePublisher();
            var raceLedger = new FluentGpu.Hosting.Threading.QuarantineLedger(64);
            FluentGpu.Hosting.Threading.ThreadGuard.BindCurrent(FluentGpu.Hosting.Threading.ThreadGuard.ThreadRole.Ui);
            var go = new System.Threading.SemaphoreSlim(0);
            var did = new System.Threading.SemaphoreSlim(0);
            var raceFlags = new bool[2];   // [0]=stop, [1]=consumer TryAcquire failed
            var consumer = new System.Threading.Thread(() =>
            {
                FluentGpu.Hosting.Threading.ThreadGuard.BindCurrent(FluentGpu.Hosting.Threading.ThreadGuard.ThreadRole.Render);
                while (true)
                {
                    go.Wait();
                    if (raceFlags[0]) { did.Release(); return; }
                    if (!raceSeam.TryAcquire(out _)) raceFlags[1] = true;
                    did.Release();
                }
            }) { IsBackground = true, Name = "seam-race-consumer" };
            consumer.Start();

            System.Span<byte> raceOne = stackalloc byte[] { 7 };
            System.ReadOnlySpan<ulong> raceNone = default;
            ulong rp1 = raceSeam.Publish(raceOne, raceNone, default);
            raceLedger.OnFreed(100, rp1);                                        // freed while producing rp1
            raceSeam.Publish(raceOne, raceNone, default);
            raceSeam.Publish(raceOne, raceNone, default);
            bool raceGatedWhileStalled = !raceLedger.TryReclaim(raceSeam.LastConsumedSeq, out _);  // reader held ⇒ LastConsumedSeq 0 ⇒ gated

            go.Release(); did.Wait();                                            // reader acquires the latest ⇒ LastConsumedSeq passes rp1 + Quarantine-1
            bool raceReclaimsAfter = raceLedger.TryReclaim(raceSeam.LastConsumedSeq, out int raceGot) && raceGot == 100;

            raceFlags[0] = true; go.Release(); did.Wait(); consumer.Join(1000);
            go.Dispose(); did.Dispose();

            Check("gate.seam.race the consume-gated quarantine holds across the cross-thread publish/consume handshake under a stalled reader — a slot freed at seq p stays quarantined until the consumer thread's LastConsumedSeq passes p + Quarantine-1 (deterministic real-thread seam.race soak)",
                raceGatedWhileStalled && raceReclaimsAfter && !raceFlags[1],
                $"gatedWhileStalled={raceGatedWhileStalled} reclaimsAfter={raceReclaimsAfter} consumerAcquireFailed={raceFlags[1]} lastConsumed={raceSeam.LastConsumedSeq}");
        }

        // ── decode.cancel-reclaim: the DecodeScheduler cancellation map must not grow with cancels (the WaveeMusic 10k-row
        // scroll cancels rows constantly). Two paths, both must end at CanceledPending==0:
        //   (A) cancel-before-claim — the dominant scroll case. With both workers parked inside a barrier-held codec, the
        //       remaining ids are still queued; cancelling a subset must drop them at claim WITHOUT leaving a tombstone,
        //       and they must never apply pixels while the survivors do.
        //   (B) cancel-in-flight — a cancel that races a claimed decode sets exactly one tombstone (proven: CanceledPending
        //       jumps to 1), which the worker reclaims at its terminal point so the map drains back to 0.
        // Toggling off the Cancel/Process-finally reclaim lines makes (A)/(B) respectively leave residue → this fails.
        {
            // (A) ───────────────────────────────────────────────────────────────────────────────────────────────────────
            var releaseA = new System.Threading.ManualResetEventSlim(false);
            int inDecodeA = 0;
            var codecA = new TestCodec(() =>
            {
                System.Threading.Interlocked.Increment(ref inDecodeA);
                releaseA.Wait();   // park the worker INSIDE the codec → it stays in-flight, holding its claimed id
            });
            const int N = 8;
            var appliedA = new bool[N + 1];
            bool reclaimA, suppressedA, survivedA;
            using (var sched = new DecodeScheduler(codecA, new TestFetcher(), new DecodeOptions { MaxConcurrency = 2 }))
            {
                for (int i = 1; i <= N; i++) sched.Begin(i, "c" + i, 8, 8);   // FIFO Visible lane: workers claim 1,2 first
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sched.Inflight < 2 && sw.ElapsedMilliseconds < 5000) System.Threading.Thread.Sleep(2);
                // Both workers are now parked in the codec ⇒ ids 3..8 are still queued (unclaimed). Cancel a subset:
                // these are removed from _reqs and dropped at claim — no tombstone should be created.
                sched.Cancel(4); sched.Cancel(6); sched.Cancel(8);
                int tombAfterQueuedCancel = sched.CanceledPending;   // must be 0: queued cancels leave nothing behind
                releaseA.Set();                                      // let the parked decodes and all survivors run
                sw.Restart();
                // Drain on Inflight+RequestCount only: _queued is never decremented for a cancel-before-claim id
                // (TryClaim dequeues-and-skips it), so QueueDepth would hold the loop to the full timeout.
                while (sw.ElapsedMilliseconds < 5000
                       && (sched.Inflight > 0 || sched.RequestCount > 0))
                {
                    sched.Pump((id, ok, w, h, f, a) => { }, (id, px, w, h) => { if (id >= 0 && id <= N) appliedA[id] = true; });
                    System.Threading.Thread.Sleep(1);                // workers finish in ms; don't spin the pump hot
                }
                for (int i = 0; i < 4; i++)                          // a few extra drains to flush late completions
                    sched.Pump((id, ok, w, h, f, a) => { }, (id, px, w, h) => { if (id >= 0 && id <= N) appliedA[id] = true; });

                reclaimA = tombAfterQueuedCancel == 0 && sched.CanceledPending == 0;
                suppressedA = !appliedA[4] && !appliedA[6] && !appliedA[8];                       // canceled-while-queued: never applied
                survivedA = appliedA[1] && appliedA[2] && appliedA[3] && appliedA[5] && appliedA[7]; // the rest decoded + applied
            }

            // (B) ───────────────────────────────────────────────────────────────────────────────────────────────────────
            var releaseB = new System.Threading.ManualResetEventSlim(false);
            int inDecodeB = 0;
            var codecB = new TestCodec(() =>
            {
                System.Threading.Interlocked.Increment(ref inDecodeB);
                releaseB.Wait();   // single worker parks here, holding id 100 in-flight
            });
            bool tombSet, reclaimB;
            using (var sched = new DecodeScheduler(codecB, new TestFetcher(), new DecodeOptions { MaxConcurrency = 1 }))
            {
                sched.Begin(100, "inflight", 8, 8);
                var sw = System.Diagnostics.Stopwatch.StartNew();
                // Wait for the worker to be INSIDE the codec (not just Inflight==1, which it sets BEFORE the entry
                // _canceled check): only then has the entry-cancel branch passed, so Cancel deterministically races
                // a claimed decode and must set exactly one tombstone.
                while (System.Threading.Volatile.Read(ref inDecodeB) < 1 && sw.ElapsedMilliseconds < 5000)
                    System.Threading.Thread.Sleep(2);
                sched.Cancel(100);                       // races a CLAIMED decode (TryRemove fails) ⇒ sets one tombstone
                tombSet = sched.CanceledPending == 1;    // proof a tombstone exists to reclaim
                releaseB.Set();
                sw.Restart();
                while (sw.ElapsedMilliseconds < 5000
                       && (sched.Inflight > 0 || sched.CanceledPending > 0))
                {
                    sched.Pump((id, ok, w, h, f, a) => { }, (id, px, w, h) => { });
                    System.Threading.Thread.Sleep(1);
                }
                reclaimB = sched.CanceledPending == 0;   // Process's finally / Pump drain reclaimed it
            }
            releaseA.Dispose(); releaseB.Dispose();

            Check("gate.decode.cancel-reclaim canceled image ids do not accumulate in DecodeScheduler (queued cancels leave no tombstone; an in-flight cancel's tombstone is reclaimed at decode completion) and canceled decodes apply no pixels",
                reclaimA && suppressedA && survivedA && tombSet && reclaimB,
                $"(A) queuedCancelLeftZero+drainedToZero={reclaimA} canceledSuppressed={suppressedA} survivorsApplied={survivedA} | (B) tombstoneSet={tombSet} reclaimedToZero={reclaimB}");
        }
    }

    static void PaletteContrastChecks()
    {
        var warm = Tok.WarmPalette;
        // Warm calibration: flattened shell anchors (values verified against the anchor-solve math) + the untouched
        // opaque warm TokenSet anchors.
        bool warmFrame = PaletteBuilder.NearColor(ColorContrast.Flatten(warm.LightShell.Toolbar, MicaRef.LightDefault), ColorF.FromRgba(0xE4, 0xDF, 0xD3));
        bool warmFile = PaletteBuilder.NearColor(ColorContrast.Flatten(warm.LightShell.FileArea, MicaRef.LightDefault), ColorF.FromRgba(0xF0, 0xF0, 0xEE));
        bool warmCard = PaletteBuilder.NearColor(warm.Light.FillCardDefault, ColorF.FromRgba(0xFC, 0xFB, 0xF9));
        bool warmTert = PaletteBuilder.NearColor(warm.Light.TextTertiary, ColorF.FromRgba(0x65, 0x64, 0x60));
        Check("palette.warm.calibration flattened shell + token anchors (toolbar, file area, card fill, tertiary text)",
            warmFrame && warmFile && warmCard && warmTert, $"frame={warmFrame} file={warmFile} card={warmCard} tertiary={warmTert}");

        var neutral = Tok.NeutralPalette;
        bool filesFileAreaRaw = PaletteBuilder.NearColor(neutral.LightShell.FileArea, PaletteBuilder.FilesLightFileArea);
        bool filesFileAreaFlat = PaletteBuilder.NearColor(
            ColorContrast.Flatten(neutral.LightShell.FileArea, MicaRef.LightDefault),
            ColorContrast.Flatten(PaletteBuilder.FilesLightFileArea, MicaRef.LightDefault));
        bool filesChrome = PaletteBuilder.NearColor(neutral.LightShell.Toolbar, ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xB3));
        bool filesCard = PaletteBuilder.NearColor(neutral.Light.FillCardDefault, ColorF.FromRgba(0xFF, 0xFF, 0xFF, 0xB3));
        Check("palette.files.filearea neutral shell matches Files #C0FCFCFC + LayerOnMica chrome + WinUI card fill",
            filesFileAreaRaw && filesFileAreaFlat && filesChrome && filesCard,
            $"raw={filesFileAreaRaw} flat={filesFileAreaFlat} chrome={filesChrome} card={filesCard}");

        ThemePalette[] all = [Tok.WarmPalette, Tok.SlatePalette, Tok.NeutralPalette, Tok.AccentTintedPalette];
        bool contrastOk = true;
        bool ladderOk = true;
        var detail = new System.Text.StringBuilder();
        foreach (var p in all)
        {
            foreach (var (themeKind, set, shell) in new (ThemeKind, TokenSet, ShellPalette)[]
            {
                (ThemeKind.Light, p.Light, p.LightShell),
                (ThemeKind.Dark, p.Dark, p.DarkShell),
            })
            {
                // The hardest background for a theme's ink is its BRIGHTEST composited surface (dark-on-light and
                // light-on-dark both max the ratio's denominator there): the zebra row flattened over the
                // translucent page card on the brightest assumed Mica, or (light only) the opaque card if lighter.
                // Ink alpha is intentionally ignored (as before): tiers assert at full ink strength — WinUI's own
                // translucent dark tiers would fail a strict flattened-ink check and they're not ours to redesign.
                ColorF micaBright = themeKind == ThemeKind.Light ? MicaRef.LightBright : MicaRef.DarkBright;
                ColorF zebraHost = ColorContrast.Flatten(shell.RowZebra, ColorContrast.Flatten(shell.FileArea, micaBright));
                ColorF hardestHost = themeKind == ThemeKind.Light && ColorContrast.RelativeLuminance(set.FillCardDefault) >= ColorContrast.RelativeLuminance(zebraHost)
                    ? set.FillCardDefault : zebraHost;
                if (!ColorContrast.MeetsAaText(set.TextPrimary, hardestHost)) { contrastOk = false; detail.Append($" {p.Id}/{themeKind}/primary"); }
                if (!ColorContrast.MeetsAaText(set.TextSecondary, hardestHost)) { contrastOk = false; detail.Append($" {p.Id}/{themeKind}/secondary"); }
                if (!ColorContrast.MeetsAaText(set.TextTertiary, hardestHost)) { contrastOk = false; detail.Append($" {p.Id}/{themeKind}/tertiary"); }
                if (themeKind == ThemeKind.Light)
                {
                    ColorF fFrame = ColorContrast.Flatten(shell.Toolbar, MicaRef.LightDefault);
                    ColorF fRail = ColorContrast.Flatten(shell.Sidebar, MicaRef.LightDefault);
                    ColorF fPage = ColorContrast.Flatten(shell.FileArea, MicaRef.LightDefault);
                    // Files-faithful neutral uses the same LayerOnMica token for toolbar + sidebar — skip frame≈rail.
                    // Rail≈page is also intentionally flat (Files separates the file area via border + ThemeShadow, not luminance).
                    if (p.Id != "neutral" && ColorContrast.LuminanceDelta(fFrame, fRail) < 0.05f) { ladderOk = false; detail.Append($" {p.Id}/frame-rail"); }
                    if (p.Id != "neutral" && ColorContrast.LuminanceDelta(fRail, fPage) < 0.05f) { ladderOk = false; detail.Append($" {p.Id}/rail-page"); }
                    if (ColorContrast.LuminanceDelta(fPage, set.FillCardDefault) < 0.05f) { ladderOk = false; detail.Append($" {p.Id}/page-card"); }
                }
            }
        }
        Check("palette.contrast all presets pass AA text tiers on the brightest composited hosting surface", contrastOk, detail.ToString());
        Check("palette.ladder light shell adjacent flattened surfaces have >=5% luminance delta", ladderOk, detail.ToString());

        // Preset distinctness: pairwise max-channel delta of the flattened chrome bar must be perceptible in both
        // themes. Accent-involving pairs get a relaxed floor — the accent seed legitimately converges on its blue
        // neighbors while the (default 210°) OS accent is blue; it diverges with any non-blue accent.
        static int MaxChannelDelta(in ColorF a, in ColorF b)
        {
            static int Ch(float f) => (int)MathF.Round(f * 255f);
            return Math.Max(Math.Abs(Ch(a.R) - Ch(b.R)), Math.Max(Math.Abs(Ch(a.G) - Ch(b.G)), Math.Abs(Ch(a.B) - Ch(b.B))));
        }
        bool distinctOk = true;
        var dDetail = new System.Text.StringBuilder();
        for (int i = 0; i < all.Length; i++)
        {
            for (int j = i + 1; j < all.Length; j++)
            {
                bool involvesAccent = all[i].Id == "accent" || all[j].Id == "accent";
                int lightDelta = MaxChannelDelta(
                    ColorContrast.Flatten(all[i].LightShell.Toolbar, MicaRef.LightDefault),
                    ColorContrast.Flatten(all[j].LightShell.Toolbar, MicaRef.LightDefault));
                int darkDelta = MaxChannelDelta(
                    ColorContrast.Flatten(all[i].DarkShell.Toolbar, MicaRef.DarkDefault),
                    ColorContrast.Flatten(all[j].DarkShell.Toolbar, MicaRef.DarkDefault));
                int lightFloor = involvesAccent ? 5 : 8, darkFloor = involvesAccent ? 3 : 6;
                if (lightDelta < lightFloor || darkDelta < darkFloor)
                {
                    distinctOk = false;
                    dDetail.Append($" {all[i].Id}-{all[j].Id}(L{lightDelta}/D{darkDelta})");
                }
            }
        }
        Check("palette.distinct presets read visibly different (pairwise flattened-toolbar max-channel delta)", distinctOk, dDetail.ToString());

        int e0 = Tok.Epoch;
        var kind = Tok.Theme;
        Tok.Use(Tok.SlatePalette, kind);
        bool epochBumps = Tok.Epoch > e0;
        Tok.Use(Tok.WarmPalette, kind);
        Check("palette.switch Tok.Use(palette, kind) bumps Epoch on palette-only change", epochBumps, $"epoch={Tok.Epoch}");
    }
}
