using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>
/// Computed template settings for the <see cref="RatingControl"/> (the typed-record convention -- see
/// <see cref="PersonPicture.PersonPictureTemplateSettings"/> / <see cref="Expander.ExpanderTemplateSettings"/>):
/// the resolved <em>display value</em> (the rating shown right now -- pointer-over preview, else committed value, else
/// placeholder), the foreground/background brushes for the current visual state, and the partial-star clip width baked
/// for the foreground "Set" layer. Mirrors the geometry WinUI's <c>RatingControl::UpdateRatingItemsAppearance</c> derives
/// each pass and binds into its two clipped stack panels. Pure factory: no signals are read here -- the caller passes the
/// already-resolved inputs, so this struct is cheap to build per render and never allocates in a hot bind.
///
/// <para>The star foreground recolor is INSTANT, never eased: WinUI drives every star visual state with
/// <c>VisualStateManager::GoToState(useTransitions:false)</c> (see <c>UpdateRatingItemsAppearance</c>), so the brush
/// steps the instant the pointer-over preview resolves. We honor that by computing the resting <see cref="Foreground"/>
/// directly from the sweep and leaving the glyph's <c>HoverColor</c> at <c>default</c> (A==0 ⇒ the recorder leaves the
/// color untouched and runs no hover ramp) -- a granular re-render swaps the resting color in one step, no cross-fade.</para>
/// </summary>
public readonly record struct RatingControlTemplateSettings(
    float DisplayValue,        // the rating shown right now (committed / preview / placeholder), supports a fractional tail
    ColorF Foreground,         // the filled-star (Set) brush for the current visual state (stepped, never eased)
    ColorF Background)         // the outline-star (unset) brush
{
    /// <param name="value">Committed rating, or <see cref="RatingControl.NoValueSet"/> (-1) when unset.</param>
    /// <param name="placeholder">Placeholder rating, or -1 for none.</param>
    /// <param name="preview">Pointer-over preview value, or -1 when not hovering/interacting.</param>
    public static RatingControlTemplateSettings For(
        float value, float placeholder, float preview, int max, bool isEnabled, bool readOnly)
    {
        const float none = RatingControl.NoValueSet;

        // Visual-state resolution -- exact precedence from UpdateRatingItemsAppearance():
        //   pointer-over -> preview value, color depends on whether a real Value is set vs placeholder/unset
        //   else Value set -> Set; else placeholder set -> Placeholder; else nothing.
        // Disabled wins on the foreground brush regardless. Every branch resolves a SINGLE resting brush: WinUI steps the
        // star color (GoToState useTransitions:false), so the resolved Foreground IS the pointer-over color (no ease).
        float display;
        ColorF fg;
        ColorF bg = StateBg;           // RatingControlUnselectedForeground = TextFillColorSecondaryBrush (static across states)

        bool pointerOver = preview > none && !readOnly && isEnabled;
        if (pointerOver)
        {
            display = MathF.Ceiling(preview);      // WinUI: ceil(mousePercentage * MaxRating)
            fg = value <= none ? StatePointerOverUnset : StateSet;
        }
        else if (value > none) { display = value; fg = StateSet; }
        else if (placeholder > none) { display = placeholder; fg = StatePlaceholder; }
        else { display = 0f; fg = StateSet; }

        if (!isEnabled) fg = StateDisabled;        // RatingControlDisabledSelectedForeground = TextFillColorDisabledBrush

        return new RatingControlTemplateSettings(Math.Clamp(display, 0f, max), fg, bg);
    }

    // ── Per-star hover SCALE (RatingControl::ApplyScaleExpressionAnimation, RatingControl.cpp:350-371) ──
    // WinUI stamps each glyph at RatingControlFontSizeForRendering = 32 (RatingControl_themeresources.xaml:39) and
    // drives the star visual's Scale.X/Y with the composition expression (RatingControl.cpp:358-360):
    //   scaleWinUI = max( -0.0005 · pointerScalar · (starCenterX − focalX)² + 1.0 · pointerScalar, 0.5 )
    // about CenterPoint (16,16) — the glyph centre (c_scaleAnimationCenterPointX/YValue, RatingControl.cpp:14-15, 370).
    // pointerScalar is the shared property-set scalar: c_mouseOverScale = 0.8 for mouse, c_touchOverScale = 1.0 for a
    // touch contact (RatingControl.cpp:19-20; seeded mouse at :116). focalX is the pointer X over the strip — the
    // expression's own comment (:355) documents it as updated from OnPointerMovedOverBackgroundStackPanel; off-strip it
    // rests at the −100 sentinel (c_noPointerOverMagicNumber :21; reset at :115/:867/:1138) which floors every star at 0.5.
    // COORDINATE SPACE: starCenterX comes from CalculateStarCenter (RatingControl.cpp:926-932) =
    // ActualRatingFontSize·(i+0.5) + i·ItemSpacing, and ActualRatingFontSize = RenderingRatingFontSize/2 = 16
    // (RatingControl.cpp:51-54) — i.e. panel-local ON-SCREEN px, the same space as our 16px-native strip (NOT the 32px
    // double-render coordinates).
    // RE-BASING: our glyphs are stamped at the on-screen 16px (≡ WinUI's 32px glyph at its 0.5 rest scale), so the
    // equivalent cell scale is exactly 2 × the WinUI expression: rest 2·0.5 = 1.0 (16px), mouse focal peak
    // 2·0.8 = 1.6 (25.6px), touch focal peak 2·1.0 = 2.0 (32px). The scale applies to the whole star CELL (a single
    // glyph, no overlay behind a filled star), so growing it never reveals an outline halo.
    public const float MouseOverScale = 0.8f;        // c_mouseOverScale  (RatingControl.cpp:19)
    public const float TouchOverScale = 1.0f;        // c_touchOverScale  (RatingControl.cpp:20)
    public const float NoPointerOverFocal = -100f;   // c_noPointerOverMagicNumber (RatingControl.cpp:21)

    /// <summary>The re-based per-star scale at <paramref name="starCenterX"/> given the live <paramref name="focalX"/>
    /// (both panel-local on-screen px) and the device <paramref name="pointerScalar"/> (<see cref="MouseOverScale"/> /
    /// <see cref="TouchOverScale"/>). 1.0 = resting (our 16px glyph); focal peak 1.6 (mouse) or 2.0 (touch).</summary>
    public static float StarScale(float starCenterX, float focalX, float pointerScalar)
    {
        float d = starCenterX - focalX;
        // 2 × max(-0.0005·p·d² + 1.0·p, 0.5)  (RatingControl.cpp:358-360, re-based into 16px-native space — see above)
        return 2f * MathF.Max(-0.0005f * pointerScalar * d * d + pointerScalar, 0.5f);
    }

    /// <summary>Mouse-scalar convenience overload (the pre-touch-damping signature, kept for source compat).</summary>
    public static float StarScale(float starCenterX, float focalX) => StarScale(starCenterX, focalX, MouseOverScale);

    /// <summary>Star center in panel-local on-screen px (RatingControl::CalculateStarCenter, RatingControl.cpp:926-932):
    /// ActualRatingFontSize·(index+0.5) + index·ItemSpacing, with ActualRatingFontSize = 32/2 = 16 (cpp:51-54).</summary>
    public static float StarCenter(int index, float starSize, float itemSpacing)
        => starSize * (index + 0.5f) + index * itemSpacing;

    // ── Visual-state brushes (RatingControl_themeresources.xaml, Default/Light dictionaries) ──
    static ColorF StateBg               => Tok.TextSecondary;            // RatingControlUnselectedForeground
    static ColorF StateSet              => Tok.AccentDefault;            // RatingControlSelectedForeground
    static ColorF StatePlaceholder      => Tok.TextPrimary;             // RatingControlPlaceholderForeground
    static ColorF StatePointerOverUnset => Tok.FillControlAltTertiary;  // RatingControl(PointerOverPlaceholder/PointerOverUnselected)Foreground
    static ColorF StateDisabled         => Tok.TextDisabled;            // RatingControlDisabledSelectedForeground
}

/// <summary>
/// A WinUI <c>RatingControl</c>: a row of stars set by click or press-and-sweep, with an optional caption. The control
/// is a two-layer star row -- an always-present unset/background layer (outline glyph E734) and a clipped Set layer
/// (filled glyph E735) revealed to the current rating. Faithful port of <c>RatingControl.cpp</c>:
/// <list type="bullet">
/// <item><b>State model</b> -- <see cref="Value"/> (caller <see cref="FloatSignal"/>; <c>-1</c> == unset), an unset
/// <see cref="PlaceholderValue"/> shown until a real value is set, and an optional <see cref="Caption"/>.</item>
/// <item><b>Pointer preview</b> -- press-and-sweep fills the stars to the pointer (with a partial tail) and lifts the
/// foreground to the pointer-over visual-state color; release commits. <see cref="IsClearEnabled"/> clears the rating
/// when you click the star equal to the current value. Implicit capture keeps the sweep alive OFF the strip while
/// pressed, so dragging past the LEFT edge and releasing clears to <see cref="NoValueSet"/>
/// (<c>RatingControl.cpp:799-805, 856-863, 888-906</c>). A TOUCH press lifts the hover-scale scalar to
/// <c>c_touchOverScale</c> = 1.0 (vs mouse 0.8) so the focal star grows to full size under the finger.</item>
/// <item><b>Keyboard</b> -- Left/Down -1, Right/Up +1, Home clears, End sets <see cref="MaxRating"/>
/// (<c>RatingControl::OnKeyDown</c>).</item>
/// <item><b>Read-only / disabled</b> -- <see cref="ReadOnly"/> renders a fixed rating with no interaction;
/// <see cref="IsEnabled"/>=false dims the foreground to the disabled brush and gates all input.</item>
/// </list>
/// Custom font/image item-info (<c>RatingItemFontInfo</c>/<c>RatingItemImageInfo</c> per-state glyphs/images) is deferred.
/// </summary>
public sealed class RatingControl : Component
{
    // WinUI MUX_RatingControlDefaultFontInfo: Glyph=E735 (filled/Set), UnsetGlyph=E734 (outline/background).
    const string OutlineStar = "\uE734";   // UnsetGlyph (outline / background layer)
    const string FilledStar  = "\uE735";   // Glyph (filled / Set layer)

    /// <summary>The WinUI <c>c_noValueSetSentinel</c>: <see cref="Value"/> &lt;= this means "unset".</summary>
    public const float NoValueSet = -1f;

    // Template parts (the WinUI x:Name vocabulary; see TemplateParts). Each part's doc lists the props the control
    // OWNS (re-asserted after any modifier — a Parts customization cannot win those).
    /// <summary>The returned control root (star strip + caption). Owned: OnKeyDown (the arrow/Home/End rating keys),
    /// Role, Children.</summary>
    public const string PartRoot = "Root";
    /// <summary>The interactive star strip (WinUI RatingBackgroundStackPanel). Owned: OnHoverMove, OnPointerDown,
    /// OnPointerPressed, OnDrag, OnClick, OnPointerExit (the hover/sweep/commit lifecycle), Children. NOTE: the sweep
    /// math maps pointer X over the STOCK width (<see cref="StarSize"/>/<see cref="ItemSpacing"/>) — resizing this
    /// part skews the star-under-pointer mapping.</summary>
    public const string PartStarRow = "StarRow";
    /// <summary>Each star cell (applied to EVERY star — full, empty and partial alike). Owned: TransformBind +
    /// TransformOriginX/Y (the focal hover-scale expression about the cell centre).</summary>
    public const string PartStarCell = "StarCell";
    /// <summary>The trailing caption (WinUI Caption TextBlock) — a <see cref="TextEl"/> part
    /// (<c>Parts.Set&lt;TextEl&gt;(…)</c>). No owned props.</summary>
    public const string PartCaption = "Caption";

    public FloatSignal Value = new(NoValueSet);   // caller-owned; -1 == unset (a page can read it)
    public int MaxRating = 5;                     // RatingControl MaxRating default = 5
    public float PlaceholderValue = NoValueSet;   // shown (Placeholder state) until a real Value is set; -1 == none
    public string Caption = "";                   // trailing CaptionTextBlockStyle label
    public int InitialSetValue = 1;               // value picked when a keyboard first sets an unset rating (default 1)
    public bool IsClearEnabled = true;            // clicking the star == current value clears the rating
    public bool ReadOnly;                         // IsReadOnly: fixed rating, no interaction
    public bool IsEnabled = true;                 // disabled gate + disabled foreground brush
    public Action<float>? OnChange;               // ValueChanged
    /// <summary>Lightweight per-part styling (CSS ::part): modifiers keyed by the <c>PartXxx</c> consts; see
    /// <see cref="TemplateParts"/> for the contract.</summary>
    public TemplateParts? Parts;

    // ── WinUI sizing (RatingControl.h / _themeresources) ──
    // ActualRatingFontSize == RenderingRatingFontSize / 2 == 32/2 == 16 (the on-screen glyph size).
    public float StarSize = 16f;                  // ActualRatingFontSize
    public float ItemSpacing = 8f;                // RatingControlItemSpacing = 8
    const int CaptionSpacing = 12;                // c_captionSpacing = 12 (gap before the caption)
    const float CaptionFontSize = 12f;            // Caption TextBlock FontSize = 12 (CaptionTextBlockStyle)

    public static Element Create(
        FloatSignal value, int max = 5, float placeholder = NoValueSet, string caption = "",
        bool readOnly = false, bool isEnabled = true, bool isClearEnabled = true, Action<float>? onChange = null)
        => Embed.Comp(() => new RatingControl
        {
            Value = value, MaxRating = max, PlaceholderValue = placeholder, Caption = caption,
            ReadOnly = readOnly, IsEnabled = isEnabled, IsClearEnabled = isClearEnabled, OnChange = onChange,
        });

    public override Element Render()
    {
        int max = Math.Max(1, MaxRating);                       // OnPropertyChanged coerces MaxRating to >= 1
        float starSize = StarSize, gap = ItemSpacing;
        bool interactive = IsEnabled && !ReadOnly;

        // The pointer-over preview value (in *stars*, -1 == not hovering). A bare hover OR a press-sweep writes it; a
        // click commits. Granular state drives the visual (re-renders only this control's subtree -- cold path, not a
        // hot bind); the mirror REF carries the live swept value so Commit (a click handler running against this frame's
        // captured closure) reads the value the move just wrote, not the now-stale render-time snapshot.
        var (preview, setPreview) = UseState(NoValueSet);
        var previewRef = UseRef(NoValueSet);                    // live swept/hovered value (-1 == not hovering); read at commit

        // Live pointer X within the strip (panel-local px) driving the per-star hover SCALE, compositor-only via a
        // TransformBind on each star cell (no re-render). Off-strip it rests at the sentinel (-100) → every star floors at
        // 1.0. A FloatSignal so the scale follows the cursor without a re-render — WinUI's composition expression.
        var focal = UseFloatSignal(RatingControlTemplateSettings.NoPointerOverFocal);
        // The shared pointerScalar (RatingControl.cpp:19-20, 116): c_mouseOverScale = 0.8 by default; a TOUCH press
        // switches it to c_touchOverScale = 1.0 so the focal star grows to the full 32px under the finger. The device
        // kind is only carried on PRESS (PointerEventArgs.Kind) — bare hover-move has no device kind in the engine, so
        // the scalar persists from the last press (mouse damping as the default), noted as an accepted approximation.
        var pointerScalar = UseFloatSignal(RatingControlTemplateSettings.MouseOverScale);
        // m_isPointerDown (RatingControl.cpp:879, 901-905): while the pointer is held we keep implicit capture — the
        // sweep continues OFF the strip (the drag-off-the-left-side-to-clear gesture, cpp:799-805, 856-863) and
        // pointer-exit must NOT reset the preview/scale until release.
        var downRef = UseRef(false);

        // Programmatic coercion (RatingControl::CoerceValueBetweenMinAndMax): a caller-set fractional value in (0,1]
        // snaps up to 1, a negative value snaps to the -1 "unset" sentinel; fractional values > 1 keep their tail.
        float committed = Coerce(Value.Value, max);             // subscribes -> re-render when the caller mutates the value
        float placeholder = Coerce(PlaceholderValue, max);
        var ts = RatingControlTemplateSettings.For(
            committed, placeholder, interactive ? preview : NoValueSet, max, IsEnabled, ReadOnly);

        // ── Interaction (RatingControl.cpp pointer + keyboard) ──
        // mousePercentage = x / actualRatingWidth ; SetRatingTo(ceil(pct * Max)). Hover/sweep update the preview; click commits.
        float ratingWidth = max * starSize + (max - 1) * gap;   // CalculateActualRatingWidth()

        float StarsAt(Point2 p)
        {
            float pct = Math.Clamp(p.X / MathF.Max(ratingWidth, 1f), 0f, 1f);
            return Math.Clamp(MathF.Ceiling(pct * max), 0f, max);
        }

        // Bare hover (OnPointerMovedOverBackgroundStackPanel) AND press-drag share this: fill stars to the cursor.
        // WinUI fills on bare mouse-over -- no button required.
        void Sweep(Point2 p)
        {
            if (!interactive) return;
            focal.Value = p.X;                                  // compositor-only: per-star scale follows the cursor (no re-render)
            float v = StarsAt(p);
            previewRef.Value = v;                               // immediate (read by Commit this same frame)
            if (v != preview) setPreview(v);                    // live preview fill (foreground steps to the pointer-over color)
        }

        // Press: record device kind for the scale damping + arm the pointer-down capture gate, then sweep.
        // (OnPointerDown delivers the position; OnPointerPressed fires alongside it with the device kind.)
        void PressInfo(PointerEventArgs e)
        {
            if (!interactive) return;
            downRef.Value = true;
            pointerScalar.Value = e.Kind == PointerKind.Touch
                ? RatingControlTemplateSettings.TouchOverScale      // c_touchOverScale = 1.0 (RatingControl.cpp:20)
                : RatingControlTemplateSettings.MouseOverScale;     // c_mouseOverScale = 0.8 (RatingControl.cpp:19)
        }

        // Release (OnPointerReleasedBackgroundStackPanel, RatingControl.cpp:888-906): commit the swept value — capture
        // delivers the release even OFF the strip, so a drag past the left edge releases at a swept value of 0, which
        // SetRatingTo turns into the cleared sentinel (drag-off-left-to-clear). The dispatcher routes a release outside
        // the strip to this click handler via the captured OnDrag target.
        void Commit()
        {
            downRef.Value = false;                              // m_isPointerDown = false (cpp:901-903)
            if (!interactive || previewRef.Value <= NoValueSet) return;   // Enter/Space reach the click handler too -- ignore unless swept
            float swept = previewRef.Value;
            previewRef.Value = NoValueSet;
            focal.Value = RatingControlTemplateSettings.NoPointerOverFocal;   // release -> stars settle back to 1.0
            SetRatingTo(swept, originatedFromMouse: true);
            setPreview(NoValueSet);                             // pointer released -> clear the preview (PointerExited)
            // WinUI also plays ElementSound Invoke here (RatingControl.cpp:898) — pending the IPlatformSound PAL seam.
        }

        // Pointer left the strip (PointerExited → PointerExitedImpl, RatingControl.cpp:844-873): drop the hover preview
        // AND settle the scale — but ONLY when the pointer is not held: while m_isPointerDown the capture keeps the
        // sweep (and the drag-off-left clear) alive outside the strip (cpp:856-863 "Only clear pointer capture when not
        // holding pointer down for drag left to clear value scenario").
        void ClearPreview()
        {
            if (downRef.Value) return;
            focal.Value = RatingControlTemplateSettings.NoPointerOverFocal;
            if (previewRef.Value <= NoValueSet && preview <= NoValueSet) return;
            previewRef.Value = NoValueSet;
            setPreview(NoValueSet);
        }

        void OnKey(KeyEventArgs e)
        {
            if (!interactive) return;
            switch (e.KeyCode)
            {
                case Keys.Left: case Keys.Down:  ChangeRatingBy(-1f); e.Handled = true; break;   // Down maps to Left
                case Keys.Right: case Keys.Up:   ChangeRatingBy(+1f); e.Handled = true; break;   // Up maps to Right
                case Keys.Home:  SetRatingTo(0f, originatedFromMouse: false); e.Handled = true; break;
                case Keys.End:   SetRatingTo(max, originatedFromMouse: false); e.Handled = true; break;
            }
        }

        // ── Star row: ONE glyph per star (filled OR outline), so a fully-filled star has NO outline glyph behind it —
        // no overlay, no sub-pixel halo. Only a genuine FRACTIONAL star (a programmatic value like 3.5; the interactive
        // preview is always integer = ceil) uses a minimal overlay: the outline behind + the filled glyph clipped to the
        // left fraction. The star color is STEPPED instantly (WinUI GoToState useTransitions:false), no hover ramp.
        Element StarCell(string glyph, ColorF color) => new BoxEl
        {
            Width = starSize, Height = starSize, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            HitTestVisible = false,
            Children = new Element[] { new TextEl(glyph) { Size = starSize, Color = color, FontFamily = Theme.IconFont } },
        };

        var stars = new Element[max];
        for (int i = 0; i < max; i++)
        {
            float frac = Math.Clamp(ts.DisplayValue - i, 0f, 1f);   // 1 = full, 0 = empty, (0,1) = partial
            var leadGap = i == 0 ? default : new Edges4(gap, 0, 0, 0);   // ItemSpacing between stars
            float starCenter = RatingControlTemplateSettings.StarCenter(i, starSize, gap);
            BoxEl cell;
            if (frac >= 0.999f)
                cell = (BoxEl)StarCell(FilledStar, ts.Foreground);
            else if (frac <= 0.001f)
                cell = (BoxEl)StarCell(OutlineStar, ts.Background);
            else
                cell = new BoxEl   // partial star: outline full + filled clipped to the left `frac` (one star only)
                {
                    Width = starSize, Height = starSize, ZStack = true, HitTestVisible = false,
                    Children = new Element[]
                    {
                        StarCell(OutlineStar, ts.Background),
                        new BoxEl   // clip the filled glyph to its left fraction; the inner full-width cell keeps it centred so the visible left part aligns with the outline
                        {
                            Width = MathF.Max(0.0001f, frac * starSize), Height = starSize,
                            ClipToBounds = true, HitTestVisible = false,
                            Children = new Element[] { StarCell(FilledStar, ts.Foreground) },
                        },
                    },
                };
            // Per-star hover scale about the cell centre, driven compositor-only by the live `focal` pointer X and the
            // device pointerScalar (mouse 0.8 / touch 1.0). Scaling the WHOLE cell (a single glyph, or the
            // self-contained outline+clipped-fill) keeps every layer aligned → no halo.
            Func<Affine2D> scaleBind = () =>
            {
                float s = RatingControlTemplateSettings.StarScale(starCenter, focal.Value, pointerScalar.Value);
                return Affine2D.Scale(s, s);
            };
            var star = cell with
            {
                Margin = leadGap,
                TransformOriginX = 0.5f, TransformOriginY = 0.5f,
                TransformBind = scaleBind,
            };
            // Parts: restyle every cell (margins, fills…); the focal hover-scale bind always wins.
            if (Parts is { } sp)
                star = sp.Apply(PartStarCell, star) with
                { TransformOriginX = 0.5f, TransformOriginY = 0.5f, TransformBind = scaleBind };
            stars[i] = star;
        }

        // Whole-panel interaction (RatingControl.cpp): OnHoverMove = bare mouse-over fill (:807-822); OnPointerDown
        // begins the sweep and OnPointerPressed arms capture + the device scalar (:875-886); OnDrag = press-and-sweep
        // (capture keeps it firing off-strip — the drag-off-left clear, :799-805); OnClick commits on release
        // (:888-906; a release OUTSIDE the strip reaches it through the dispatcher's captured-drag release routing);
        // OnPointerExit drops the preview only while not held (:849-873).
        var starRow = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center,
            Width = ratingWidth, Height = starSize,
            IsEnabled = interactive,
            OnHoverMove = interactive ? Sweep : null,
            OnPointerDown = interactive ? Sweep : null,
            OnPointerPressed = interactive ? PressInfo : null,
            OnDrag = interactive ? Sweep : null,
            OnClick = interactive ? Commit : null,
            OnPointerExit = interactive ? ClearPreview : null,
            Children = stars,
        };
        // Parts: restyle the strip; the hover/sweep/commit lifecycle (and its interactive gating) always wins.
        if (Parts is { } rp)
            starRow = rp.Apply(PartStarRow, starRow) with
            {
                OnHoverMove = interactive ? Sweep : null,
                OnPointerDown = interactive ? Sweep : null,
                OnPointerPressed = interactive ? PressInfo : null,
                OnDrag = interactive ? Sweep : null,
                OnClick = interactive ? Commit : null,
                OnPointerExit = interactive ? ClearPreview : null,
                Children = stars,
            };

        // ── Caption (CaptionStackPanel -> Caption TextBlock) ──
        Element[] rowChildren = string.IsNullOrEmpty(Caption)
            ? new Element[] { starRow }
            : new Element[]
            {
                starRow,
                Parts.Apply(PartCaption, new TextEl(Caption)
                {
                    Size = CaptionFontSize,                         // 12 (CaptionTextBlockStyle)
                    Color = Tok.TextSecondary,                      // RatingControlCaptionForeground = TextFillColorSecondaryBrush
                    Margin = new Edges4(CaptionSpacing, 0, 0, 0),   // c_captionSpacing = 12 before the caption
                    AlignSelf = FlexAlign.Center,
                }),
            };

        var root = new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            MinHeight = 32f,                                        // RatingControl MinHeight = 32
            Role = AutomationRole.Rating,
            IsEnabled = IsEnabled,
            Focusable = interactive,                               // takes keyboard focus when interactive
            OnKeyDown = interactive ? OnKey : null,
            Children = rowChildren,
        };
        // Parts: restyle the root; the rating keys, the automation role and the structure always win.
        if (Parts is { } pr)
            root = pr.Apply(PartRoot, root) with
            { OnKeyDown = interactive ? OnKey : null, Role = AutomationRole.Rating, Children = rowChildren };
        return root;

        // ── Local helpers (RatingControl.cpp ChangeRatingBy / SetRatingTo) ──
        void ChangeRatingBy(float change)
        {
            float old = Value.Peek();
            float rating;
            if (old > NoValueSet)
            {
                if (MathF.Truncate(old) != old)                     // drop a programmatic fraction before stepping
                    rating = change == -1f ? MathF.Truncate(old) : MathF.Truncate(old) + change;
                else
                    rating = old + change;
            }
            else
            {
                rating = InitialSetValue;                           // first key on an unset rating jumps to InitialSetValue
            }
            SetRatingTo(rating, originatedFromMouse: false);
        }

        void SetRatingTo(float newRating, bool originatedFromMouse)
        {
            float rating = Math.Clamp(newRating, 0f, max);
            float old = Value.Peek();

            // The base case: no rating + pressed left -> nothing happens.
            if (!(old > NoValueSet || rating != 0f)) return;

            float result;
            if (!IsClearEnabled && rating <= 0f)
                result = 1f;                                        // can't clear -> floor at 1
            else if (rating == old && IsClearEnabled && (rating != max || originatedFromMouse))
                result = NoValueSet;                                // click the current value (or a non-max key) -> clear
            else if (rating > 0f)
                result = rating;
            else
                result = NoValueSet;

            if (result == old) return;
            Value.Value = result;
            OnChange?.Invoke(result);
        }
    }

    /// <summary>Programmatic value coercion (RatingControl::CoerceValueBetweenMinAndMax): a negative value snaps to the
    /// <see cref="NoValueSet"/> (-1) "unset" sentinel; a value in (0,1] snaps up to 1 (a single star); a value above
    /// <paramref name="max"/> clamps down to max. Fractional values in (1,max] keep their tail (the partial-star fill).
    /// Applied to both <see cref="Value"/> and <see cref="PlaceholderValue"/> at render time.</summary>
    public static float Coerce(float value, int max)
    {
        switch (value)
        {
            case < 0f:
                return NoValueSet;     // all negatives -> the "unset" sentinel
            case <= 1f:
                return 1f;            // (0,1] -> one star (note: a programmatic 0 coerces UP to 1, per WinUI)
        }

        if (value > max) return max;
        return value;                          // fractional (1,max] keeps its tail
    }
}
