using System.Collections.Generic;
using System.Runtime.InteropServices;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>Process-constant touch-digitizer probe. The per-row swipe belt is <c>touchOnly</c>, so on a machine with no
/// touchscreen every row's <see cref="SwipeControl"/> is inert weight (35+ per list realize — a measured nav/scroll-mount
/// cost). Skipping the wrapper there is look-identical (an untouchable touch-only swipe reveals nothing) and cuts that
/// mount cost. Fails SAFE: any probe failure assumes touch present, so a feature is never removed on uncertainty.</summary>
static partial class TouchInput
{
    private const int SM_MAXIMUMTOUCHES = 95;   // winuser.h: simultaneous touch points; 0 ⇒ no touch digitizer
    private static int _cached = -1;            // -1 = not yet probed
    public static bool Available
    {
        get
        {
            if (_cached < 0)
            {
                int max;
                try { max = GetSystemMetrics(SM_MAXIMUMTOUCHES); }
                catch { max = 1; }   // never DROP swipe on a probe error — assume touch present
                _cached = max > 0 ? 1 : 0;
            }
            return _cached != 0;
        }
    }

    [LibraryImport("user32.dll")]
    private static partial int GetSystemMetrics(int nIndex);
}

/// <summary>
/// The touch swipe-to-action layer for a row (Phase D): the THIRD projection of the shared action system
/// (<see cref="AppAction.ToSwipeAction"/>), wrapping a row element in a WinUI <see cref="SwipeControl"/> so a horizontal
/// finger swipe reveals + commits ONE action per side (Execute mode: drag → plate fills → threshold flips the accent +
/// pops the icon → release invokes, springs closed). Touch-only (<c>touchOnly: true</c>): the mouse never pans a row —
/// the engine's touch arena + this belt keep pointer swipe off. Spotify mapping: <b>leading</b> (swipe RIGHT, revealed
/// on the left) and <b>trailing</b> (swipe LEFT, revealed on the right).
/// </summary>
public static class RowSwipe
{
    // Preserve the row's outer layout contract. A BoxEl margin belongs to the list slot, not inside SwipeControl's
    // overlay; keeping it on the child makes the wrapped body grid wider than its fixed column header.
    static (Element Content, Edges4 Margin) LiftMargin(Element row) => row is BoxEl b
        ? (b with { Margin = default }, b.Margin)
        : (row, default);

    // WinUI SwipeControl delete red (SwipeControl gallery Delete plate) — the destructive plate for remove/delete verbs.
    /// <summary>Wrap <paramref name="row"/> in a touch swipe-to-action control. <paramref name="leading"/> commits on a
    /// swipe RIGHT, <paramref name="trailing"/> on a swipe LEFT; a disabled action (its <c>IsEnabled</c> false for this
    /// target) drops its side. <paramref name="group"/> gives single-open coordination across the list;
    /// <paramref name="resetKey"/> (a bound slot's index signal) snap-closes the row when the slot recycles — pass null
    /// for eager keyed rows (each entry gets a fresh keyed control). Returns the row unchanged when neither side lands.</summary>
    public static Element Wrap(Element row, in ActionContext ctx, SwipeGroup? group = null,
                               AppAction? leading = null, AppAction? trailing = null,
                               IReadSignal<int>? resetKey = null)
    {
        if (!TouchInput.Available) return row;   // touchOnly swipe can't fire without a touch digitizer → skip the per-row wrapper
        var left = Project(leading, ctx);      // leading  → swipe right → left-revealed
        var right = Project(trailing, ctx);    // trailing → swipe left  → right-revealed
        if (left is null && right is null) return row;
        var (content, margin) = LiftMargin(row);
        return SwipeControl.Create(content,
            leftActions: left, rightActions: right,
            // Each side deliberately projects ONE primary action. Execute gives the phone-style contract: a short
            // drag previews the underlay, crossing the native threshold arms it, and release invokes + closes.
            leftMode: SwipeMode.Execute, rightMode: SwipeMode.Execute,
            group: group, resetKey: resetKey, touchOnly: true,
            // Clip the full-bleed action underlay to the row silhouette. The app row remains the foreground and keeps
            // its own zebra/selection/hover fill; the wrapper contributes no replacement content background.
            corners: CornerRadius4.All(6f), margin: margin);
    }

    /// <summary>Recycle-safe bound-row projection. Visual state and invocation resolve the current slot context.</summary>
    public static Element WrapBound(Element row, Func<ActionContext?> context, SwipeGroup? group = null,
                                    AppAction? leading = null, AppAction? trailing = null,
                                    IReadSignal<int>? resetKey = null)
    {
        if (!TouchInput.Available) return row;   // see Wrap: no touch digitizer ⇒ the SwipeControl is inert, skip it
        var left = ProjectBound(leading, context);
        var right = ProjectBound(trailing, context);
        if (left is null && right is null) return row;
        var (content, margin) = LiftMargin(row);
        return SwipeControl.Create(content,
            leftActions: left, rightActions: right,
            leftMode: SwipeMode.Execute, rightMode: SwipeMode.Execute,
            group: group, resetKey: resetKey, touchOnly: true,
            corners: CornerRadius4.All(6f), margin: margin);
    }

    // One side → a single-item Execute list, or null when the action is absent / disabled for this target. Destructive
    // verbs (remove / delete) get the red plate; everything else uses the Execute pre/post-threshold accent flip.
    static IReadOnlyList<SwipeAction>? Project(AppAction? a, in ActionContext ctx)
    {
        if (a is null || !a.EnabledFor(ctx)) return null;
        var s = a.ToSwipeAction(ctx);
        return new[] { Style(a, s) };
    }

    static IReadOnlyList<SwipeAction>? ProjectBound(AppAction? a, Func<ActionContext?> context)
    {
        if (a is null || context() is not { } initial || !a.EnabledFor(initial)) return null;
        IconRef IconNow()
        {
            if (context() is not { } c) return ActionIcons.Resolve(a.IconKey);
            bool on = a.IsChecked?.Invoke(c) ?? false;
            return ActionIcons.Resolve(a.IconKey, on);
        }
        string LabelNow() => context() is { } c ? a.Label(c) : a.Label(initial);
        bool EnabledNow() => context() is { } c && a.EnabledFor(c);
        var s = new SwipeAction(IconNow(), LabelNow())
        {
            IconSource = IconNow,
            LabelSource = LabelNow,
            IsEnabledSource = EnabledNow,
            OnInvoked = () => { if (context() is { } c && a.EnabledFor(c)) a.Execute(c); },
        };
        return new[] { Style(a, s) };
    }

    static SwipeAction Style(AppAction a, SwipeAction s)
    {
        if (a.Destructive)
            // Keep destructive intent visible throughout the drag. Execute's threshold-remount still pops the icon,
            // making the armed edge unmistakable without flashing a delete gesture through the normal accent colour.
            return s with { Color = Tok.SystemFillCriticalBackground, Foreground = Tok.SystemFillCritical };
        // No custom colours: native Execute styling owns the meaningful state transition — neutral tertiary plate
        // before the threshold, AccentDefault + on-accent foreground after it, plus the threshold icon pop.
        return s;
    }
}
