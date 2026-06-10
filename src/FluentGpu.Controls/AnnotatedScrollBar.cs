using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace FluentGpu.Controls;

/// <summary>Why an <see cref="AnnotatedScrollBar"/> scrolled — mirrors WinUI
/// <c>AnnotatedScrollBarScrollingEventKind</c> (AnnotatedScrollBar.idl:6-12).</summary>
public enum AnnotatedScrollBarScrollKind : byte { Click = 0, Drag = 1, IncrementButton = 2, DecrementButton = 3 }

/// <summary>
/// WinUI <c>AnnotatedScrollBar</c> (controls\dev\AnnotatedScrollBar) — the jump-list rail: a column of right-aligned
/// labels beside a thin accent thumb, with CLICK-TO-JUMP on the rail, thumb drag, and repeat-hold increment/decrement
/// buttons. Template facts (AnnotatedScrollBar.xaml + _themeresources.xaml):
/// • Thumb = 30×3 @ r1.5 (ThumbWidth :36 / ThumbHeight :35 / ThumbCornerRadius :41), AccentFillColorDefault
///   (VerticalThumbBrush :11/:21), right-aligned, top-anchored at the scroll position (xaml:84-92).
/// • Ghost thumb = same geometry in AccentFillColorDisabled (PART_VerticalThumbGhost xaml:74-83) — the hover preview
///   of where a rail click would land.
/// • Labels grid min width 44 (LabelsGridMinWidth :37), label text right-aligned (LabelTemplate xaml:8-19).
/// • Increment RepeatButton on TOP with glyph EDDB, decrement on the BOTTOM with EDDC, FontSize 8
///   (ScrollButtonFontSize :40; xaml:31-39/:94-102) — each steps by <c>SmallChange</c>
///   (default ViewportSize / ratio — AnnotatedScrollBar.cpp:484-489 EnsureSmallChangeValue).
/// • Rail click / drag / buttons raise <c>Scrolling</c> with the offset + kind, CANCELABLE
///   (AnnotatedScrollBarScrollingEventArgs.Cancel; cpp:960+ RaiseScrolling) — modeled by the
///   <c>onScrolling</c> filter returning false to cancel.
/// • Detail label on rail hover (PART_DetailLabelToolTip xaml:46-73) — modeled by <c>detailLabel</c>: the engine
///   shows the resolved text in a floating chip at the pointer (the DetailLabelRequested seam).
/// </summary>
public static class AnnotatedScrollBar
{
    private const float RailWidth = 12f;
    private const float ThumbWidth = 30f;     // ThumbWidth (:36)
    private const float ThumbHeight = 3f;     // ThumbHeight (:35)
    private const float ThumbRadius = 1.5f;   // ThumbCornerRadius (:41)
    private const float LabelsMinWidth = 44f; // LabelsGridMinWidth (:37)
    private const float LabelSize = 14f;      // BodyTextBlockStyle (LabelTemplate)
    private const float ButtonGlyph = 8f;     // ScrollButtonFontSize (:40)

    /// <summary>Static/legacy surface (kept source-compatible): annotations as (label, 0..1 position) with no
    /// callbacks — renders the full anatomy inert at position 0.2 (the original demo shape).</summary>
    public static Element Create(IReadOnlyList<(string Label, float Position01)> annotations, float height = 280f)
        => Create(annotations, 0.2f, onScroll: null, height: height);

    /// <summary>
    /// The interactive control. <paramref name="position01"/> is the normalized scroll position (0..1);
    /// <paramref name="onScroll"/> receives every user scroll (new position, kind); <paramref name="onScrolling"/>
    /// (optional) may return false to CANCEL a scroll (the WinUI Scrolling.Cancel seam);
    /// <paramref name="smallChange01"/> is the button step (default 0.05 — the SmallChange auto-value stands in for
    /// viewport/ratio, cpp:484-489); <paramref name="detailLabel"/> resolves the hover chip text from a position.
    /// </summary>
    public static Element Create(IReadOnlyList<(string Label, float Position01)> annotations,
                                 float position01,
                                 Action<float, AnnotatedScrollBarScrollKind>? onScroll,
                                 float height = 280f,
                                 float smallChange01 = 0.05f,
                                 Func<float, bool>? onScrolling = null,
                                 Func<float, string>? detailLabel = null)
        => Embed.Comp(() => new AnnotatedScrollBarComponent
        {
            Annotations = annotations ?? [],
            Position = Math.Clamp(position01, 0f, 1f),
            OnScroll = onScroll,
            OnScrolling = onScrolling,
            DetailLabel = detailLabel,
            Height = height,
            SmallChange = smallChange01,
        });

    internal sealed class AnnotatedScrollBarComponent : Component
    {
        public IReadOnlyList<(string Label, float Position01)> Annotations = [];
        public float Position;
        public Action<float, AnnotatedScrollBarScrollKind>? OnScroll;
        public Func<float, bool>? OnScrolling;
        public Func<float, string>? DetailLabel;
        public float Height = 280f;
        public float SmallChange = 0.05f;

        public override Element Render()
        {
            var (hoverY, setHoverY) = UseState(float.NaN);   // rail hover → ghost thumb + detail chip

            float railHeight = MathF.Max(1f, Height - 2f * RailWidth);   // minus the two button cells
            float pos = Math.Clamp(Position, 0f, 1f);

            void Scroll(float to, AnnotatedScrollBarScrollKind kind)
            {
                to = Math.Clamp(to, 0f, 1f);
                if (OnScrolling is not null && !OnScrolling(to)) return;   // Scrolling.Cancel (idl:27)
                OnScroll?.Invoke(to, kind);
            }

            void RailPress(Point2 p) => Scroll(p.Y / railHeight, AnnotatedScrollBarScrollKind.Click);
            void RailDrag(Point2 p) => Scroll(p.Y / railHeight, AnnotatedScrollBarScrollKind.Drag);

            // Annotation labels, absolutely placed via OffsetY; right-aligned per the LabelTemplate.
            var labels = new Element[Annotations.Count];
            for (int i = 0; i < Annotations.Count; i++)
            {
                var (label, p01) = Annotations[i];
                float p = Math.Clamp(p01, 0f, 1f);
                labels[i] = new BoxEl
                {
                    OffsetY = p * railHeight,
                    Direction = 0,
                    Justify = FlexJustify.End,
                    HitTestVisible = false,
                    Children = [new TextEl(label ?? "") { Size = LabelSize, Color = Tok.TextSecondary }],
                };
            }

            bool interactive = OnScroll is not null;
            bool hovering = interactive && !float.IsNaN(hoverY);

            var railLayers = new List<Element>(4)
            {
                // Labels fill the body, right-aligned against the rail (LabelsGrid).
                new BoxEl { Direction = 1, ZStack = true, MinWidth = LabelsMinWidth, Height = railHeight, Children = labels },
            };
            if (hovering)
            {
                // Ghost thumb at the hover target (PART_VerticalThumbGhost — AccentFillColorDisabled).
                railLayers.Add(new BoxEl
                {
                    Width = ThumbWidth,
                    Height = ThumbHeight,
                    Corners = CornerRadius4.All(ThumbRadius),
                    Fill = Tok.AccentDisabled,
                    OffsetY = Math.Clamp(hoverY, 0f, railHeight - ThumbHeight),
                    AlignSelf = FlexAlign.End,
                    HitTestVisible = false,
                });
                if (DetailLabel is not null)
                {
                    // The detail chip (the DetailLabelToolTip seam): resolved text floated at the hover position.
                    railLayers.Add(new BoxEl
                    {
                        OffsetY = MathF.Max(0f, hoverY - 24f),
                        Direction = 0,
                        Justify = FlexJustify.End,
                        HitTestVisible = false,
                        Children =
                        [
                            new BoxEl
                            {
                                Padding = new Edges4(8, 4, 8, 4),
                                Corners = Radii.ControlAll,
                                Fill = Tok.AcrylicFlyout.Fallback,
                                BorderColor = Tok.StrokeFlyoutDefault,
                                BorderWidth = 1f,
                                Shadow = Elevation.Tooltip,
                                Children = [new TextEl(DetailLabel(Math.Clamp(hoverY / railHeight, 0f, 1f))) { Size = 12f, Color = Tok.TextPrimary }],
                            },
                        ],
                    });
                }
            }
            // Live thumb (30×3 accent @ r1.5, right-aligned, top-anchored at the position).
            railLayers.Add(new BoxEl
            {
                Width = ThumbWidth,
                Height = ThumbHeight,
                Corners = CornerRadius4.All(ThumbRadius),
                Fill = Tok.AccentDefault,                       // VerticalThumbBrush (:11/:21)
                OffsetY = pos * (railHeight - ThumbHeight),
                AlignSelf = FlexAlign.End,
                HitTestVisible = false,
            });

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
                MinWidth = LabelsMinWidth,
                Role = AutomationRole.ScrollBar,
                AlignItems = FlexAlign.Stretch,
                Children =
                [
                    // Increment on TOP (EDDB), decrement on the BOTTOM (EDDC) — xaml:31-39 / :94-102.
                    ScrollButton(up: true, interactive ? () => Scroll(Position - SmallChange, AnnotatedScrollBarScrollKind.IncrementButton) : null),
                    rail,
                    ScrollButton(up: false, interactive ? () => Scroll(Position + SmallChange, AnnotatedScrollBarScrollKind.DecrementButton) : null),
                ],
            };
        }

        /// <summary>A repeat-hold scroll button (ScrollButtonStyle): subtle fills, glyph at FontSize 8.</summary>
        private static Element ScrollButton(bool up, Action? onClick)
            => new BoxEl
            {
                Height = RailWidth,
                Direction = 0,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.End,
                Padding = new Edges4(0, 0, ThumbWidth * 0.5f - ButtonGlyph * 0.5f, 0),
                Corners = Radii.ControlAll,
                HoverFill = Tok.FillSubtleSecondary,
                PressedFill = Tok.FillSubtleTertiary,
                Repeats = true,
                TabStop = false,                                  // IsTabStop=False (xaml:37/:100)
                OnClick = onClick,
                IsEnabled = onClick is not null,
                Children =
                [
                    new TextEl(up ? "" : "")      // EDDB / EDDC (xaml:38/:101)
                    {
                        Size = ButtonGlyph,
                        FontFamily = Theme.IconFont,
                        Color = Tok.TextSecondary,
                    },
                ],
            };
    }
}
