using System.Collections.Generic;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

// Unkeyed child identity (TreeReconciler.ReconcileChildrenCore + ChildReconcilePlan.Step, mode decided by
// UnkeyedPairing.Ordinal). Keyed children always match by Key. Unkeyed children:
//   • ORDINAL when the unkeyed population is unchanged (same count): the k-th unkeyed new child pairs with the k-th
//     unkeyed old child (keyed siblings skipped) — keyed churn never shifts unkeyed identity;
//   • POSITIONAL (the former same-index rule) when an unkeyed child was added/removed — which also keeps a keyed⇄unkeyed
//     flip at one slot from shifting the unkeyed siblings after it.
// Reuse needs the element type to match; a mismatch consumes the ordinal/slot (no slide).
//
// The bug these gates pin (Wavee now-playing header): a container [title, artists] — both UNKEYED, structurally
// identical BoxEl+component lines — gets a KEYED line inserted in front → [remoteLine, title, artists]. The former
// same-ABSOLUTE-index rule paired the new title with the old ARTISTS node + component instance (whose factory froze at
// mount, so the header showed the artist name twice) and remounted artists. (a), (b), (a3), (e), (f), the ordinal half
// of (c2) and the mixed half of (d) FAIL under that rule; (a2) and (e2) — a keyed⇄unkeyed flip at one slot — FAIL under
// a PURE ordinal rule (which is why the mode is count-gated); (c) holds under all three.
static class UnkeyedOrdinalChecks
{
    // A propless line component that records, per instance, the label its factory froze at mount. The factory runs ONLY
    // on a (re)mount, so `made` counts constructions and each instance's Context.AnchorNode says which slot it sits in.
    static Element Line(string label, List<OrdinalLineProbe> made) => new BoxEl
    {
        Direction = 1,
        Children = [Embed.Comp(() => { var c = new OrdinalLineProbe { Label = label }; made.Add(c); return c; })],
    };

    static Element Keyed(string key) => new BoxEl { Key = key, Width = 10, Height = 4 };
    static Element Remote() => Keyed("remote-line");
    static Element Placeholder() => new BoxEl { Width = 10, Height = 4 };   // UNKEYED, same element type as a Line wrapper

    static OrdinalLineProbe? Made(List<OrdinalLineProbe> made, string label)
    {
        foreach (var c in made) if (c.Label == label) return c;
        return null;
    }

    // The component instance labelled `label` sits in the line wrapper at `wrapper` (its anchor is the wrapper's child).
    static bool InSlot(SceneStore s, List<OrdinalLineProbe> made, string label, NodeHandle wrapper)
        => Made(made, label) is { } c && !wrapper.IsNull && c.Context.AnchorNode == Child(s, wrapper, 0)
           && s.IsLive(c.Context.AnchorNode);

    static BoxEl Row(params Element[] kids) => new() { Direction = 1, Children = kids };

    internal static void Run(StringTable strings)
    {
        // (a) [U_title, U_artists] → [K, U_title', U_artists']: both unkeyed lines keep their node AND component instance
        //     (the artists line must not remount; the title slot must not adopt the artists instance).
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            var made = new List<OrdinalLineProbe>();
            var t1 = Row(Line("title", made), Line("artists", made));
            recon.ReconcileRoot(t1, null);
            var wTitle = Child(scene, scene.Root, 0);
            var wArtists = Child(scene, scene.Root, 1);
            bool mounted = made.Count == 2 && InSlot(scene, made, "title", wTitle) && InSlot(scene, made, "artists", wArtists);

            var t2 = Row(Remote(), Line("title", made), Line("artists", made));
            recon.ReconcileRoot(t2, t1);
            var remote = Child(scene, scene.Root, 0);
            bool nodesKept = Child(scene, scene.Root, 1) == wTitle && Child(scene, scene.Root, 2) == wArtists
                             && scene.IsLive(wTitle) && scene.IsLive(wArtists) && remote != wTitle && remote != wArtists;
            bool instancesKept = made.Count == 2
                                 && InSlot(scene, made, "title", Child(scene, scene.Root, 1))
                                 && InSlot(scene, made, "artists", Child(scene, scene.Root, 2));
            Check("gate.reconcile.unkeyed-ordinal.keyed-insert-ahead a keyed child inserted in FRONT of unkeyed siblings keeps each unkeyed child's node and component instance ([title, artists] → [remote, title, artists])",
                mounted && nodesKept && instancesKept,
                $"mounted={mounted} nodesKept={nodesKept} instancesKept={instancesKept} constructions={made.Count} (want 2)");
        }

        // (b) the reverse: [K, U_a, U_b] → [U_a', U_b']: removing the keyed child ahead must not shift the unkeyed pairing.
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            var made = new List<OrdinalLineProbe>();
            var t1 = Row(Remote(), Line("a", made), Line("b", made));
            recon.ReconcileRoot(t1, null);
            var kRemote = Child(scene, scene.Root, 0);
            var wA = Child(scene, scene.Root, 1);
            var wB = Child(scene, scene.Root, 2);

            var t2 = Row(Line("a", made), Line("b", made));
            recon.ReconcileRoot(t2, t1);
            bool nodesKept = Child(scene, scene.Root, 0) == wA && Child(scene, scene.Root, 1) == wB
                             && Child(scene, scene.Root, 2).IsNull && !scene.IsLive(kRemote);
            bool instancesKept = made.Count == 2
                                 && InSlot(scene, made, "a", Child(scene, scene.Root, 0))
                                 && InSlot(scene, made, "b", Child(scene, scene.Root, 1));
            Check("gate.reconcile.unkeyed-ordinal.keyed-remove-ahead removing a keyed child AHEAD of unkeyed siblings keeps each unkeyed child's node and component instance ([remote, a, b] → [a, b])",
                nodesKept && instancesKept,
                $"nodesKept={nodesKept} instancesKept={instancesKept} remoteFreed={!scene.IsLive(kRemote)} constructions={made.Count} (want 2)");
        }

        // (a2) a keyed⇄unkeyed FLIP at one slot (`cond ? keyedLine : placeholder` ahead of [title, artists]) — the unkeyed
        //      count changes, so pairing stays same-index and the lines behind the flipped slot keep node + instance in
        //      BOTH directions. A pure ordinal rule would pair the placeholder with the title line and hand the title
        //      slot the artists instance — the very swap (a) fixes (NavigationView's top-bar "More" button has this shape).
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            var made = new List<OrdinalLineProbe>();
            var t1 = Row(Remote(), Line("title", made), Line("artists", made));
            recon.ReconcileRoot(t1, null);
            var kRemote = Child(scene, scene.Root, 0);
            var wTitle = Child(scene, scene.Root, 1);
            var wArtists = Child(scene, scene.Root, 2);

            var t2 = Row(Placeholder(), Line("title", made), Line("artists", made));   // keyed → unkeyed at slot 0
            recon.ReconcileRoot(t2, t1);
            var placeholder = Child(scene, scene.Root, 0);
            bool toUnkeyed = made.Count == 2 && !scene.IsLive(kRemote) && placeholder != wTitle && placeholder != wArtists
                             && Child(scene, scene.Root, 1) == wTitle && Child(scene, scene.Root, 2) == wArtists
                             && InSlot(scene, made, "title", wTitle) && InSlot(scene, made, "artists", wArtists);

            var t3 = Row(Remote(), Line("title", made), Line("artists", made));        // unkeyed → keyed at slot 0
            recon.ReconcileRoot(t3, t2);
            bool toKeyed = made.Count == 2 && !scene.IsLive(placeholder)
                           && Child(scene, scene.Root, 1) == wTitle && Child(scene, scene.Root, 2) == wArtists
                           && InSlot(scene, made, "title", wTitle) && InSlot(scene, made, "artists", wArtists);
            Check("gate.reconcile.unkeyed-ordinal.slot-flip a keyed⇄unkeyed flip at one slot does not shift the unkeyed siblings behind it ([remote, title, artists] ⇄ [placeholder, title, artists])",
                toUnkeyed && toKeyed, $"toUnkeyed={toUnkeyed} toKeyed={toKeyed} constructions={made.Count} (want 2)");
        }

        // (a3) keyed children MOVING across an unchanged unkeyed run: [K1, U_a, U_b, K2] → [K2, K3, U_a', U_b', K1].
        //      Same-index pairing would hand U_a' the U_b instance and remount U_b'; ordinal keeps both.
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            var made = new List<OrdinalLineProbe>();
            var t1 = Row(Keyed("k1"), Line("a", made), Line("b", made), Keyed("k2"));
            recon.ReconcileRoot(t1, null);
            var k1 = Child(scene, scene.Root, 0);
            var wA = Child(scene, scene.Root, 1);
            var wB = Child(scene, scene.Root, 2);
            var k2 = Child(scene, scene.Root, 3);
            var t2 = Row(Keyed("k2"), Keyed("k3"), Line("a", made), Line("b", made), Keyed("k1"));
            recon.ReconcileRoot(t2, t1);
            bool kept = made.Count == 2
                        && Child(scene, scene.Root, 0) == k2 && Child(scene, scene.Root, 2) == wA
                        && Child(scene, scene.Root, 3) == wB && Child(scene, scene.Root, 4) == k1
                        && InSlot(scene, made, "a", wA) && InSlot(scene, made, "b", wB);
            Check("gate.reconcile.unkeyed-ordinal.keyed-move-across keyed children moving/inserting around an unchanged unkeyed run keep each unkeyed child's node and instance",
                kept, $"constructions={made.Count} (want 2) aKept={Child(scene, scene.Root, 2) == wA} bKept={Child(scene, scene.Root, 3) == wB}");
        }

        // (c) a PURE-unkeyed list keeps today's positional behaviour exactly: tail append, tail remove, and — the
        //     documented unkeyed semantic, key it if identity matters — a front insert still pairs by position.
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            var made = new List<OrdinalLineProbe>();
            var t1 = Row(Line("a", made), Line("b", made));
            recon.ReconcileRoot(t1, null);
            var w0 = Child(scene, scene.Root, 0);
            var w1 = Child(scene, scene.Root, 1);

            var t2 = Row(Line("a", made), Line("b", made), Line("c", made));
            recon.ReconcileRoot(t2, t1);
            var w2 = Child(scene, scene.Root, 2);
            bool append = made.Count == 3 && Child(scene, scene.Root, 0) == w0 && Child(scene, scene.Root, 1) == w1
                          && !w2.IsNull && w2 != w0 && w2 != w1
                          && InSlot(scene, made, "a", w0) && InSlot(scene, made, "b", w1) && InSlot(scene, made, "c", w2);

            var t3 = Row(Line("a", made), Line("b", made));
            recon.ReconcileRoot(t3, t2);
            bool remove = made.Count == 3 && Child(scene, scene.Root, 0) == w0 && Child(scene, scene.Root, 1) == w1
                          && Child(scene, scene.Root, 2).IsNull && !scene.IsLive(w2)
                          && InSlot(scene, made, "a", w0) && InSlot(scene, made, "b", w1);

            // Front insert of an UNKEYED same-type line: positional, exactly as before — slot 0 (now "x") reuses a's node
            // and instance, slot 1 reuses b's, and only the TAIL factory ("b") runs again.
            var t4 = Row(Line("x", made), Line("a", made), Line("b", made));
            recon.ReconcileRoot(t4, t3);
            bool frontPositional = made.Count == 4 && made[3].Label == "b"
                                   && Child(scene, scene.Root, 0) == w0 && Child(scene, scene.Root, 1) == w1
                                   && Made(made, "a") is { } ia && ia.Context.AnchorNode == Child(scene, w0, 0)
                                   && Made(made, "x") is null;
            Check("gate.reconcile.unkeyed-ordinal.pure-unkeyed-positional an unkeyed-only list keeps positional identity (tail append/remove preserve the prefix; a front insert still pairs by position)",
                append && remove && frontPositional,
                $"append={append} remove={remove} frontPositional={frontPositional} constructions={made.Count} (want 4)");
        }

        // (c2) a TYPE MISMATCH is no reuse and never slides to a later same-type sibling.
        //      Ordinal mode (unkeyed count unchanged): [K1, T_u, L_a] → [L_x, L_a', K1] — ordinal 0 is Text vs Box, so
        //      x mounts fresh and the text goes; ordinal 1 still pairs (L_a' keeps a's node + instance).
        //      Positional mode (a count change): [T_u, L_a] → [L_a'] — slot 0 is Text vs Box, so a' mounts fresh and both
        //      old children go (exactly the former rule's outcome).
        {
            bool ordinalOk;
            {
                var scene = new SceneStore();
                var recon = new TreeReconciler(scene, strings);
                var made = new List<OrdinalLineProbe>();
                var t1 = Row(Keyed("k1"), new TextEl("caption"), Line("a", made));
                recon.ReconcileRoot(t1, null);
                var k1 = Child(scene, scene.Root, 0);
                var text = Child(scene, scene.Root, 1);
                var wA = Child(scene, scene.Root, 2);
                var t2 = Row(Line("x", made), Line("a", made), Keyed("k1"));
                recon.ReconcileRoot(t2, t1);
                var wX = Child(scene, scene.Root, 0);
                ordinalOk = made.Count == 2 && made[1].Label == "x" && !scene.IsLive(text)
                            && wX != wA && InSlot(scene, made, "x", wX)
                            && Child(scene, scene.Root, 1) == wA && InSlot(scene, made, "a", wA)
                            && Child(scene, scene.Root, 2) == k1;
            }
            bool positionalOk;
            {
                var scene = new SceneStore();
                var recon = new TreeReconciler(scene, strings);
                var made = new List<OrdinalLineProbe>();
                var t1 = Row(new TextEl("caption"), Line("a", made));
                recon.ReconcileRoot(t1, null);
                var text = Child(scene, scene.Root, 0);
                var wA = Child(scene, scene.Root, 1);
                var t2 = Row(Line("a", made));
                recon.ReconcileRoot(t2, t1);
                var wA2 = Child(scene, scene.Root, 0);
                positionalOk = made.Count == 2 && !scene.IsLive(text) && !scene.IsLive(wA) && !wA2.IsNull
                               && Child(scene, scene.Root, 1).IsNull && made[1].Label == "a"
                               && made[1].Context.AnchorNode == Child(scene, wA2, 0);
            }
            Check("gate.reconcile.unkeyed-ordinal.type-mismatch-no-reuse a type mismatch at an unkeyed ordinal/slot mounts the new child and removes the old one, and never slides to a later same-type sibling",
                ordinalOk && positionalOk, $"ordinalMode={ordinalOk} positionalMode={positionalOk}");
        }

        // (d) reorder detection still reports: a PURE reorder (no mount, no remove, same count) marks the container
        //     LayoutDirty only through `moved` — for a keyed reorder AND for a mixed keyed/unkeyed swap whose unkeyed
        //     child now pairs by ordinal. Negative control: the same order re-rendered does NOT mark it.
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            var ka = new BoxEl { Key = "a", Width = 10, Height = 10 };
            var kb = new BoxEl { Key = "b", Width = 10, Height = 10 };
            var kc = new BoxEl { Key = "c", Width = 10, Height = 10 };
            var t1 = Row(ka, kb, kc);
            recon.ReconcileRoot(t1, null);
            var hA = Child(scene, scene.Root, 0);
            var hB = Child(scene, scene.Root, 1);
            var hC = Child(scene, scene.Root, 2);
            bool Dirty() => (scene.Flags(scene.Root) & NodeFlags.LayoutDirty) != 0;

            scene.Unmark(scene.Root, NodeFlags.LayoutDirty);
            var t2 = t1 with { Children = [ka, kb, kc] };             // same order, same element objects
            recon.ReconcileRoot(t2, t1);
            bool quietSameOrder = !Dirty();

            scene.Unmark(scene.Root, NodeFlags.LayoutDirty);
            var t3 = t1 with { Children = [kc, ka, kb] };             // pure keyed reorder
            recon.ReconcileRoot(t3, t2);
            bool keyedMoved = Dirty() && Child(scene, scene.Root, 0) == hC && Child(scene, scene.Root, 1) == hA
                              && Child(scene, scene.Root, 2) == hB;

            var mixedScene = new SceneStore();
            var mixed = new TreeReconciler(mixedScene, strings);
            var u = new BoxEl { Width = 5, Height = 5 };
            var k = new BoxEl { Key = "k", Width = 6, Height = 6 };
            var m1 = Row(u, k);
            mixed.ReconcileRoot(m1, null);
            var hU = Child(mixedScene, mixedScene.Root, 0);
            var hK = Child(mixedScene, mixedScene.Root, 1);
            mixedScene.Unmark(mixedScene.Root, NodeFlags.LayoutDirty);
            var m2 = m1 with { Children = [k, u] };                   // [U, K] → [K, U]: nothing mounts or is removed
            mixed.ReconcileRoot(m2, m1);
            bool mixedMoved = (mixedScene.Flags(mixedScene.Root) & NodeFlags.LayoutDirty) != 0
                              && Child(mixedScene, mixedScene.Root, 0) == hK && Child(mixedScene, mixedScene.Root, 1) == hU;

            Check("gate.reconcile.moved-flag a pure reorder still marks the container LayoutDirty via the match-order inversion (keyed reorder and a mixed [U,K]→[K,U] swap), and an unchanged order does not",
                quietSameOrder && keyedMoved && mixedMoved,
                $"quietSameOrder={quietSameOrder} keyedMoved={keyedMoved} mixedMoved={mixedMoved}");
        }

        // (e) the >128-children path (TreeReconciler.ReconcilePlannedChildren → ChildReconcilePlan) applies the SAME rule:
        //     scenario (a) padded past StackScratchMax with keyed siblings behind the two unkeyed lines (ordinal mode) …
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            var made = new List<OrdinalLineProbe>();
            var pad = Pad(130);
            var t1 = new BoxEl { Direction = 1, Children = [Line("title", made), Line("artists", made), .. pad] };
            recon.ReconcileRoot(t1, null);
            var wTitle = Child(scene, scene.Root, 0);
            var wArtists = Child(scene, scene.Root, 1);
            var pad0 = Child(scene, scene.Root, 2);
            var t2 = new BoxEl { Direction = 1, Children = [Remote(), Line("title", made), Line("artists", made), .. pad] };
            recon.ReconcileRoot(t2, t1);
            bool planned = made.Count == 2
                           && Child(scene, scene.Root, 1) == wTitle && Child(scene, scene.Root, 2) == wArtists
                           && Child(scene, scene.Root, 3) == pad0
                           && InSlot(scene, made, "title", wTitle) && InSlot(scene, made, "artists", wArtists);
            Check("gate.reconcile.unkeyed-ordinal.planned-path the >128-child planned reconcile pairs unkeyed children by ordinal exactly like the stack path",
                planned, $"constructions={made.Count} (want 2) titleKept={Child(scene, scene.Root, 1) == wTitle} artistsKept={Child(scene, scene.Root, 2) == wArtists}");
        }

        // (e2) … and scenario (a2) padded the same way (positional mode on a keyed⇄unkeyed slot flip).
        {
            var scene = new SceneStore();
            var recon = new TreeReconciler(scene, strings);
            var made = new List<OrdinalLineProbe>();
            var pad = Pad(130);
            var t1 = new BoxEl { Direction = 1, Children = [Remote(), Line("title", made), Line("artists", made), .. pad] };
            recon.ReconcileRoot(t1, null);
            var wTitle = Child(scene, scene.Root, 1);
            var wArtists = Child(scene, scene.Root, 2);
            var t2 = new BoxEl { Direction = 1, Children = [Placeholder(), Line("title", made), Line("artists", made), .. pad] };
            recon.ReconcileRoot(t2, t1);
            bool planned = made.Count == 2
                           && Child(scene, scene.Root, 1) == wTitle && Child(scene, scene.Root, 2) == wArtists
                           && InSlot(scene, made, "title", wTitle) && InSlot(scene, made, "artists", wArtists);
            Check("gate.reconcile.unkeyed-ordinal.planned-slot-flip the >128-child planned reconcile keeps same-index pairing across a keyed⇄unkeyed slot flip exactly like the stack path",
                planned, $"constructions={made.Count} (want 2)");
        }

        // (f) the plan's ordinal cursor is plan STATE: a budgeted Step that yields between the two unkeyed matches must
        //     resume the walk where it stopped (one child per Step here, so every match lands in its own Step).
        {
            var scene = new SceneStore();
            var parent = scene.CreateNode(1);
            scene.Root = parent;
            const int padN = 140;
            var old = new Element[padN + 2];
            old[0] = new BoxEl { Width = 1, Height = 1 };
            old[1] = new BoxEl { Width = 2, Height = 1 };
            for (int i = 0; i < padN; i++) old[i + 2] = new BoxEl { Key = "p" + i, Width = 1, Height = 1 };
            foreach (var e in old) scene.AppendChild(parent, scene.CreateNode(e.ElementTypeId));
            var desired = new Element[padN + 3];
            desired[0] = new BoxEl { Key = "inserted", Width = 1, Height = 1 };
            desired[1] = new BoxEl { Width = 1, Height = 1 };
            desired[2] = new BoxEl { Width = 2, Height = 1 };
            for (int i = 0; i < padN; i++) desired[i + 3] = old[i + 2];
            long clock = 0;
            var plan = new ChildReconcilePlan(() => clock++);
            plan.Begin(scene, parent, desired, old, 0);
            int yields = 0;
            while (!plan.Step(clock + 1)) yields++;
            var m = plan.Matches;
            bool padOk = true;
            for (int i = 0; i < padN; i++) padOk &= m[i + 3] == i + 2;
            bool ok = yields > padN && m[0] == -1 && m[1] == 0 && m[2] == 1 && padOk;
            Check("gate.child-plan-unkeyed-ordinal-yield a yielded plan resumes the unkeyed ordinal cursor across Steps (inserted keyed child → -1, the two unkeyed children → old 0 and 1)",
                ok, $"yields={yields} m0={m[0]} m1={m[1]} (want 0) m2={m[2]} (want 1) padOk={padOk}");
            plan.Reset();
        }
    }

    static Element[] Pad(int n)
    {
        var pad = new Element[n];
        for (int i = 0; i < n; i++) pad[i] = new BoxEl { Key = "pad-" + i, Width = 1, Height = 1 };
        return pad;
    }
}

/// <summary>Propless probe for <see cref="UnkeyedOrdinalChecks"/>: <see cref="Label"/> is the mount-frozen caller data
/// (set once by the factory). No hooks, no signals — the checks read identity, not output.</summary>
sealed class OrdinalLineProbe : Component
{
    public string Label = "";
    public override Element Render() => new BoxEl { Width = 10, Height = 10 };
}
