using System;
using System.Collections.Generic;
using FluentGpu.Foundation;
using FluentGpu.Media;
using FluentGpu.Pal;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

// ── gate.video.slot.* (L2-02: video slot binding and readiness) ─────────────────────────────────────────────────────────
// The headless seam has no DirectComposition presenter, so the registry's render-side contract is gated against a
// recording presenter drained by hand (exactly what the host's phase-11 drain does), and the element's contract —
// visibility written LAST by the element, readiness per SLOT, pump registered in a layout effect — is gated by a source
// scan of PumpNow/Render, the same way the sibling gate.media.el.* checks pin branch shapes a headless host cannot reach.
static partial class ControlsSuite
{
    sealed class SlotRecordingPresenter : IVideoPresenter
    {
        public readonly List<string> Calls = new();
        public bool LastVisible = true;
        public int FailBinds;
        private uint _next = 1;

        public VideoSurfaceId CreateSurface() { var id = new VideoSurfaceId(_next++); Calls.Add("Create"); return id; }
        public bool BindSurfaceHandle(VideoSurfaceId id, nuint dcompSurfaceHandle)
        {
            if (FailBinds > 0) { FailBinds--; Calls.Add("BindFail"); return false; }
            Calls.Add("Bind");
            return true;
        }
        public void Place(VideoSurfaceId id, RectF deviceRect, float opacity, int z) => Calls.Add("Place");
        public void SetVisible(VideoSurfaceId id, bool visible) { LastVisible = visible; Calls.Add("Visible"); }
        public void Destroy(VideoSurfaceId id) => Calls.Add("Destroy");
        public void Commit() { }
        public int Count(string name) { int n = 0; foreach (var c in Calls) if (c == name) n++; return n; }
    }

    static void VideoSlotReadinessChecks()
    {
        // A presenter swap (device recovery) rebuilds the live surface with no new intent.
        {
            var reg = new VideoSurfaceRegistry();
            int token = reg.Acquire();
            reg.Bind(token, 0x1234);
            reg.Place(token, new RectF(10f, 20f, 100f, 50f));
            var a = new SlotRecordingPresenter();
            reg.Drain(a, 1f);
            int aCalls = a.Calls.Count;
            var b = new SlotRecordingPresenter();
            reg.Drain(b, 1f);
            Check("gate.video.slot.presenter-swap-rebuilds",
                b.Count("Create") == 1 && b.Count("Bind") == 1 && b.Count("Place") == 1 && reg.HasLiveSurface && a.Calls.Count == aCalls,
                $"create={b.Count("Create")} bind={b.Count("Bind")} place={b.Count("Place")} live={reg.HasLiveSurface} oldTouched={a.Calls.Count != aCalls}");
        }

        // A failed bind is not recorded as bound and retries without any new handle.
        {
            var reg = new VideoSurfaceRegistry();
            int token = reg.Acquire();
            reg.Bind(token, 0x77);
            reg.Place(token, new RectF(0f, 0f, 64f, 36f));
            var p = new SlotRecordingPresenter { FailBinds = 1 };
            reg.Drain(p, 1f);
            bool notLive = !reg.HasLiveSurface;
            for (int i = 0; i < 4; i++) reg.Drain(p, 1f);
            Check("gate.video.slot.failed-bind-retries",
                notLive && p.Count("BindFail") == 1 && p.Count("Bind") == 1 && reg.HasLiveSurface,
                $"notLiveAfterFailure={notLive} fail={p.Count("BindFail")} bind={p.Count("Bind")} live={reg.HasLiveSurface}");
        }

        // An inactive (parked) player's hide drains as Visible=false and a later bind/placement never flips it back on.
        {
            var reg = new VideoSurfaceRegistry();
            int token = reg.Acquire();
            reg.Place(token, new RectF(0f, 0f, 320f, 180f));
            reg.Bind(token, 0xCAFE);
            reg.SetVisible(token, false);
            reg.SetContentSize(token, 1280, 720);
            var p = new SlotRecordingPresenter();
            reg.Drain(p, 1f);
            Check("gate.video.slot.hidden-drains-invisible", !p.LastVisible && !reg.HasLiveSurface,
                $"lastVisible={p.LastVisible} live={reg.HasLiveSurface}");
        }

        // Readiness is per slot: false until THIS slot's surface is bound, then true once the UI thread syncs it.
        {
            var reg = new VideoSurfaceRegistry { RequireSlotBinding = true };
            int token = reg.Acquire();
            bool before = reg.Bound(token).Peek();
            reg.Bind(token, 0x42);
            reg.Place(token, new RectF(0f, 0f, 100f, 100f));
            reg.Drain(new SlotRecordingPresenter(), 1f);
            bool beforeSync = reg.Bound(token).Peek();
            reg.PumpPending(1f);
            bool after = reg.Bound(token).Peek();
            Check("gate.video.slot.bound-per-slot", !before && !beforeSync && after,
                $"before={before} beforeSync={beforeSync} afterSync={after}");
        }

        // The element contract (source scan): readiness per slot, visibility written last, pump in a layout effect.
        {
            string? el = ReadRepoFile("src/FluentGpu.Controls/Media/MediaPlayerElement.cs");
            string? mf = ReadRepoFile("src/FluentGpu.Windows/Media/MfMediaSession.cs");
            bool found = el is not null && mf is not null;
            bool slotGate = el is not null
                && el.Contains("bool slotBound = binding.Bound.Value;", StringComparison.Ordinal)
                && el.Contains("&& slotBound;", StringComparison.Ordinal);
            bool writesLast = el is not null
                && el.Contains("b.SetVisible(active && !audioOnly && !Player.VideoSurface.Peek().IsNone);", StringComparison.Ordinal)
                && !el.Contains("if (audioOnly) b.SetVisible(false);", StringComparison.Ordinal);
            bool sessionSilent = mf is not null && !mf.Contains("binding.SetVisible(true)", StringComparison.Ordinal);
            bool layoutPump = false;
            if (el is not null)
            {
                int reg = el.IndexOf("binding.RegisterPump(this, PumpNow)", StringComparison.Ordinal);
                int le = reg < 0 ? -1 : el.LastIndexOf("UseLayoutEffect(", reg, StringComparison.Ordinal);
                layoutPump = le >= 0 && reg - le < 400;
            }
            Check("gate.video.slot.element-gates-hole-on-slot", found && slotGate, $"found={found} slotGate={slotGate}");
            Check("gate.video.slot.element-writes-visibility-last", found && writesLast && sessionSilent,
                $"found={found} writesLast={writesLast} sessionSilent={sessionSilent}");
            Check("gate.video.slot.pump-registered-in-layout-effect", found && layoutPump, $"found={found} layoutPump={layoutPump}");
        }
    }
}
