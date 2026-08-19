using System;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>A grid's tail: infinite scroll onto an existing "Show all" pipeline, mirroring
/// <see cref="Wavee.Features.Concerts.ConcertAppendPreloader"/>'s three gates — (C) the tail is NEAR the viewport
/// (fed from the host's own scroll geometry via <see cref="NearTail"/>), (B) after a short arm-debounce so a fast
/// fling through the threshold doesn't fire, and (A) never concurrently (<see cref="Loading"/> is the single-in-
/// flight guard) — with the same bounded-retry collapse.
///
/// <para>No visual shimmer child, unlike its Concert sibling: the grid this trails
/// (<see cref="Wavee.HomeModules.SectionGrid"/>) is a SELF-SCROLLING <c>Virtual.Custom</c> viewport — there is no
/// list of shelf elements to append a shimmer row to the way <c>ConcertHubPage</c> does, and an appended page's
/// cards land inside that SAME virtualized grid the moment <c>LoadMore</c>'s <c>SetReady</c> lands, so a separate
/// loading placeholder would just be a redundant flash. This component's only job is deciding WHEN to call
/// <see cref="Start"/>; the host (<see cref="Wavee.HomeSectionPage"/>, and <c>Wavee.Features.Browse.BrowsePage</c>'s
/// flattened grid) owns the fetch, the cursor, and the exhausted latch exactly as it already does for the masthead's
/// "Show all" button — this is a second, silent trigger for the identical call, gated identically (the host only
/// mounts this when the button itself would be armed).</para></summary>
sealed class HomeSectionAppendPreloader : Component
{
    const int MaxAttempts = 3;
    const int ArmDelayMs = 300;

    /// <summary>The host's append-in-flight signal: true while <see cref="Start"/>'s fetch is outstanding, flipping
    /// back to false on completion or quiet failure — the false edge is what re-arms a retry here. A plain
    /// <see cref="Signal{T}"/> for a host with a dedicated bool (HomeSectionPage's own `loadingMore`), or a small
    /// read-only adapter for a host whose loading state lives inside a larger record (BrowsePage's flattened-shelf
    /// paging overlay) — either way this component only ever reads it.</summary>
    public required IReadSignal<bool> Loading;
    /// <summary>True while the grid's bottom edge is within ~1.5 viewport heights of the content end. Host-owned;
    /// dropped to false after each append so only a fresh scroll event continues the chain.</summary>
    public required IReadSignal<bool> NearTail;
    public required Action Start;

    CancellationTokenSource? _arm;
    int _attempts;

    public override Element Render()
    {
        var post = UsePost();

        UseSignalEffect(() => Reactive.OnCleanup(() => { _arm?.Cancel(); _arm?.Dispose(); }));
        UseSignalEffect(() =>
        {
            bool n = NearTail.Value;
            bool l = Loading.Value;
            if (!n || l || _attempts >= MaxAttempts) { _arm?.Cancel(); return; }
            Arm(post);
        });

        return new BoxEl();   // no visual footprint — see the class doc comment.
    }

    /// <summary>(B) the arm-debounce: cancel-and-restart on every gate re-evaluation, fire only if the gates still
    /// hold after the delay (checked on the UI thread via post).</summary>
    void Arm(Action<Action> post)
    {
        _arm?.Cancel();
        _arm?.Dispose();
        var cts = _arm = new CancellationTokenSource();
        _ = DelayedStart(cts, post);
    }

    async Task DelayedStart(CancellationTokenSource cts, Action<Action> post)
    {
        try { await Task.Delay(ArmDelayMs, cts.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return; }
        post(() =>
        {
            if (cts.IsCancellationRequested || Loading.Peek() || !NearTail.Peek() || _attempts >= MaxAttempts) return;
            _attempts++;
            Start();
        });
    }
}
