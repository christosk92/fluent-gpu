using System;
using System.Collections.Generic;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Text;
using static FluentGpu.VerticalSlice.Harness.Gate;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// Gates for the video structural drain (F208) and the single composition commit per render turn (F080). A real pop-out cannot
/// present headlessly (a Headless window never spawns a render thread and there is no DirectComposition device in this
/// harness), so the DECISIONS are gated where they live: the render loop's <c>preTurn</c> hook runs the registries' early
/// drain before the present-slot take, on a bare wake, with no UI publication; only create and bind move early; and the
/// deferred drains of a turn apply without committing so the host makes ONE commit and then publishes readiness.
/// </summary>
static class DetachedVideoSuite
{
    public static void Run(StringTable strings)
    {
        _ = strings;
        StructuralDrainRunsBeforeTheSlotWaitChecks();
        DeferredCommitChecks();
    }

    private sealed class RecordingPresenter : IVideoPresenter
    {
        public readonly List<string> Calls = new();
        public int Commits, Applies;
        private uint _next = 1;

        public VideoSurfaceId CreateSurface() { var id = new VideoSurfaceId(_next++); Calls.Add("Create"); return id; }
        public bool BindSurfaceHandle(VideoSurfaceId id, nuint dcompSurfaceHandle) { Calls.Add("Bind"); return true; }
        public void Place(VideoSurfaceId id, RectF deviceRect, float opacity, int z) => Calls.Add("Place");
        public void SetVisible(VideoSurfaceId id, bool visible) => Calls.Add("Visible");
        public void Destroy(VideoSurfaceId id) => Calls.Add("Destroy");
        public void ApplyPending() => Applies++;
        public void Commit() => Commits++;
        public int Count(string name) { int n = 0; foreach (var c in Calls) if (c == name) n++; return n; }
    }

    // gate.detached-video.*: a pop-out's handle arrives with NO publication behind it. The loop's preTurn hook (what AppHost wires
    // to its early drain) creates and binds the surface within two turns, before the present-slot take of a turn that has
    // something to present, while a placement move and a destroy of the same registry wait for the coupled drain.
    static void StructuralDrainRunsBeforeTheSlotWaitChecks()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var seam = new SceneFramePublisher();
        var reg = new VideoSurfaceRegistry { RequireSlotBinding = true };
        var presenter = new RecordingPresenter();
        var order = new List<string>();
        byte[] one = [1];
        int preTurns = 0;
        int firstCreateTurn = 0;
        var rt = new RenderThread(seam, _ => order.Add("submit"), async: false,
            takePresentSlot: _ => { order.Add("take"); return true; },
            preTurn: () =>
            {
                preTurns++;
                order.Add("pre");
                reg.DrainStructural(presenter, 1f, deferCommit: true);
                if (firstCreateTurn == 0 && presenter.Count("Create") > 0) firstCreateTurn = preTurns;
                // What the host does at the end of its turn: ONE device commit, then each registry publishes its readiness.
                if (presenter.Applies > presenter.Commits) { presenter.Commit(); reg.PublishCommitted(); order.Add("commit"); }
            });
        try
        {
            int token = reg.Acquire();
            reg.Bind(token, 0xCAFE);                       // the producer's handle arrives (UI thread) ...
            reg.Place(token, new RectF(10f, 20f, 320f, 180f));
            rt.DrainSync();                                 // ... and one bare turn follows: no publication, no motion

            Check("gate.detached-video.bound-within-2-turns-without-a-ui-publish a handle with no publication behind it was created, bound and placed by the early drain",
                presenter.Count("Create") == 1 && presenter.Count("Bind") == 1 && presenter.Count("Place") == 1 && firstCreateTurn is >= 1 and <= 2
                && rt.FreshPresents == 0 && !order.Contains("submit") && !order.Contains("take"),
                $"create={presenter.Count("Create")} bind={presenter.Count("Bind")} place={presenter.Count("Place")} turn={firstCreateTurn} fresh={rt.FreshPresents} order={string.Join(',', order)}");
            reg.PumpPending(1f);
            Check("gate.detached-video.readiness-published-after-the-commit the slot reads bound only once the turn's commit ran",
                reg.Bound(token).Peek() && presenter.Commits == 1, $"bound={reg.Bound(token).Peek()} commits={presenter.Commits}");

            // A placement move and a destroy do not ride the early drain; a turn that presents takes its slot AFTER preTurn.
            order.Clear();
            reg.Place(token, new RectF(30f, 40f, 160f, 90f));
            reg.Release(token);
            seam.Publish(one, default, default);
            rt.DrainSync();
            Check("gate.detached-video.preturn-precedes-the-slot-take a presenting turn ran the early drain before taking its present slot",
                order.IndexOf("pre") == 0 && order.IndexOf("take") == 1 && order.IndexOf("submit") == 2, string.Join(',', order));
            Check("gate.detached-video.place-and-destroy-stay-coupled the early drain neither re-placed nor destroyed the released surface",
                presenter.Count("Destroy") == 0 && presenter.Count("Place") == 1,
                $"destroys={presenter.Count("Destroy")} places={presenter.Count("Place")}");
            reg.Drain(presenter, 1f);                       // the coupled drain (rides the UI frame's present) owns the destroy
            Check("gate.detached-video.destroy-applied-by-the-coupled-drain the UI frame's drain destroyed it", presenter.Count("Destroy") == 1);
        }
        finally { rt.Dispose(); }
    }

    // gate.detached-video.deferred-*: the parent's and a pop-out's registries drain deferred in one turn - each presenter only
    // APPLIES - and the host's single device commit follows, then publishes each registry's readiness.
    static void DeferredCommitChecks()
    {
        var parentReg = new VideoSurfaceRegistry();
        var childReg = new VideoSurfaceRegistry { RequireSlotBinding = true };
        var parentPresenter = new RecordingPresenter();
        var childPresenter = new RecordingPresenter();
        int pt = parentReg.Acquire();
        parentReg.Bind(pt, 0x1);
        parentReg.Place(pt, new RectF(0f, 0f, 100f, 100f));
        int ct = childReg.Acquire();
        childReg.Bind(ct, 0x2);
        childReg.Place(ct, new RectF(0f, 0f, 200f, 100f));

        parentReg.Drain(parentPresenter, 1f, deferCommit: true);
        childReg.Drain(childPresenter, 1f, deferCommit: true);
        childReg.PumpPending(1f);
        Check("gate.detached-video.deferred-apply-only both windows applied their placement and neither committed on its own",
            parentPresenter.Applies == 1 && childPresenter.Applies == 1 && parentPresenter.Commits == 0 && childPresenter.Commits == 0
            && !childReg.Bound(ct).Peek(),
            $"parent={parentPresenter.Applies}/{parentPresenter.Commits} child={childPresenter.Applies}/{childPresenter.Commits} bound={childReg.Bound(ct).Peek()}");

        // The host's one device commit happens here; only then does each registry publish.
        bool childEdge = childReg.PublishCommitted();
        parentReg.PublishCommitted();
        childReg.PumpPending(1f);
        Check("gate.detached-video.readiness-after-the-single-commit the pop-out's slot is bound only after the turn's one commit",
            childEdge && childReg.Bound(ct).Peek(), $"edge={childEdge} bound={childReg.Bound(ct).Peek()}");

        // A present that stood down applies only releases: the move stays dirty for the next real present.
        parentReg.Place(pt, new RectF(5f, 5f, 100f, 100f));
        int places = parentPresenter.Count("Place");
        parentReg.Drain(parentPresenter, 1f, deferCommit: true, placement: false);
        Check("gate.detached-video.standdown-keeps-placement-dirty a stood-down present committed no placement for its target",
            parentPresenter.Count("Place") == places, $"places {places} -> {parentPresenter.Count("Place")}");
        parentReg.Drain(parentPresenter, 1f, deferCommit: true);
        Check("gate.detached-video.standdown-placement-lands-on-the-next-real-present the held move applied on the next drain",
            parentPresenter.Count("Place") == places + 1, $"places={parentPresenter.Count("Place")}");
    }
}
