using FluentGpu.Foundation;

namespace FluentGpu.Pal;

/// <summary>How an IME composition clause renders (Imm32 ATTR_* / TSF display attributes): Input = dotted underline
/// (not yet converted), Converted = solid underline, TargetConverted/TargetNotConverted = the clause the candidate
/// window currently targets (thick underline).</summary>
public enum ImeClauseKind : byte { Input = 0, Converted = 1, TargetConverted = 2, TargetNotConverted = 3 }

/// <summary>One clause of the in-flight composition string (UTF-16 char range, local to the composition).</summary>
public readonly record struct ImeClause(int Start, int Length, ImeClauseKind Kind);

/// <summary>
/// Receives IME composition events from the platform (the focused editor registers as the sink through the input
/// router). Composition TEXT cannot ride the POD <see cref="InputEventRing"/> (strings) — the platform invokes the
/// sink directly on the UI thread during the message pump; the typing edge is allowed bounded Gen0.
/// </summary>
public interface ITextInputSink
{
    void OnCompositionStart();
    /// <summary>The in-flight (uncommitted) composition changed: full current text + caret position inside it +
    /// the clause segmentation for underline rendering.</summary>
    void OnCompositionUpdate(ReadOnlySpan<char> text, int caret, ReadOnlySpan<ImeClause> clauses);
    /// <summary>The composition committed this final text (insert it; the composition display is torn down).</summary>
    void OnCompositionCommit(ReadOnlySpan<char> text);
    void OnCompositionEnd();
}

/// <summary>
/// The per-window IME/text-services seam (Imm32 today; the interface is event-shaped so a TSF/ITextStoreACP
/// implementation can replace it without touching the engine — full in-place TSF is a named hardening item). It is
/// also the portable SIP (software input panel / touch keyboard) seam: <see cref="TryShowTouchKeyboard"/> /
/// <see cref="TryHideTouchKeyboard"/> request the on-screen keyboard and <see cref="OccludedRectChanged"/> reports the
/// area it covers, so the engine reflows the focused editor's caret above it (WinUI <c>InputPane</c> + the
/// ScrollContentPresenter bring-into-view that <c>InputPaneHandler</c> drives — dxaml\xcp\dxaml\lib\InputPaneHandler.cpp).
/// </summary>
public interface IPlatformTextInput
{
    /// <summary>Register the composition sink (the focused editor, via the input router). Null = none.</summary>
    void SetSink(ITextInputSink? sink);

    /// <summary>Enable/disable the IME for this window (ImmAssociateContext on/off) — flipped when keyboard focus
    /// enters/leaves an editable control, so the IME never composes over a button.</summary>
    void SetEditable(bool editable);

    /// <summary>Position the candidate window: the caret rect in PHYSICAL window pixels (CFS_EXCLUDE placement).</summary>
    void SetCaretRectPx(in RectF rect);

    /// <summary>The OS caret blink half-period in ms (Win32 GetCaretBlinkTime; headless = a fixed test value).</summary>
    int CaretBlinkMs { get; }

    // ── SIP (touch keyboard) trigger seam (input-a11y.md §10; WinUI InputPane2.TryShow/TryHide via IInputPaneInterop) ──

    /// <summary>Request the OS touch keyboard (software input panel). The engine calls this only when focus is gained on
    /// an editable control by a TOUCH pointer (the WinUI policy InputPaneHandler.cpp drives off the focused element's
    /// editability + the touch-input source). Returns true if the platform showed (or already shows) the panel; false on
    /// a desktop with no touch keyboard available — never throws. The default (headless / SIP-less backends) is a no-op
    /// returning false, so the seam is opt-in: a backend overrides it to participate.</summary>
    bool TryShowTouchKeyboard() => false;

    /// <summary>Dismiss the OS touch keyboard. Called when focus leaves the editor for a non-editable target. Returns
    /// true if a panel was hidden, false otherwise (no-op default; never throws).</summary>
    bool TryHideTouchKeyboard() => false;

    /// <summary>The SIP reported a new occluded region (the touch keyboard's <c>InputPane.Showing</c>/<c>Hiding</c>
    /// OccludedRect), in CLIENT DIP — the area the panel now covers, or the default empty rect (<see cref="RectF.IsEmpty"/>)
    /// when it is hidden. The
    /// host subscribes and scrolls the focused editor's caret above <c>rect.Y</c> (the WinUI EnsureFocusedElementInView
    /// reflow). Backends without a SIP never raise it; the default add/remove keeps non-supporting impls test-neutral
    /// (a default-interface-method event so a backend opts in without every implementer declaring the field).</summary>
    event Action<RectF>? OccludedRectChanged { add { } remove { } }
}
