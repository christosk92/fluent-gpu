using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>Why an <see cref="AnnotatedScrollBar"/> scrolled — mirrors WinUI
/// <c>AnnotatedScrollBarScrollingEventKind</c> (AnnotatedScrollBar.idl:6-12).</summary>
public enum AnnotatedScrollBarScrollKind : byte { Click = 0, Drag = 1, IncrementButton = 2, DecrementButton = 3 }

/// <summary>
/// WinUI <c>AnnotatedScrollBar</c> (controls\dev\AnnotatedScrollBar) — the jump-list rail: a column of right-aligned
/// labels beside a thin accent thumb, with CLICK-TO-JUMP on the rail, thumb drag, and repeat-hold increment/decrement
/// buttons. Template facts (AnnotatedScrollBar.xaml + _themeresources.xaml; the _perf2026 variant differs only in
/// storyboard→setter form, identical values):
/// • Control MinWidth = LabelsGridMinWidth 44 (xaml:4, :37) — the control hugs ~44 unless its host stretches it.
/// • Root grid rows: increment RepeatButton on TOP (glyph EDDB, HorizontalAlignment=Right — xaml:31-39), the
///   vertical grid (star), decrement RepeatButton on the BOTTOM (glyph EDDC — xaml:94-102).
/// • Thumb = 30×3 @ r1.5 (ThumbWidth :36 / ThumbHeight :35 / ThumbCornerRadius :41), VerticalThumbBrush =
///   AccentFillColorDefault (:11/:21), HorizontalAlignment=Right + VerticalAlignment=Top at the scroll position
///   (xaml:84-92).
/// • Ghost thumb = same geometry in AccentFillColorDisabled (PART_VerticalThumbGhost xaml:74-83) — the hover preview
///   of where a rail click would land.
/// • PART_LabelsGrid: HorizontalAlignment=Center, MinWidth 44, transparent background (xaml:41-45); the
///   LabelTemplate is a right-aligned BodyTextBlockStyle TextBlock (14px Normal — TextBlock_themeresources.xaml:4/:23)
///   with Margin 0,-5,0,-2 (xaml:8-19).
/// • PART_ToolTipRail: a 1px right-aligned tooltip anchor rail (xaml:46) whose ToolTip is Placement=Top,
///   MaxWidth 360 / MinHeight 40 (:38/:39), BaseTextBlockStyle content (14px SemiBold) — modeled by
///   <c>detailLabel</c>: the resolved text floats in a chip above the pointer (the DetailLabelRequested seam).
/// • ScrollButtonStyle: MinWidth/MinHeight 16 (:43-44), FontSize 8 (:40/:51), CornerRadius = ControlCornerRadius 4
///   (:49), background SubtleFillColorTransparent in EVERY state (:5; the :56-87 visual states recolor ONLY the
///   foreground: TextFillColorPrimary → Secondary hover → Tertiary pressed → Disabled, :6-9), IsTabStop=False
///   (xaml:37/:100) — each steps by <c>SmallChange</c> (default ViewportSize / ratio —
///   AnnotatedScrollBar.cpp:484-489 EnsureSmallChangeValue).
/// • Rail click / drag / buttons raise <c>Scrolling</c> with the offset + kind, CANCELABLE
///   (AnnotatedScrollBarScrollingEventArgs.Cancel; cpp:960+ RaiseScrolling) — modeled by the
///   <c>onScrolling</c> filter returning false to cancel.
/// ENGINE SEMANTIC (the bug this file once had): inside a ZStack, children land at the LEFT edge and
/// <c>AlignSelf</c> is the VERTICAL overlay placement — RIGHT alignment therefore uses a full-width row wrapper
/// (<c>Direction=0, Justify=FlexJustify.End</c>) per overlay part, never <c>AlignSelf</c>.
/// Thumb position rides a <c>TransformBind</c> on the position signal (compositor-instant, no re-render).
/// </summary>
public static class AnnotatedScrollBar
{
    // Template parts (see TemplateParts). Each part's doc lists the props the control OWNS (re-asserted after any
    // modifier — a Parts customization cannot win those). Every Create overload takes a trailing `TemplateParts?
    // parts`. Const VALUES happen to match the internal reconcile Keys; the Keys themselves stay literal on the
    // elements (never derived from these consts).
    /// <summary>The labels grid (WinUI PART_LabelsGrid). Owned: Key, Children (the annotations slot — restructure
    /// via the annotations list, restyle via this part).</summary>
    public const string PartLabels = "asb-labels";
    /// <summary>The 1px right-aligned tooltip anchor rail row (WinUI PART_ToolTipRail). Owned: Key.</summary>
    public const string PartRail = "asb-rail";
    /// <summary>The hover-preview ghost-thumb row (WinUI PART_VerticalThumbGhost; mounted only while hovering).
    /// Owned: Key — the hover-tracking OffsetY is stock per-render placement a modifier sees and may override.</summary>
    public const string PartGhost = "asb-ghost";
    /// <summary>The hover detail-chip row (WinUI PART_DetailLabelToolTip; mounted while hovering with a
    /// detailLabel). Owned: Key — same OffsetY note as <see cref="PartGhost"/>.</summary>
    public const string PartTip = "asb-tip";
    /// <summary>The live accent-thumb row (WinUI PART_VerticalThumb). Owned: Key, TransformBind (the
    /// compositor-bound scroll position).</summary>
    public const string PartThumb = "asb-thumb";

    private const float ThumbWidth = 30f;      // ThumbWidth (:36)
    private const float ThumbHeight = 3f;      // ThumbHeight (:35)
    private const float ThumbRadius = 1.5f;    // ThumbCornerRadius (:41)
    private const float LabelsMinWidth = 44f;  // LabelsGridMinWidth (:37)
    private const float LabelSize = 14f;       // BodyTextBlockStyle (TextBlock_themeresources.xaml:4/:23)
    private const float ButtonGlyph = 8f;      // ScrollButtonFontSize (:40)
    private const float ButtonCell = 16f;      // ScrollButtonStyle MinWidth/MinHeight (:43-44)
    private const float TooltipMaxWidth = 360f;   // AnnotatedScrollBarTooltipMaxWidth (:38)
    private const float TooltipMinHeight = 40f;   // AnnotatedScrollBarTooltipMinHeight (:39)

    /// <summary>Static/legacy surface (kept source-compatible): annotations as (label, 0..1 position) with no
    /// callbacks — renders the full anatomy inert at position 0.2 (the original demo shape).</summary>
    public static Element Create(IReadOnlyList<(string Label, float Position01)> annotations, float height = 280f, TemplateParts? parts = null)
        => Create(annotations, 0.2f, onScroll: null, height: height, parts: parts);

    /// <summary>The interactive control with a FROZEN position (component props freeze at mount — use the
    /// <c>Signal&lt;float&gt;</c> overload below for a live, app-controlled position).</summary>
    public static Element Create(IReadOnlyList<(string Label, float Position01)> annotations,
                                 float position01,
                                 Action<float, AnnotatedScrollBarScrollKind>? onScroll,
                                 float height = 280f,
                                 float smallChange01 = 0.05f,
                                 Func<float, bool>? onScrolling = null,
                                 Func<float, string>? detailLabel = null,
                                 TemplateParts? parts = null)
        => Create(annotations, new Signal<float>(Math.Clamp(position01, 0f, 1f)), onScroll, height, smallChange01, onScrolling, detailLabel, parts);

    /// <summary>
    /// The interactive control. <paramref name="position01"/> is the normalized scroll position signal (0..1) —
    /// writes move the thumb compositor-instantly; <paramref name="onScroll"/> receives every user scroll
    /// (new position, kind); <paramref name="onScrolling"/> (optional) may return false to CANCEL a scroll (the WinUI
    /// Scrolling.Cancel seam); <paramref name="smallChange01"/> is the button step (default 0.05 — the SmallChange
    /// auto-value stands in for viewport/ratio, cpp:484-489); <paramref name="detailLabel"/> resolves the hover chip
    /// text from a position; <paramref name="parts"/> = per-part styling keyed by the <c>PartXxx</c> consts (see
    /// <see cref="TemplateParts"/> for the contract).
    /// </summary>
    public static Element Create(IReadOnlyList<(string Label, float Position01)> annotations,
                                 Signal<float> position01,
                                 Action<float, AnnotatedScrollBarScrollKind>? onScroll,
                                 float height = 280f,
                                 float smallChange01 = 0.05f,
                                 Func<float, bool>? onScrolling = null,
                                 Func<float, string>? detailLabel = null,
                                 TemplateParts? parts = null)
        => Embed.Comp(() => new AnnotatedScrollBarComponent
        {
            Annotations = annotations ?? [],
            Position = position01,
            OnScroll = onScroll,
            OnScrolling = onScrolling,
            DetailLabel = detailLabel,
            Height = height,
            SmallChange = smallChange01,
            Parts = parts,
        });

    internal sealed class AnnotatedScrollBarComponent : Component
    {
        public IReadOnlyList<(string Label, float Position01)> Annotations = [];
        public required Signal<float> Position;
        public Action<float, AnnotatedScrollBarScrollKind>? OnScroll;
        public Func<float, bool>? OnScrolling;
        public Func<float, string>? DetailLabel;
        public float Height = 280f;
        public float SmallChange = 0.05f;
        /// <summary>Lightweight per-part styling (CSS ::part): modifiers keyed by the <c>PartXxx</c> consts; see
        /// <see cref="TemplateParts"/> for the contract.</summary>
        public TemplateParts? Parts;

        public override Element Render()
        {
            var (hoverY, setHoverY) = UseState(float.NaN);   // rail hover → ghost thumb + detail chip

            float railHeight = MathF.Max(1f, Height - 2f * ButtonCell);   // minus the two 16px button rows (:43-44)

            void Scroll(float to, AnnotatedScrollBarScrollKind kind)
            {
                to = Math.Clamp(to, 0f, 1f);
                if (OnScrolling is not null && !OnScrolling(to)) return;   // Scrolling.Cancel (idl:27)
                OnScroll?.Invoke(to, kind);
            }

            void RailPress(Point2 p) => Scroll(p.Y / railHeight, AnnotatedScrollBarScrollKind.Click);
            void RailDrag(Point2 p) => Scroll(p.Y / railHeight, AnnotatedScrollBarScrollKind.Drag);

            // Annotation labels (PART_LabelsGrid content): each label is a FULL-WIDTH row with Justify=End — the
            // ZStack right-alignment idiom — placed via OffsetY; text up-shifted 5 per the LabelTemplate's
            // Margin 0,-5,0,-2 (xaml:11) so it centers on its position tick.
            var labels = new Element[Annotations.Count];
            for (int i = 0; i < Annotations.Count; i++)
            {
                var (label, p01) = Annotations[i];
                float p = Math.Clamp(p01, 0f, 1f);
                labels[i] = new BoxEl
                {
                    OffsetY = p * railHeight - 5f,
                    Direction = 0,
                    Justify = FlexJustify.End,                  // HorizontalAlignment=Right (xaml:12)
                    HitTestVisible = false,
                    Children = [new TextEl(label ?? "") { Size = LabelSize, Color = Tok.TextPrimary }],
                };
            }

            bool interactive = OnScroll is not null;
            bool hovering = interactive && !float.IsNaN(hoverY);

            // PART_LabelsGrid: HorizontalAlignment=Center MinWidth 44 (xaml:41-45) — center == fill in the
            // 44-wide control; the label rows right-align inside it.
            var labelsGrid = new BoxEl { Key = "asb-labels", ZStack = true, MinWidth = LabelsMinWidth, Height = railHeight, Children = labels };
            if (Parts is not null)
                labelsGrid = Parts.Apply(PartLabels, labelsGrid) with { Key = "asb-labels", Children = labels };   // structure = the annotations slot
            // PART_ToolTipRail: the 1px right-aligned tooltip anchor (xaml:46), right-aligned via a row wrapper.
            var tooltipRail = new BoxEl
            {
                Key = "asb-rail",
                Direction = 0,
                Justify = FlexJustify.End,
                HitTestVisible = false,
                Children = [new BoxEl { Width = 1f, Height = railHeight }],
            };
            if (Parts is not null)
                tooltipRail = Parts.Apply(PartRail, tooltipRail) with { Key = "asb-rail" };
            var railLayers = new List<Element>(5) { labelsGrid, tooltipRail };
            if (hovering)
            {
                // Ghost thumb at the hover target (PART_VerticalThumbGhost xaml:74-83 — AccentFillColorDisabled,
                // right-aligned, top-anchored).
                var ghost = new BoxEl
                {
                    Key = "asb-ghost",
                    Direction = 0,
                    Justify = FlexJustify.End,
                    OffsetY = Math.Clamp(hoverY, 0f, railHeight - ThumbHeight),
                    HitTestVisible = false,
                    Children =
                    [
                        new BoxEl
                        {
                            Width = ThumbWidth,
                            Height = ThumbHeight,
                            Corners = CornerRadius4.All(ThumbRadius),
                            Fill = Tok.AccentDisabled,
                        },
                    ],
                };
                if (Parts is not null)
                    ghost = Parts.Apply(PartGhost, ghost) with { Key = "asb-ghost" };
                railLayers.Add(ghost);
                if (DetailLabel is not null)
                {
                    // The detail chip (PART_DetailLabelToolTip xaml:46-73): Placement=Top above the pointer,
                    // MaxWidth 360 / MinHeight 40 (:38/:39), BaseTextBlockStyle content (14px SemiBold,
                    // TextBlock_themeresources.xaml:10-18), right-aligned against the tooltip rail.
                    var tip = new BoxEl
                    {
                        Key = "asb-tip",
                        Direction = 0,
                        Justify = FlexJustify.End,
                        OffsetY = MathF.Max(0f, hoverY - TooltipMinHeight - 4f),
                        HitTestVisible = false,
                        Children =
                        [
                            new BoxEl
                            {
                                MaxWidth = TooltipMaxWidth,
                                MinHeight = TooltipMinHeight,
                                Direction = 0,
                                AlignItems = FlexAlign.Center,          // VerticalContentAlignment=Center (xaml:53)
                                Padding = new Edges4(12, 6, 12, 8),     // text Margin 0,0,0,2 folded into the bottom (xaml:60)
                                Corners = Radii.ControlAll,
                                Fill = Tok.AcrylicFlyout.Fallback,
                                BorderColor = Tok.StrokeFlyoutDefault,
                                BorderWidth = 1f,
                                Shadow = Elevation.Tooltip,
                                Children = [new TextEl(DetailLabel(Math.Clamp(hoverY / railHeight, 0f, 1f))) { Size = 14f, Bold = true, Color = Tok.TextPrimary }],
                            },
                        ],
                    };
                    if (Parts is not null)
                        tip = Parts.Apply(PartTip, tip) with { Key = "asb-tip" };
                    railLayers.Add(tip);
                }
            }
            // Live thumb (PART_VerticalThumb xaml:84-92): 30×3 accent @ r1.5, HorizontalAlignment=Right via the
            // Justify=End row wrapper, VerticalAlignment=Top + the position translate as a compositor TransformBind
            // (a position write moves the thumb the same frame — no re-render, no relayout).
            Func<Affine2D> thumbPositionBind = () => Affine2D.Translation(0f, Math.Clamp(Position.Value, 0f, 1f) * (railHeight - ThumbHeight));
            var thumb = new BoxEl
            {
                Key = "asb-thumb",
                Direction = 0,
                Justify = FlexJustify.End,
                HitTestVisible = false,
                TransformBind = thumbPositionBind,
                Children =
                [
                    new BoxEl
                    {
                        Width = ThumbWidth,
                        Height = ThumbHeight,
                        Corners = CornerRadius4.All(ThumbRadius),
                        Fill = Tok.AccentDefault,               // VerticalThumbBrush = AccentFillColorDefault (:11/:21)
                    },
                ],
            };
            // Parts: restyle anything (swap the thumb visual via Children…); the Key and the compositor position
            // bind always win — they ARE the scroll indicator, not style.
            if (Parts is not null)
                thumb = Parts.Apply(PartThumb, thumb) with { Key = "asb-thumb", TransformBind = thumbPositionBind };
            railLayers.Add(thumb);

            var rail = new BoxEl
            {
                ZStack = true,
                Height = railHeight,
                MinWidth = LabelsMinWidth,
                OnPointerDown = interactive ? RailPress : null,   // PART_RootGrid IsTapEnabled → click-to-jump
                OnDrag = interactive ? RailDrag : null,
                OnHoverMove = interactive ? p => setHoverY(p.Y) : null,
                OnPointerExit = interactive ? () => setHoverY(float.NaN) : null,
                Children = railLayers.ToArray(),
            };

            return new BoxEl
            {
                Direction = 1,
                Height = Height,
                MinWidth = LabelsMinWidth,                       // control MinWidth = LabelsGridMinWidth (xaml:4)
                Role = AutomationRole.ScrollBar,
                Children =
                [
                    // Increment on TOP (EDDB), decrement on the BOTTOM (EDDC) — xaml:31-39 / :94-102.
                    ScrollButton(up: true, interactive ? () => Scroll(Position.Peek() - SmallChange, AnnotatedScrollBarScrollKind.IncrementButton) : null),
                    rail,
                    ScrollButton(up: false, interactive ? () => Scroll(Position.Peek() + SmallChange, AnnotatedScrollBarScrollKind.DecrementButton) : null),
                ],
            };
        }

        /// <summary>A repeat-hold scroll button (ScrollButtonStyle): 16×16 (:43-44), right-aligned (xaml:36/:99),
        /// ControlCornerRadius 4 (:49), background TRANSPARENT in every state (:5 + the foreground-only visual
        /// states :56-87), glyph at FontSize 8 (:40) on the TextFillColor Primary→Secondary→Tertiary ramp (:6-8),
        /// disabled = TextFillColorDisabled (:9). IsTabStop=False (xaml:37/:100).</summary>
        private static Element ScrollButton(bool up, Action? onClick)
            => new BoxEl
            {
                Width = ButtonCell,
                Height = ButtonCell,
                AlignSelf = FlexAlign.End,                        // HorizontalAlignment=Right in the root COLUMN (flex, not ZStack)
                Direction = 0,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Corners = Radii.ControlAll,                       // ControlCornerRadius 4 (:49)
                Fill = ColorF.Transparent,                        // ScrollButtonBackground, ALL states (:5, :56-87)
                Repeats = true,
                TabStop = false,                                  // IsTabStop=False (xaml:37/:100)
                OnClick = onClick,
                IsEnabled = onClick is not null,
                Children =
                [
                    new TextEl(up ? "" : "")          // EDDB / EDDC (xaml:38/:101)
                    {
                        Size = ButtonGlyph,                       // ScrollButtonFontSize 8 (:40)
                        FontFamily = Theme.IconFont,
                        Color = onClick is null ? Tok.TextDisabled : Tok.TextPrimary,   // (:9 / :6)
                        HoverColor = Tok.TextSecondary,           // ScrollButtonForegroundPointerOver (:7)
                        PressedColor = Tok.TextTertiary,          // ScrollButtonForegroundPressed (:8)
                    },
                ],
            };
    }
}
