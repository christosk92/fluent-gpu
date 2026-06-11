using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.Controls;

/// <summary>How the user interacted with an <see cref="ItemContainer"/> — the WinUI
/// <c>ItemContainerInteractionTrigger</c> subset the engine surfaces (ItemContainer.idl:17-26; PointerPressed/
/// PointerReleased collapse into <see cref="Tap"/> here — see <see cref="ItemContainer.Build"/> remarks).</summary>
public enum ItemContainerTrigger : byte { Tap, DoubleTap, EnterKey, SpaceKey }

/// <summary>
/// WinUI <c>ItemContainer</c> (controls\dev\ItemContainer\ItemContainer.xaml) — the single-item wrapper every modern
/// items control (ItemsView) realizes around its templates: pointer-state plate, dual-stroke selection visual,
/// multi-select checkbox, disabled dimming, focusability. Built as a STATIC factory returning a plain keyed BoxEl
/// tree (no Component, no reactive binds) so a realized window of containers stays on the virtualization recycle
/// fast path (<c>TreeReconciler.IsRecyclable</c>).
///
/// Template mapping (ItemContainer.xaml:11-145, values verified in ItemContainer_themeresources.xaml):
/// • PART_ContainerRoot — the root box: Background = ItemContainerBackground = SubtleFillColorTransparent (:5/:37,
///   transparent both themes); Disabled → Opacity 0.3 (ItemContainerDisabledOpacity, :54).
/// • PART_SelectionVisual (xaml:116-126) — 3px border, BorderBrush = ItemContainerSelectionVisualBackground =
///   AccentFillColorDefault (:14/:46 → Tok.AccentDefault, #60CDFF dark / #005FB8 light), deferred
///   (x:DeferLoadStrategy="Lazy") → mounted only while selected; SelectedPointerOver/Pressed fade it in over
///   ControlFastAnimationDuration with KeySpline 0,0,0,1 (xaml:54-56) → enter-fade 167ms decelerate.
///   Disabled collapses it (xaml:108-110).
/// • PART_CommonVisual (xaml:127-135) — ONE full-stretch rectangle declared AFTER the child placeholder (the child
///   is inserted at index 0, ItemContainer.cpp:66), so it overlays the item content. It carries BOTH faces:
///   – the pointer-state fill (xaml:17-33): PointerOver = ItemContainerPointerOverBackground =
///     SubtleFillColorSecondary (#0FFFFFFF dark :6 / #09000000 light :38); Pressed = ItemContainerPressedBackground
///     = SubtleFillColorTertiary (#0AFFFFFF dark :7 / #06000000 light :39); the Selected* ramp resolves to the SAME
///     brushes (:12-13/:44-45). Drawn ABOVE the content so the tint reads over opaque photo-wall tiles.
///   – the 1px selected inner stroke: Stroke = ItemContainerSelectedInnerBorderBrush = ControlSolidFillColorDefault
///     (:17/:49 → Tok.FillControlSolid, #FF454545 dark / #FFFFFFFF light; Common_themeresources_any.xaml:24/:228),
///     StrokeThickness = ItemContainerSelectedInnerThickness = 1 (:58), selected Margin =
///     ItemContainerSelectedInnerMargin = 2 (:57). At rest the stroke is transparent (ItemContainerBorderBrush, :8).
/// • PART_SelectionCheckbox (xaml:136-144, style :63-139) — top-right (:59-60), Margin 4,−2 (:56), 20px box,
///   unchecked plate = ItemContainerCheckboxBackgroundUnchecked = ControlOnImageFillColorDefault (:18 →
///   Tok.FillControlOnImage, #B31C1C1C dark / #C9FFFFFF light), stroke = CheckBoxCheckBackgroundStrokeUnchecked
///   (→ Tok.StrokeControlStrongDefault); Multiple mode fades it in over ControlFastAnimationDuration (xaml:93-99).
///
/// Interaction mapping (engine-deliberate): selection fires at the PRESS edge via <c>OnPointerPressed</c> — the one
/// pointer callback carrying the modifier chord/click count (WinUI processes selection at PointerReleased; the visual
/// outcome is identical and Win32 lists select on button-down). The container takes NO OnClick so Enter and Space
/// reach <c>OnKeyDown</c> distinctly (the dispatcher's Enter/Space activation would otherwise collapse them), giving
/// WinUI's EnterKey-vs-SpaceKey invoke split (ItemsView.cpp:423-426).
/// </summary>
public static class ItemContainer
{
    public const float DisabledOpacity = 0.3f;          // ItemContainerDisabledOpacity (ItemContainer_themeresources.xaml:54)
    public const float SelectionVisualThickness = 3f;   // PART_SelectionVisual BorderThickness (ItemContainer.xaml:120)
    public const float SelectedInnerThickness = 1f;     // ItemContainerSelectedInnerThickness (themeresources:58)
    public const float SelectedInnerMargin = 2f;        // ItemContainerSelectedInnerMargin (themeresources:57)
    public const float CheckboxSize = 20f;              // CheckBoxSize (CheckBox_themeresources)
    public const float CheckboxGlyphSize = 12f;         // CheckBoxGlyphSize
    public const float FadeMs = 167f;                   // ControlFastAnimationDuration (the KeySpline 0,0,0,1 storyboards)

    /// <summary>Standalone two-state convenience: toggles <paramref name="isSelected"/> on tap/Space (the WinUI
    /// out-of-ItemsView ItemContainer behavior, IsSelected get/set per ItemContainer.idl:69).</summary>
    public static BoxEl Build(Element child, bool isSelected, Action<bool> onSelectionChanged,
                              bool isEnabled = true, CornerRadius4? corners = null)
        => Build(child, isSelected,
                 onInteraction: (t, _) =>
                 {
                     if (t is ItemContainerTrigger.Tap or ItemContainerTrigger.SpaceKey or ItemContainerTrigger.EnterKey)
                         onSelectionChanged(!isSelected);
                 },
                 isEnabled: isEnabled, corners: corners);

    /// <summary>
    /// The full container. <paramref name="onInteraction"/> receives the WinUI interaction trigger + the live modifier
    /// chord — a composing control (ItemsView) routes it through its selector. The returned tree is plain (recyclable);
    /// realize-time closures are the only managed cost. <paramref name="isTabStop"/> is the roving-tab-stop seam:
    /// ItemsView keeps it true on the keyboard-current container ONLY (TabNavigation="Once", ItemsView.xaml:7).
    /// </summary>
    // Per-item chrome customization goes through the composing control's ContainerFactory seam, NOT TemplateParts —
    // per-item part modifiers in recycled scroll paths are an allocation/recycling hazard
    // (docs/guide/control-fidelity.md §6).
    public static BoxEl Build(
        Element child,
        bool isSelected,
        Action<ItemContainerTrigger, KeyModifiers>? onInteraction = null,
        bool isEnabled = true,
        bool showSelectionCheckbox = false,
        bool isChecked = false,
        CornerRadius4? corners = null,
        Action<bool>? onFocusChanged = null,
        float width = float.NaN,
        float height = float.NaN,
        bool isTabStop = true)
    {
        CornerRadius4 outer = corners ?? Radii.ControlAll;   // ControlCornerRadius default (ItemContainer.xaml:7)
        bool selectedVisible = isSelected && isEnabled;       // Disabled collapses PART_SelectionVisual (xaml:108-110)

        // Child count is shape-stable per (selected, enabled, checkbox) state; stable keys keep identity across state
        // flips so the keyed diff inserts/removes exactly the ring/common/checkbox nodes (never morphs the content layer).
        int n = 1 + (selectedVisible ? 1 : 0) + (isEnabled ? 1 : 0) + (showSelectionCheckbox ? 1 : 0);
        var children = new Element[n];
        int w = 0;

        // Content layer (the "Placeholder for child", xaml:115) — fills the container.
        children[w++] = new BoxEl { Key = "ic-content", Children = [child] };

        if (selectedVisible)
        {
            // PART_SelectionVisual: 3px accent ring, full-stretch, fades in over ControlFastAnimationDuration with
            // KeySpline 0,0,0,1 (xaml:54-56 SelectedPointerOver / :73-75 SelectedPressed; SelectedNormal is instant —
            // the enter tween covers both at the WinUI duration).
            children[w++] = new BoxEl
            {
                Key = "ic-ring",
                BorderColor = Tok.AccentDefault,                  // ItemContainerSelectionVisualBackground (:14/:46)
                BorderWidth = SelectionVisualThickness,
                Corners = outer,
                HitTestVisible = false,                           // IsHitTestVisible="False" (xaml:125)
                Animate = new LayoutTransition(
                    TransitionChannels.Opacity,
                    TransitionDynamics.Tween(FadeMs, Easing.FluentDecelerate),
                    Enter: new EnterExit(Opacity: 0f, Active: true)),
            };
        }

        if (isEnabled)
        {
            // PART_CommonVisual (xaml:127-135): the state plane ABOVE the content. The pointer-state fills are
            // DISCRETE keyframes (DiscreteObjectKeyFrame KeyTime=0, xaml:17-33) — duration 0 here both matches that
            // and seeds the interaction-anim row the container's hover/press retargets into this non-hit-test child
            // (the recorder eases the child's own row; the dispatcher propagates the hovered root's state down).
            // Disabled containers never hover/press and collapse their selection chrome, so the plane is
            // enabled-gated (keeps the disabled subtree at the bare content layer).
            children[w++] = new BoxEl
            {
                Key = "ic-common",
                HitTestVisible = false,                           // IsHitTestVisible="False" (xaml:133)
                HoverFill = Tok.FillSubtleSecondary,              // ItemContainerPointerOverBackground (:6/:38) — selected ramp identical (:12/:44)
                PressedFill = Tok.FillSubtleTertiary,             // ItemContainerPressedBackground (:7/:39) — selected ramp identical (:13/:45)
                HoverDurationMs = 0f,                             // DiscreteObjectKeyFrame KeyTime="0" (xaml:18/:28)
                PressDurationMs = 0f,
                // Selected adds the 1px ControlSolid stroke inset 2px (Margin/Stroke keyframes, xaml:47-52;
                // themeresources:57-58). RadiusX/RadiusY bind the TemplatedParent CornerRadius via the corner
                // converters with NO inset shrink (xaml:134-135) — the OUTER radius, unshrunk.
                Margin = selectedVisible ? Edges4.All(SelectedInnerMargin) : default,
                BorderColor = selectedVisible ? Tok.FillControlSolid : default,   // ItemContainerSelectedInnerBorderBrush (:17/:49); rest transparent (:8)
                BorderWidth = selectedVisible ? SelectedInnerThickness : 0f,
                Corners = outer,
            };
        }

        if (showSelectionCheckbox)
        {
            // PART_SelectionCheckbox: top-right (themeresources:59-60), Margin 4,−2 (:56), fades in when Multiple
            // mode activates (xaml:93-99 — opacity 0→1 over ControlFastAnimationDuration, KeySpline 0,0,0,1).
            children[w++] = new BoxEl
            {
                Key = "ic-check",
                Direction = 0,
                Justify = FlexJustify.End,
                AlignItems = FlexAlign.Start,
                HitTestVisible = false,                           // style sets IsHitTestVisible=False (:68)
                Animate = new LayoutTransition(
                    TransitionChannels.Opacity,
                    TransitionDynamics.Tween(FadeMs, Easing.FluentDecelerate),
                    Enter: new EnterExit(Opacity: 0f, Active: true)),
                Children = [BuildCheckPlate(isChecked)],
            };
        }

        return new BoxEl
        {
            ZStack = true,
            Width = width,
            Height = height,
            Corners = outer,
            Fill = Tok.FillSubtleTransparent,        // ItemContainerBackground (:5/:37) — pointer states live on ic-common above
            Opacity = isEnabled ? 1f : DisabledOpacity,   // Disabled storyboard (xaml:104-107)
            IsEnabled = isEnabled,
            // Roving single tab stop (ItemsView.xaml:7 TabNavigation="Once"; the RadioButtons IsTabStop pattern):
            // an explicit TabStop — NOT Focusable — so only the keyboard-current container sits in the tab order;
            // programmatic/keyboard-driven focus (ItemsView.FocusIndex) still lands on any container.
            TabStop = isTabStop && isEnabled,
            FocusVisualMargin = Edges4.All(0f),      // template default margin (no FocusVisualMargin setter in the style)
            Role = AutomationRole.Button,            // ItemContainerAutomationPeer: SelectionItem + Invoke provider
            ClipToBounds = false,
            OnPointerPressed = onInteraction is null ? null : args =>
            {
                // Press-edge selection (see class remarks): click-count 2 = DoubleTap (ItemsView raises ItemInvoked
                // for it when a selection mode is active, ItemsView.cpp:425), else Tap.
                onInteraction(args.ClickCount >= 2 ? ItemContainerTrigger.DoubleTap : ItemContainerTrigger.Tap, args.Mods);
            },
            OnKeyDown = onInteraction is null ? null : args =>
            {
                if (args.KeyCode == Keys.Enter) { onInteraction(ItemContainerTrigger.EnterKey, args.Mods); args.Handled = true; }
                else if (args.KeyCode == Keys.Space && !args.IsRepeat) { onInteraction(ItemContainerTrigger.SpaceKey, args.Mods); args.Handled = true; }
            },
            OnFocusChanged = onFocusChanged,
            Children = children,
        };
    }

    /// <summary>The 20px multi-select check plate. Unchecked = the on-image plate + strong stroke; checked = accent
    /// fill + drawn checkmark (CheckBoxCheckBackgroundFillChecked / CheckBoxCheckGlyphForegroundChecked ladder —
    /// the same Tok ramp CheckBox.cs uses). A plain polyline (no Component) keeps the subtree recyclable.</summary>
    private static BoxEl BuildCheckPlate(bool isChecked)
        => new()
        {
            Width = CheckboxSize,
            Height = CheckboxSize,
            Margin = new Edges4(4f, -2f, 4f, -2f),   // ItemContainerCheckboxMargin = 4,-2 (themeresources:56)
            Corners = Radii.ControlAll,
            BorderWidth = 1f,                        // CheckBoxBorderThickness
            Fill = isChecked ? Tok.AccentDefault : Tok.FillControlOnImage,
            BorderColor = isChecked ? Tok.AccentDefault : Tok.StrokeControlStrongDefault,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            HitTestVisible = false,
            Children = isChecked
                ?
                [
                    // WinUI AnimatedAccept checkmark vertices (the DrawnCheckmark path), drawn static so the
                    // container subtree stays on the recycle fast path.
                    new PolylineStrokeEl
                    {
                        Width = CheckboxGlyphSize + 2f,
                        Height = CheckboxGlyphSize + 2f,
                        P0 = new Point2(0.18f * (CheckboxGlyphSize + 2f), 0.50f * (CheckboxGlyphSize + 2f)),
                        P1 = new Point2(0.42f * (CheckboxGlyphSize + 2f), 0.72f * (CheckboxGlyphSize + 2f)),
                        P2 = new Point2(0.80f * (CheckboxGlyphSize + 2f), 0.26f * (CheckboxGlyphSize + 2f)),
                        PointCount = 3,
                        Color = Tok.TextOnAccentPrimary,
                        Thickness = 1.8f,
                        RoundCaps = true,
                    },
                ]
                : [],
        };
}
