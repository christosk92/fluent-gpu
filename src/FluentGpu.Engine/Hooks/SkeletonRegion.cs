using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Hooks;

/// <summary>How the real content reveals when a <see cref="SkelRegionEl"/> swaps shimmer→real.</summary>
public enum SkelReveal : byte
{
    /// <summary>The whole content blur-rises in as one (Opacity + TranslateY + Blur → rest). The default scalar reveal.</summary>
    Soft,
    /// <summary>Each row blur-rises 40ms behind the last (the track-list reveal — walks the mounted children).</summary>
    StaggerRows,
    /// <summary>A plain opacity cross-fade (no translate/blur) — for content that should not move.</summary>
    FadeOnly,
    /// <summary>No root reveal — the content animates its OWN entrance (e.g. an ItemsView's per-row
    /// <c>ItemCollectionTransition</c> adds). Pair with a longer <see cref="SkeletonStyle.ExitMs"/> so the shimmer
    /// lingers and cross-dissolves with the content's own fade-in instead of leaving a gap.</summary>
    None,
}

/// <summary>The look of the derived shimmer (bar color + corner, the breathing pulse, and the inter-row gap the list
/// shimmer stacks at). Defaults to the WinUI-flavoured subtle fill + a 1s breathe (matches the gallery skeleton bars).</summary>
public readonly record struct SkeletonStyle(
    ColorF BarColor, float PulseMs = 1000f, float PulseMin = 0.5f, float RowGap = 8f, float BarRadius = 4f, float TextRatio = 0.72f,
    float ExitMs = Expressive.Fast)
{
    public static SkeletonStyle Default => new(Tok.FillSubtleSecondary);
}

/// <summary>
/// The native skeleton-loading boundary (the ONE author surface). A layout-transparent container, modelled on the
/// reactive <c>Show</c> boundary: a reconciler effect reads the <see cref="Loadable{T}"/> state via <see cref="Pending"/>
/// / <see cref="Failed"/> and mounts one of three branches — a DERIVED shimmer (from <see cref="ShimmerSource"/>, or — when
/// it is null — from <see cref="Content"/> itself rendered against the seed; never a hand-authored second tree), the real
/// <see cref="Content"/>, or the <see cref="OnFailed"/> error UI — swapping with the
/// blur reveal on Ready. Build it with <see cref="Skel"/>. The shimmer is derived once per pending→loaded edge (a
/// reconcile-edge event, not a paint phase), so it adds zero per-frame cost.
/// </summary>
public sealed record SkelRegionEl(
    Func<bool> Pending,
    Func<bool> Failed,
    Func<Element> Content,
    Func<Element>? ShimmerSource,   // null ⇒ the reconciler derives Content itself (= content(seed) while Pending); set only when the shimmer is a DIFFERENT tree
    Func<Element>? OnFailed,
    SkelReveal Reveal,
    SkeletonStyle Style,
    object? Group,
    bool SmoothResize = true) : Element
{
    public override ushort ElementTypeId => 13;
}

/// <summary>The public author surface for native skeleton-loading: <c>Skel.Region(loadable, …)</c> derives the shimmer
/// from your ONE real UI and swaps to it on load with the blur reveal; <c>el.Pending(field)</c> shimmers a single leaf
/// in place inside an already-real row (incremental field arrival).</summary>
public static class Skel
{
    /// <summary>A list/region skeleton: while <paramref name="loadable"/> is Pending, derive a shimmer from
    /// <paramref name="count"/> copies of <paramref name="rowTemplate"/>(null) (the SAME row shape as the real row —
    /// single source); on Ready mount <paramref name="content"/>(value) — or <paramref name="onEmpty"/> when the loaded
    /// array is empty — and blur-reveal it (<paramref name="reveal"/>); on Failed mount <paramref name="onFailed"/>.
    /// The engine owns all four states (Pending / Ready / Empty / Failed) so callers never branch on them by hand.
    /// <paramref name="group"/> coordinates the reveal with sibling regions sharing the token (one settle window). Reads
    /// State and Value so both branch flips and Ready-to-Ready value refreshes reconcile.</summary>
    public static SkelRegionEl Region<T>(
        Loadable<T[]> loadable, Func<T?, Element> rowTemplate, int count, Func<T[], Element> content,
        SkelReveal reveal = SkelReveal.StaggerRows, Func<Element>? onFailed = null, Func<Element>? onEmpty = null,
        object? group = null, SkeletonStyle? style = null, bool smoothResize = true)
    {
        var st = style ?? SkeletonStyle.Default;
        return new SkelRegionEl(
            Pending: () => loadable.State.Value == (byte)LoadState.Pending,
            Failed: () => loadable.State.Value == (byte)LoadState.Failed,
            Content: () => { var v = loadable.Value.Value; return onEmpty is not null && (v is null || v.Length == 0) ? onEmpty() : content(v); },
            ShimmerSource: () => RowStack(count, rowTemplate, st.RowGap),
            OnFailed: onFailed,
            Reveal: reveal, Style: st, Group: group, SmoothResize: smoothResize);
    }

    /// <summary>A single-subtree skeleton: while Pending derive the shimmer from <paramref name="shimmerSource"/> (your
    /// one real subtree, built with placeholder/empty fields — never a hand-authored second tree), on Ready mount
    /// <paramref name="content"/>(value) — or <paramref name="onEmpty"/> when <paramref name="isEmpty"/>(value) is true,
    /// so the engine owns the Empty branch too — on Failed mount <paramref name="onFailed"/>.</summary>
    public static SkelRegionEl Region<T>(
        Loadable<T> loadable, Func<Element> shimmerSource, Func<T, Element> content,
        SkelReveal reveal = SkelReveal.Soft, Func<Element>? onFailed = null,
        Func<T, bool>? isEmpty = null, Func<Element>? onEmpty = null,
        object? group = null, SkeletonStyle? style = null, bool smoothResize = true)
        => new(
            Pending: () => loadable.State.Value == (byte)LoadState.Pending,
            Failed: () => loadable.State.Value == (byte)LoadState.Failed,
            Content: () => { var v = loadable.Value.Value; return onEmpty is not null && isEmpty is not null && isEmpty(v) ? onEmpty() : content(v); },
            ShimmerSource: shimmerSource,
            OnFailed: onFailed,
            Reveal: reveal, Style: style ?? SkeletonStyle.Default, Group: group, SmoothResize: smoothResize);

    /// <summary>The single-subtree skeleton — pass ONLY your real <paramref name="content"/>, nothing about skeletons:
    /// the engine renders <c>content(seed)</c> (your SAME content rendered against the loadable's own pending seed value —
    /// the placeholder you gave <c>UseResource</c>) and DERIVES the shimmer from it, then fills in the real data on
    /// load. There is no second tree and no shimmer arg. The author's ONLY responsibility is that the resource's seed
    /// renders a representative shape (e.g. an empty artist whose page still lays out hero + tracks via a fake-data
    /// fallback; a home/search seed with a few blank items) — a DATA concern declared at the resource, not skeleton code.
    /// On Ready mount content(value) — or <paramref name="onEmpty"/> when <paramref name="isEmpty"/>(value) — on Failed
    /// <paramref name="onFailed"/>.
    /// (The explicit-<c>shimmerSource</c> overload exists ONLY for the two cases where <c>content(seed)</c> can't BE the
    /// shimmer: a streaming LIST where <c>Flow.For</c> over the seed yields zero rows, and content that must reserve a slot
    /// a derived shimmer can't carry — e.g. a connected-animation morph cover — or is a stateful component you must not
    /// mount during load. Everything else uses THIS overload.)</summary>
    public static SkelRegionEl Region<T>(
        Loadable<T> loadable, Func<T, Element> content,
        SkelReveal reveal = SkelReveal.Soft, Func<Element>? onFailed = null,
        Func<T, bool>? isEmpty = null, Func<Element>? onEmpty = null,
        object? group = null, SkeletonStyle? style = null, bool smoothResize = true)
        => new(
            Pending: () => loadable.State.Value == (byte)LoadState.Pending,
            Failed: () => loadable.State.Value == (byte)LoadState.Failed,
            Content: () => { var v = loadable.Value.Value; return onEmpty is not null && isEmpty is not null && isEmpty(v) ? onEmpty() : content(v); },
            ShimmerSource: null,   // the shimmer IS Content(seed) — the reconciler derives Content itself, no separate field
            OnFailed: onFailed,
            Reveal: reveal, Style: style ?? SkeletonStyle.Default, Group: group, SmoothResize: smoothResize);

    /// <summary>Shimmer a SINGLE leaf in place inside an already-real row (incremental field arrival): wrap
    /// <paramref name="leaf"/> so that while <paramref name="field"/> is Pending it shows a derived shimmer bar of the
    /// leaf's shape, and on Ready the leaf reveals via a blurred cross-fade. The leaf typically binds the field's value
    /// (<c>Text = field.Bind()</c>), so it shows the value the moment it arrives.</summary>
    public static SkelRegionEl Pending<T>(this Element leaf, Loadable<T> field, SkeletonStyle? style = null)
        => new(
            Pending: () => field.State.Value == (byte)LoadState.Pending,
            Failed: () => field.State.Value == (byte)LoadState.Failed,
            Content: () => leaf,
            ShimmerSource: null,   // the shimmer IS the leaf — the reconciler derives Content itself
            OnFailed: null,
            Reveal: SkelReveal.Soft, Style: style ?? SkeletonStyle.Default, Group: null);

    private static Element RowStack<T>(int count, Func<T?, Element> rowTemplate, float gap)
    {
        var rows = new Element[Math.Max(0, count)];
        for (int i = 0; i < rows.Length; i++) rows[i] = rowTemplate(default);
        return new BoxEl { Direction = 1, Gap = gap, Children = rows };
    }
}

/// <summary>Seeds the real-content reveal when a region swaps shimmer→real (delegates to the existing blur recipes).
/// Walks the mounted real subtree for the staggered row case. Called by the reconciler (a reconcile-edge event).</summary>
internal static class SkeletonReveal
{
    public static void Play(AnimEngine anim, SceneStore scene, SkelReveal reveal, NodeHandle realRoot, in SkeletonStyle style)
    {
        if (realRoot.IsNull || !scene.IsLive(realRoot)) return;
        switch (reveal)
        {
            case SkelReveal.None:
                break;   // content owns its entrance (its rows cross-dissolve with the lingering shimmer)
            case SkelReveal.FadeOnly:
                anim.Animate(realRoot, AnimChannel.Opacity, 0f, 1f, Expressive.Fast, Easing.SmoothOut);
                break;
            case SkelReveal.StaggerRows:
            {
                NodeHandle rowParent = UnwrapTransparentBoundaries(scene, realRoot);
                int n = 0;
                for (var c = scene.FirstChild(rowParent); !c.IsNull; c = scene.NextSibling(c)) n++;
                if (n == 0) { anim.SoftReveal(realRoot); break; }
                var rows = new NodeHandle[n];
                int k = 0;
                for (var c = scene.FirstChild(rowParent); !c.IsNull; c = scene.NextSibling(c)) rows[k++] = c;
                anim.SoftRevealStaggered(rows, dy: 8f, blur: Expressive.BlurMedium, durationMs: Expressive.VerySlow, staggerMs: Expressive.Stagger);
                break;
            }
            default:
                anim.SoftReveal(realRoot, dy: 8f, blur: Expressive.BlurMedium);
                break;
        }
    }

    static NodeHandle UnwrapTransparentBoundaries(SceneStore scene, NodeHandle root)
    {
        var n = root;
        for (int depth = 0; depth < 8 && scene.IsLive(n) && IsTransparentBoundary(scene.ElementTypeId(n)); depth++)
        {
            if (scene.ChildCount(n) != 1) break;
            var child = scene.FirstChild(n);
            if (child.IsNull) break;
            n = child;
        }
        return n;
    }

    static bool IsTransparentBoundary(ushort typeId) => typeId is
        3   // ComponentEl
        or 4   // ContextProviderEl
        or 7   // ShowEl
        or 13  // SkelRegionEl
        or 14; // KeepAliveEl
}

/// <summary>Coordinates the reveal of sibling <see cref="SkelRegionEl"/>s sharing a <c>group</c> token: each member
/// registers on mount and reports done (Ready/Failed) with its reveal thunk; when the LAST member of the group is done,
/// all reveals fire together (one settle window) instead of N unsynchronized blur-reveals. Per-thread (the UI thread);
/// no group token ⇒ regions reveal independently (the common path, never touches this).</summary>
internal static class SkelGroupCoordinator
{
    private sealed class Group { public readonly HashSet<int> Members = new(); public readonly Dictionary<int, Action?> Done = new(); }
    [ThreadStatic] private static Dictionary<object, Group>? _groups;

    private static Group Get(object group)
    {
        _groups ??= new();
        if (!_groups.TryGetValue(group, out var g)) { g = new Group(); _groups[group] = g; }
        return g;
    }

    /// <summary>A region in the group has mounted (Pending) — count it as a member whose reveal the round waits for.</summary>
    public static void Register(object group, int regionId) => Get(group).Members.Add(regionId);

    /// <summary>A region left the group (unmounted) — drop it so the round can complete without it.</summary>
    public static void Unregister(object group, int regionId)
    {
        if (_groups is null || !_groups.TryGetValue(group, out var g)) return;
        g.Members.Remove(regionId);
        g.Done.Remove(regionId);
        if (g.Members.Count == 0) _groups.Remove(group);
    }

    /// <summary>Report a member done (Ready ⇒ <paramref name="reveal"/> thunk; Failed ⇒ null). When EVERY registered
    /// member is done, fire all pending reveals together (one settle window) and reset the round (a refresh re-coordinates).</summary>
    public static void Done(object group, int regionId, Action? reveal)
    {
        var g = Get(group);
        g.Members.Add(regionId);
        g.Done[regionId] = reveal;
        if (g.Done.Count >= g.Members.Count)
        {
            foreach (var kv in g.Done) kv.Value?.Invoke();
            g.Done.Clear();
        }
    }
}
