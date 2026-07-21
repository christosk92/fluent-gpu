using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  In-app Toast host (WS3 P6). A top-Z lane auto-mounted by every OverlayHost + registered as the process-default
//  service for the static Toast API. Toasts are InfoBar-shaped cards over Elevation.Flyout, stacked newest-nearest-edge
//  with an 8px gap, auto-dismissing on the host frame-clock HostTimerQueue (0 = sticky), PAUSED while the pointer hovers
//  the strip. Enter/Exit ride the declarative Standard motion tokens (translate-from-edge + fade).
//
//  NAMING: this Controls.Toast is the IN-APP toast (a card in the app window). It is DISTINCT from
//  FluentGpu.WindowsApi Toast, which raises an OS notification (Action Center). Different namespaces, different
//  surfaces — use this for transient in-app status, the WindowsApi one for OS-level notifications.
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Where the toast strip docks (screen corner / edge). Default <see cref="BottomRight"/>.</summary>
public enum ToastPlacement : byte { BottomRight, BottomLeft, BottomCenter, TopRight, TopLeft, TopCenter }

/// <summary>Per-toast options. <see cref="Severity"/> reuses <see cref="InfoBarSeverity"/> (the shared
/// <c>SeverityVisuals</c> mapping, so toast/InfoBar can't drift). <see cref="DurationMs"/> = 0 ⇒ sticky (no
/// auto-dismiss). <see cref="CustomContent"/> replaces the standard title/message card entirely.</summary>
public sealed record ToastOptions
{
    public InfoBarSeverity Severity { get; init; } = InfoBarSeverity.Informational;
    public string? Title { get; init; }
    /// <summary>Auto-dismiss delay (ms). Default 5000. <c>0</c> = sticky (dismissed only by the user / <c>Close()</c>).</summary>
    public float DurationMs { get; init; } = 5000f;
    public string? ActionLabel { get; init; }
    public Action? OnAction { get; init; }
    public bool Closable { get; init; } = true;
    /// <summary>Custom body — when set, replaces the standard severity/title/message card (rendered inside the shadowed,
    /// tinted toast frame).</summary>
    public Func<Element>? CustomContent { get; init; }
}

/// <summary>Options for an explicit <see cref="ToastHost.Create"/> (multi-window / non-default placement). The
/// auto-mounted host (inside every <see cref="OverlayHost"/>) reads the static <see cref="Toast.Placement"/>/
/// <see cref="Toast.MaxVisible"/> instead.</summary>
public sealed record ToastHostOptions
{
    public ToastPlacement Placement { get; init; } = ToastPlacement.BottomRight;
    public int MaxVisible { get; init; } = 3;
    /// <summary>Extra DIP reserved on the DOCKED edge, beyond the default 24px screen-edge dock — so the strip
    /// clears a persistent chrome bar (e.g. a now-playing / player bar or a safe-area inset). Applied to the bottom
    /// edge for <c>Bottom*</c> placements and the top edge for <c>Top*</c> placements. Default 0.</summary>
    public float EdgeInset { get; init; }
}

/// <summary>A live toast; <see cref="Close"/> dismisses it (idempotent).</summary>
public sealed class ToastHandle
{
    internal ToastController? Controller;
    internal ToastItem? Item;
    public bool IsOpen { get; internal set; }
    public void Close() { if (IsOpen && Controller is { } c && Item is { } it) c.Close(it); }
}

/// <summary>The static in-app toast API. <see cref="Show"/> enqueues a toast on the process-default host (the lane
/// auto-mounted by <see cref="OverlayHost"/>). See the naming note on <see cref="ToastPlacement"/>: distinct from the
/// OS-notification <c>FluentGpu.WindowsApi.Notifications.Toast</c>.</summary>
public static class Toast
{
    /// <summary>Max simultaneously VISIBLE toasts; the rest wait in a FIFO overflow queue. Default 3.</summary>
    public static int MaxVisible = 3;
    /// <summary>Default placement for the auto-mounted host. Default <see cref="ToastPlacement.BottomRight"/>.</summary>
    public static ToastPlacement Placement = ToastPlacement.BottomRight;
    /// <summary>Extra DIP reserved on the docked edge for the auto-mounted lane (see
    /// <see cref="ToastHostOptions.EdgeInset"/>) — e.g. set to a player-bar height so toasts float above it. Default 0.</summary>
    public static float EdgeInset = 0f;

    // The process-default controller (the auto-mounted OverlayHost lane; save/restore stack for explicit overrides).
    internal static ToastController? Default;

    /// <summary>Show a toast. Returns a live <see cref="ToastHandle"/> ({IsOpen, Close}). When no host is mounted yet the
    /// returned handle is inert (IsOpen == false).</summary>
    public static ToastHandle Show(string message, ToastOptions? options = null)
        => Default?.Show(message, options) ?? new ToastHandle();

    /// <summary>Dismiss every visible + queued toast on the default host.</summary>
    public static void CloseAll() => Default?.CloseAll();
}

/// <summary>One enqueued toast (controller-internal). Carries its auto-dismiss timer bookkeeping (generation-guarded,
/// mount-allocated <see cref="Fire"/> delegate — zero per-frame alloc).</summary>
internal sealed class ToastItem
{
    public int Id;
    public string Message = "";
    public ToastOptions Options = new();
    public ToastHandle Handle = null!;
    // Auto-dismiss timer state (owned by the controller, scheduled on the HostTimerQueue).
    public long Gen;
    public bool Armed;
    public double DueAt;
    public float Remaining;       // ms left when NOT armed (paused / not-yet-visible)
    public Action<long> Fire = null!;
}

/// <summary>Owns the toast queue + the auto-dismiss timers on the host frame-clock queue. FIFO: the first
/// <see cref="MaxVisible"/> items are visible; the rest wait. Hover over the strip pauses the visible countdowns.</summary>
internal sealed class ToastController
{
    private readonly Signal<int> _version = new(0);   // bump → the lane re-renders (show/close)
    private readonly List<ToastItem> _items = new(4);
    private HostTimerQueue? _queue;
    private bool _paused;
    private int _nextId;

    public ToastPlacement Placement = ToastPlacement.BottomRight;
    public int MaxVisible = 3;
    public float EdgeInset;   // extra DIP reserved on the docked edge (see ToastHostOptions.EdgeInset)
    public IReadSignal<int> Version => _version;
    public IReadOnlyList<ToastItem> Items => _items;
    public bool Paused => _paused;

    private void Bump() => _version.Value = _version.Peek() + 1;

    public void SetQueue(HostTimerQueue? q)
    {
        if (ReferenceEquals(_queue, q)) return;
        _queue = q;
        ReconcileTimers();   // a queue that arrived after a Show arms the pending visible countdowns
    }

    public ToastHandle Show(string message, ToastOptions? options)
    {
        var opts = options ?? new ToastOptions();
        var item = new ToastItem
        {
            Id = _nextId++,
            Message = message,
            Options = opts,
            Handle = new ToastHandle { IsOpen = true },
            Remaining = MathF.Max(0f, opts.DurationMs),
        };
        item.Handle.Controller = this;
        item.Handle.Item = item;
        item.Fire = g => { if (g == item.Gen && item.Armed) { item.Armed = false; Close(item); } };
        _items.Add(item);
        Bump();
        ReconcileTimers();
        return item.Handle;
    }

    public void Close(ToastItem item)
    {
        int idx = _items.IndexOf(item);
        if (idx < 0) return;
        item.Handle.IsOpen = false;
        item.Gen++;                 // invalidate any pending fire
        item.Armed = false;
        _items.RemoveAt(idx);
        Bump();
        ReconcileTimers();          // a queued toast may now be visible → start its countdown
    }

    public void CloseAll()
    {
        if (_items.Count == 0) return;
        foreach (var it in _items) { it.Handle.IsOpen = false; it.Gen++; it.Armed = false; }
        _items.Clear();
        Bump();
    }

    public void SetPaused(bool paused)
    {
        if (_paused == paused) return;
        _paused = paused;
        ReconcileTimers();
    }

    // Arm the auto-dismiss for every VISIBLE, non-sticky, not-paused item; cancel (banking the remaining time) for
    // items that shouldn't currently be counting (paused / off-window / sticky).
    private void ReconcileTimers()
    {
        int max = MaxVisible < 1 ? 1 : MaxVisible;
        for (int i = 0; i < _items.Count; i++)
        {
            var it = _items[i];
            bool shouldRun = i < max && it.Options.DurationMs > 0f && !_paused && _queue is not null;
            if (shouldRun && !it.Armed) Arm(it);
            else if (!shouldRun && it.Armed) CancelBanking(it);
        }
    }

    private void Arm(ToastItem it)
    {
        if (_queue is null) return;
        it.Gen++;
        it.Armed = true;
        it.DueAt = _queue.NowMs + MathF.Max(it.Remaining, 0f);
        _queue.Schedule(it.DueAt, it.Gen, it.Fire);
    }

    // Cancel a pending fire and BANK how much time is left, so a resume re-arms for exactly the remainder (hover-pause).
    private void CancelBanking(ToastItem it)
    {
        if (_queue is not null) it.Remaining = MathF.Max(0f, (float)(it.DueAt - _queue.NowMs));
        it.Gen++;
        it.Armed = false;
    }

    // ── Rendering ─────────────────────────────────────────────────────────────────────────────────────────────────
    public Element BuildLane()
    {
        int count = _items.Count;
        int max = MaxVisible < 1 ? 1 : MaxVisible;
        int visible = count < max ? count : max;
        if (visible == 0)
            // Dormant: a full-bleed, hit-test-transparent nothing (no strip, no hover handlers, no timers).
            return new BoxEl { Grow = 1, HitTestVisible = false, HitTestPassThrough = true };

        bool bottom = Placement is ToastPlacement.BottomRight or ToastPlacement.BottomLeft or ToastPlacement.BottomCenter;
        bool right = Placement is ToastPlacement.BottomRight or ToastPlacement.TopRight;
        bool center = Placement is ToastPlacement.BottomCenter or ToastPlacement.TopCenter;
        var sideAlign = center ? FlexAlign.Center : right ? FlexAlign.End : FlexAlign.Start;

        // Slide-from-edge terminal for Enter/Exit (translate + fade). Corner placements slide horizontally; center
        // placements slide vertically from the docked edge.
        float dx = center ? 0f : right ? 48f : -48f;
        float dy = center ? (bottom ? 48f : -48f) : 0f;

        var cards = new Element[visible];
        for (int i = 0; i < visible; i++)
        {
            // Newest nearest the docked edge: bottom strips list oldest→newest top-to-bottom (newest at the bottom);
            // top strips reverse (newest at the top).
            var it = bottom ? _items[i] : _items[visible - 1 - i];
            cards[i] = Card(it, dx, dy);
        }

        var strip = new BoxEl
        {
            Direction = 1,               // vertical stack
            Gap = 8f,                    // 8px inter-toast gap
            AlignSelf = sideAlign,
            AlignItems = sideAlign,
            // Hover over the strip pauses the remaining auto-dismiss time; leaving resumes.
            OnHoverMove = _ => SetPaused(true),
            OnPointerExit = () => SetPaused(false),
            Children = cards,
        };

        return new BoxEl
        {
            Grow = 1,
            Direction = 1,
            HitTestPassThrough = true,   // yields self (children — the strip — keep hit-testing)
            Justify = bottom ? FlexJustify.End : FlexJustify.Start,
            AlignItems = sideAlign,
            // Dock 24px in from the screen edges; EdgeInset reserves extra DIP on the docked edge (bottom for
            // Bottom* placements, top for Top*) so the strip clears a player/chrome bar.
            Padding = new Edges4(24f, 24f + (bottom ? 0f : EdgeInset), 24f, 24f + (bottom ? EdgeInset : 0f)),
            Children = [strip],
        };
    }

    // One toast card: an InfoBar-shaped body (shared SeverityVisuals) inside a shadowed frame, with declarative
    // Enter/Exit motion (Standard tokens). Keyed by id so a removal orphans exactly this card and plays its exit.
    private Element Card(ToastItem it, float dx, float dy)
    {
        var o = it.Options;
        Element? action = o.ActionLabel is { Length: > 0 } lbl
            ? Button.Standard(lbl, () => { o.OnAction?.Invoke(); it.Handle.Close(); })
            : null;

        Element body = o.CustomContent is { } custom
            ? new BoxEl
            {
                Direction = 1,
                MinHeight = 48f,
                Padding = Edges4.All(16f),
                Corners = Radii.ControlAll,
                Fill = SeverityVisuals.For(o.Severity).Background,
                BorderWidth = 1f,
                BorderColor = Tok.StrokeCardDefault,
                Children = [custom()],
            }
            : InfoBar.Create(
                o.Severity,
                o.Title ?? "",
                it.Message,
                onClose: o.Closable ? () => it.Handle.Close() : null,
                isClosable: o.Closable,
                actionButton: action);

        return new BoxEl
        {
            Key = "toast:" + it.Id,
            AlignSelf = FlexAlign.Stretch,
            MinWidth = 300f,
            MaxWidth = 380f,
            Corners = Radii.ControlAll,
            Shadow = Elevation.Flyout,          // toast card elevation
            Enter = new EnterExit(Dx: dx, Dy: dy, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dx: dx, Dy: dy, Opacity: 0f, Active: true),
            Transition = MotionTok.StandardEnter,
            Children = [body],
        };
    }
}

/// <summary>The auto-mounted (or explicit) toast lane. Every <see cref="OverlayHost"/> mounts one at the top of its
/// Z-stack and registers its controller as the process-default for the static <see cref="Toast"/> API. Mount an
/// explicit one via <see cref="Create"/> to override the placement / multi-window (last-registered wins; the previous
/// default is restored when the explicit host unmounts).</summary>
public sealed class ToastHost : Component
{
    /// <summary>Explicit options; <c>null</c> (the OverlayHost auto-mount) reads the static <see cref="Toast"/> config.</summary>
    public ToastHostOptions? Options;

    /// <summary>Mount an explicit toast host (multi-window / non-default placement). Most apps rely on the one
    /// auto-mounted by <see cref="OverlayHost"/> and never call this.</summary>
    public static Element Create(ToastHostOptions? options = null)
        => Embed.Comp(() => new ToastHost { Options = options ?? new ToastHostOptions() });

    public override Element Render()
    {
        var ctlRef = UseRef<ToastController?>(null);
        ctlRef.Value ??= new ToastController();
        var ctl = ctlRef.Value;
        ctl.Placement = Options?.Placement ?? Toast.Placement;
        ctl.MaxVisible = Options?.MaxVisible ?? Toast.MaxVisible;
        ctl.EdgeInset = Options?.EdgeInset ?? Toast.EdgeInset;
        ctl.SetQueue(UseContext(HostTimers.Current));

        // Register as the process-default for the static Toast API — an idempotent render-body write (NOT a mount
        // UseEffect): the auto-mounted lane lives for the app's lifetime, and last-registered-wins is the accepted
        // process-static contract (the ContextMenu._currentHandle precedent). Doing it in the body keeps the mount
        // allocation-free (no effect closure / pending-effect enqueue).
        if (!ReferenceEquals(Toast.Default, ctl)) Toast.Default = ctl;

        _ = ctl.Version.Value;   // subscribe → re-render the lane on show/close
        return ctl.BuildLane();
    }
}
