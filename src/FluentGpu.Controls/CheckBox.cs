using FluentGpu.Animation;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI CheckBox: a 20px box + label. Two-state via the <see cref="bool"/> overload; three-state (adds the mixed
/// "indeterminate" glyph) via the <see cref="CheckState"/> overload, which cycles Unchecked → Checked → Indeterminate →
/// Unchecked on click. Controlled — the caller owns the state.
///
/// Visuals follow WinUI 1:1 but WITHOUT WinUI's 12-state matrix (Unchecked/Checked/Indeterminate × Normal/PointerOver/
/// Pressed/Disabled, each restating 7 setters). Instead each property is a <see cref="StateBrush"/> ramp over the
/// interaction axis, picked by the logical state; the engine eases Rest→Hover→Pressed using the element's interaction
/// timing specs and the disabled leg is a flat swap. The glyph uses WinUI's AnimatedAccept draw-on timeline plus
/// <c>PressScale</c> for the pressed "squish". So ramps + one authored stroke-trim timeline express all reachable
/// states — see <see cref="ControlMotion"/>.
/// </summary>
public static partial class CheckBox
{
    // Template parts (the TemplateParts door; see Expander for the reference adoption). Each part's doc lists the
    // props the control OWNS (re-asserted after any modifier — a Parts customization cannot win those).
    /// <summary>The returned clickable row (WinUI RootGrid). Owned: OnClick (the toggle), Role, Children.</summary>
    public const string PartRoot = "Root";
    /// <summary>The 20px check square (WinUI NormalRectangle). Owned: Children (the conditional draw-on mark mount —
    /// checkmark when Checked, dash when Indeterminate, none when Unchecked).</summary>
    public const string PartBox = "Box";
    /// <summary>The drawn checkmark / indeterminate dash (WinUI CheckGlyph) — a <see cref="PolylineStrokeEl"/>, so use
    /// <c>Parts.Set&lt;PolylineStrokeEl&gt;(CheckBox.PartMark, …)</c>. Owned: TrimStart/TrimEnd (the StrokeTrimEnd
    /// draw-on keyframes land there).</summary>
    public const string PartMark = "Mark";
    /// <summary>The label (a <see cref="TextEl"/> — use <c>Parts.Set&lt;TextEl&gt;(CheckBox.PartLabel, …)</c>).
    /// Owned: none (the fg ramp sits in the stock build; a modifier may restyle it).</summary>
    public const string PartLabel = "Label";

    public sealed record Style
    {
        public float BoxSize { get; init; } = 20f;          // CheckBoxSize (CheckBox_themeresources.xaml:270)
        public float GlyphSize { get; init; } = 12f;        // CheckBoxGlyphSize (CheckBox_themeresources.xaml:271)
        public float FontSize { get; init; } = 14f;         // ControlContentThemeFontSize
        public float MinHeight { get; init; } = 32f;        // CheckBoxHeight (CheckBox_themeresources.xaml:272/291)
        public float MinWidth { get; init; } = 120f;        // CheckBoxMinWidth (CheckBox_themeresources.xaml:273/290)
        /// <summary>Box→label gap: CheckBoxPadding 8,5,0,0 — the ContentPresenter's left margin off the Auto (20px)
        /// glyph column (CheckBox_themeresources.xaml:274/283 + grid columns :598-601).</summary>
        public float ContentGap { get; init; } = 8f;
        /// <summary>WinUI CheckBoxFocusVisualMargin −7,−3,−7,−3 (CheckBox_themeresources.xaml:275/293); engine-drawn (E1).</summary>
        public Edges4 FocusVisualMargin { get; init; } = new(-7f, -3f, -7f, -3f);

        // Unchecked box: fill = ControlAltFillColor ramp; stroke = ControlStrongStroke (DIMS to Disabled on press, per WinUI).
        public StateBrush OffFill { get; init; }
        public StateBrush OffStroke { get; init; }
        // Checked / Indeterminate box: accent ramp; stroke tracks the accent fill (→ StrongDisabled when disabled).
        public StateBrush OnFill { get; init; }
        public StateBrush OnStroke { get; init; }
        // The checkmark / dash glyph foreground (text-on-accent ramp; dims to Secondary on press, per WinUI).
        public StateBrush GlyphFg { get; init; }
        // The label foreground (TextPrimary, → TextDisabled when disabled).
        public StateBrush LabelFg { get; init; }
    }

    public static Style? StyleOverride;
    public static Style DefaultStyle => StyleOverride ?? new Style
    {
        // Ramp = (Rest, PointerOver, Pressed, Disabled) — the exact WinUI CheckBox_themeresources brush ladder.
        OffFill   = new(Tok.FillControlAltSecondary, Tok.FillControlAltTertiary, Tok.FillControlAltQuaternary, Tok.FillControlAltDisabled),
        OffStroke = new(Tok.StrokeControlStrongDefault, Tok.StrokeControlStrongDefault, Tok.StrokeControlStrongDisabled, Tok.StrokeControlStrongDisabled),
        OnFill    = new(Tok.AccentDefault, Tok.AccentSecondary, Tok.AccentTertiary, Tok.AccentDisabled),
        OnStroke  = new(Tok.AccentDefault, Tok.AccentSecondary, Tok.AccentTertiary, Tok.StrokeControlStrongDisabled),
        GlyphFg   = new(Tok.TextOnAccentPrimary, Tok.TextOnAccentPrimary, Tok.TextOnAccentSecondary, Tok.TextOnAccentDisabled),
        LabelFg   = StateBrush.Flat(Tok.TextPrimary) with { Disabled = Tok.TextDisabled },
    };

    public static BoxEl Create(string label, bool isChecked, Action onToggle, Style? style = null, bool isEnabled = true,
                               TemplateParts? parts = null)
        => Build(label, isChecked ? CheckState.Checked : CheckState.Unchecked, _ => onToggle(), style, isEnabled, parts);

    public static BoxEl Create(string label, CheckState state, Action<CheckState> onChange, Style? style = null, bool isEnabled = true,
                               TemplateParts? parts = null)
    {
        var next = state switch
        {
            CheckState.Unchecked => CheckState.Checked,
            CheckState.Checked => CheckState.Indeterminate,
            _ => CheckState.Unchecked,
        };
        return Build(label, state, _ => onChange(next), style, isEnabled, parts);
    }

    static BoxEl Build(string label, CheckState state, Action<CheckState> onClick, Style? style, bool enabled, TemplateParts? parts)
    {
        var s = style ?? DefaultStyle;
        bool on = state == CheckState.Checked;
        bool indet = state == CheckState.Indeterminate;
        bool filled = on || indet;

        StateBrush fill = filled ? s.OnFill : s.OffFill;
        StateBrush stroke = filled ? s.OnStroke : s.OffStroke;
        ColorF glyphColor = s.GlyphFg.Resting(enabled);

        // The glyph is a child the reconciler INSERTS on check / REMOVES on uncheck. On check it doesn't fade or scale in
        // — the checkmark STROKE DRAWS ITSELF (WinUI's AnimatedAcceptVisualSource): two pen-stroke capsules in a clip box
        // whose presented width reveals 0→full left-to-right (the check's x increases monotonically left-tip→vertex→right-
        // tip, so a left-to-right reveal is the pen order). The dash reveals the same way. Unchecked has NO child (an empty
        // placeholder would persist as the same node and suppress the insert/remove). The box fill/stroke ease under it.
        Element[] markChildren = on
            ? [Embed.Comp(() => new DrawnCheckmark { Size = s.GlyphSize + 2f, Thickness = 1.8f, Color = glyphColor, Pressable = enabled, Parts = parts })]
            : indet
                ? [Embed.Comp(() => new DrawnDash { Width = s.BoxSize * 0.5f, Thickness = 2f, Color = glyphColor, Pressable = enabled, Parts = parts })]
                : [];

        // The box keeps a 1px ring in every state; the engine eases Fill/BorderColor toward the Hover/Pressed legs of the
        // ramp with the row's interaction timing. The disabled leg is the resting swap (Resting(enabled)) — when the row is
        // IsEnabled=false the engine routes no hover/press progress down, so the Hover/Pressed legs are never reached.
        var box = new BoxEl
        {
            Width = s.BoxSize, Height = s.BoxSize,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = Radii.ControlAll,
            BorderWidth = 1f,
            Fill = fill.Resting(enabled),
            HoverFill = fill.Hover,
            PressedFill = fill.Pressed,
            BorderColor = stroke.Resting(enabled),
            HoverBorderColor = stroke.Hover,
            PressedBorderColor = stroke.Pressed,
            Children = markChildren,
        };
        // Parts: restyle the square freely (the state ramps above sit BEFORE the modifier); the mark mount always wins.
        box = parts.Apply(PartBox, box) with { Children = markChildren };

        Action toggle = () => onClick(state);
        Element[] children =
        [
            box,
            parts.Apply(PartLabel, new TextEl(label)
            {
                Size = s.FontSize,
                Color = s.LabelFg.Rest,
                HoverColor = s.LabelFg.Hover,
                PressedColor = s.LabelFg.Pressed,
                DisabledColor = s.LabelFg.Disabled,
            }),
        ];

        var root = new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Gap = s.ContentGap,                  // CheckBoxPadding 8,5,0,0 (CheckBox_themeresources.xaml:274)
            MinHeight = s.MinHeight,
            MinWidth = s.MinWidth,               // CheckBoxMinWidth 120 (CheckBox_themeresources.xaml:273/290)
            Role = AutomationRole.CheckBox,
            // Engine disabled gate: it stops hit-test/focus/keyboard when IsEnabled=false, so OnClick stays wired
            // (no manual null) and the label TextEl picks its disabled leg from the nearest interactive ancestor.
            IsEnabled = enabled,
            // Keyboard focus: UseSystemFocusVisuals + CheckBoxFocusVisualMargin −7,−3,−7,−3
            // (CheckBox_themeresources.xaml:292-293); the engine draws the ring (E1). Space/Enter activation = the
            // engine's focused-clickable contract → OnClick (WinUI CheckBox toggles via ButtonBase Space-on-keyup).
            // NOTE deliberately NO BrushTransitionMs: every CheckBox state-brush change is a DiscreteObjectKeyFrame at
            // KeyTime 0 (e.g. CheckedNormal, CheckBox_themeresources.xaml:378-401) — WinUI does not cross-fade here.
            Focusable = true,
            FocusVisualMargin = s.FocusVisualMargin,
            OnClick = toggle,
            // Space ONLY (KeyPress::Button bAcceptsReturn=false, CheckBox_Partial.cpp:27) — Enter falls through
            // to normal key routing (e.g. a dialog's default button).
            ActivateOnEnter = false,
            Children = children,
        };
        // Parts: restyle anything on the row; the toggle mechanics and the box+label structure always win.
        return parts.Apply(PartRoot, root) with { OnClick = toggle, Role = AutomationRole.CheckBox, Children = children };
    }
}

/// <summary>
/// The checkmark, DRAWN — WinUI's AnimatedAcceptVisualSource without Lottie. Two rounded "pen-stroke" capsules
/// (down-stroke + up-stroke) sit in a clip box; on mount the box's PRESENTED width reveals 0→Size, and because the
/// check's x-coordinate increases monotonically (left tip → bottom vertex → top-right tip) a left-to-right reveal is
/// exactly the pen order — the stroke appears to draw itself. Compositor-only (a SizeW reveal clips, never relayouts).
/// </summary>
internal static class CheckBoxMotion
{
    // WinUI source: AnimatedAcceptVisualSource (Controls_01_Checkmark), NormalOffToNormalOn.
    // Full visual: 160 frames at 60 fps = 2666.6667 ms.
    public const float AcceptDurationMs = 2666.6667f;
    public const float NormalOffToNormalOnStart = 0.09375f;
    public const float NormalOffToNormalOnEnd = 0.212500006f;
    public const float NormalOffToNormalOnMs = (NormalOffToNormalOnEnd - NormalOffToNormalOnStart) * AcceptDurationMs;
    public static readonly EasingSpec AcceptTrimEndEase = EasingSpec.CubicBezier(0.550000012f, 0f, 0f, 1f);
}

/// <summary>The checked glyph stroke-trim animation, using WinUI AnimatedAccept NormalOffToNormalOn constants.</summary>
internal sealed class DrawnCheckmark : Component
{
    public float Size = 14f;
    public float Thickness = 1.8f;
    public ColorF Color;
    public bool Pressable = true;
    public float DurationMs = CheckBoxMotion.NormalOffToNormalOnMs;
    public TemplateParts? Parts;   // routes CheckBox.PartMark onto the stroke

    // Normalized checkmark vertices in the Size×Size box (y down). x is monotonic → left-to-right reveal = draw order.
    static readonly EasingSpec DrawEase = CheckBoxMotion.AcceptTrimEndEase;
    static readonly (float x, float y) P0 = (0.18f, 0.50f), P1 = (0.42f, 0.72f), P2 = (0.80f, 0.26f);

    public override Element Render()
    {
        // Reveal the presented width 0→Size on mount (clip exposes the stroke left-to-right). LayoutEffect seeds it before
        // the first anim tick, so the very first recorded frame is already at width 0 — no full-checkmark flash.
        Context.UseKeyframes(AnimChannel.StrokeTrimEnd,
            [new Keyframe(0f, 0f, Easing.Linear), new Keyframe(1f, 1f, DrawEase)], DurationMs);

        var mark = new PolylineStrokeEl
        {
            Width = Size,
            Height = Size,
            P0 = new Point2(P0.x * Size, P0.y * Size),
            P1 = new Point2(P1.x * Size, P1.y * Size),
            P2 = new Point2(P2.x * Size, P2.y * Size),
            PointCount = 3,
            Color = Color,
            Thickness = Thickness,
            TrimStart = 0f,
            TrimEnd = 1f,
            RoundCaps = true,
            PressScale = Pressable ? 0.86f : 1f,   // WinUI's pressed "squish" (inherits the row's press progress)
        };
        // PartMark: restyle geometry/color/thickness; the trim stays owned (the draw-on keyframes land on it).
        return Parts.Apply(CheckBox.PartMark, mark) with { TrimStart = 0f, TrimEnd = 1f };
    }

}

/// <summary>The indeterminate dash, drawn the same way: a rounded bar revealed left-to-right by a presented-width sweep.</summary>
internal sealed class DrawnDash : Component
{
    public float Width = 10f;
    public float Thickness = 2f;
    public ColorF Color;
    public bool Pressable = true;
    public float DurationMs = 200f;
    public TemplateParts? Parts;   // routes CheckBox.PartMark onto the stroke

    public override Element Render()
    {
        Context.UseKeyframes(AnimChannel.StrokeTrimEnd,
            [new Keyframe(0f, 0f, Easing.Linear), new Keyframe(1f, 1f, CheckBoxMotion.AcceptTrimEndEase)], DurationMs);
        var dash = new PolylineStrokeEl
        {
            Width = Width,
            Height = Thickness,
            P0 = new Point2(0f, Thickness * 0.5f),
            P1 = new Point2(Width, Thickness * 0.5f),
            PointCount = 2,
            Color = Color,
            Thickness = Thickness,
            TrimStart = 0f,
            TrimEnd = 1f,
            RoundCaps = true,
            PressScale = Pressable ? 0.86f : 1f,
        };
        // PartMark (shared with the checkmark — one is mounted at a time); the trim stays owned (draw-on keyframes).
        return Parts.Apply(CheckBox.PartMark, dash) with { TrimStart = 0f, TrimEnd = 1f };
    }
}
