using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>A WinUI AppBarToggleButton: like <see cref="AppBarButton"/> (a vertically-stacked 16px icon glyph
/// over a 12px label) but a stateful toggle. When checked it becomes a SOLID accent pill
/// (<see cref="Tok.AccentDefault"/> fill, white <see cref="Tok.TextOnAccentPrimary"/> icon/label, with
/// <see cref="Tok.AccentSecondary"/>/<see cref="Tok.AccentTertiary"/> hover/press) and gains the accent
/// elevation-border (AppBarToggleButtonBorderBrushChecked); unchecked it is transparent with the standard subtle
/// hover/press states. Press dims the foreground (TextSecondary unchecked, TextOnAccentSecondary checked) to match
/// WinUI via the per-state foreground ramps. Used inside a CommandBar; the whole control is one click target that
/// flips the checked state. <see cref="BoxEl.IsEnabled"/> gates hit-test/focus/keyboard; the disabled resting
/// surface/foreground stay control-chosen.</summary>
public sealed class AppBarToggleButton : Component
{
    public string Glyph = "";
    public string Label = "";
    /// <summary>Controlled checked state (the caller's value signal, frozen at mount); null ⇒ the control materializes
    /// its own internal signal (auto-materialize). A click writes it first, then fires <see cref="OnChange"/>.</summary>
    public Signal<bool>? IsChecked;
    public bool IsEnabled = true;
    /// <summary>Compact layout (the closed CommandBar): icon-only at the 48px compact height
    /// (AppBarThemeCompactHeight, CommandBar_themeresources.xaml:72); FullSize is 64 (AppBarThemeMinHeight :71 —
    /// audit fix: was 48).</summary>
    public bool IsCompact = false;
    public Action<bool>? OnChange;

    public static Element Create(string glyph, string label, Signal<bool>? isChecked = null, Action<bool>? onChange = null,
                                 bool isEnabled = true, bool isCompact = false) =>
        Embed.Comp(() => new AppBarToggleButton
        {
            Glyph = glyph, Label = label, IsChecked = isChecked, OnChange = onChange, IsEnabled = isEnabled,
            IsCompact = isCompact,
        });

    // Frozen-props tripwire (ReuseGuard): Glyph/Label/IsEnabled/IsCompact freeze at mount (InitialChecked is a genuine
    // mount seed — not compared). A reused instance whose scalar caller-data changed is the frozen-props bug.
    public override bool ChecksReuse => ReuseGuard.CompiledIn;
    public override void DebugCheckReuse(Component next)
    {
        if (next is not AppBarToggleButton n) return;
        if (n.Glyph != Glyph) ReuseGuard.ScalarChanged(this, nameof(Glyph));
        else if (n.Label != Label) ReuseGuard.ScalarChanged(this, nameof(Label));
        else if (n.IsEnabled != IsEnabled) ReuseGuard.ScalarChanged(this, nameof(IsEnabled));
        else if (n.IsCompact != IsCompact) ReuseGuard.ScalarChanged(this, nameof(IsCompact));
    }

    public override Element Render()
    {
        var own = UseSignal(false);
        var sig = IsChecked ?? own;   // auto-materialize: one code path (caller's signal wins; else the internal one)
        bool on = sig.Value;          // read the value signal directly; a programmatic write re-skins with no onChange echo
        bool enabled = IsEnabled;
        bool labeled = !IsCompact && Label.Length > 0;

        // Resting fill/foreground stay control-chosen per logical checked state; the engine IsEnabled gate stops
        // hit-test/focus/keyboard and drives the TextEl DisabledColor ramp. WinUI dims the foreground on press
        // (TextSecondary unchecked / TextOnAccentSecondary checked) via a per-state foreground — now carried by the
        // TextEl PressedColor ramp the interactive BoxEl inherits.
        ColorF fg = enabled ? (on ? Tok.TextOnAccentPrimary : Tok.TextPrimary)
                            : (on ? Tok.TextOnAccentDisabled : Tok.TextDisabled);
        ColorF pressedFg = on ? Tok.TextOnAccentSecondary : Tok.TextSecondary;
        ColorF disabledFg = on ? Tok.TextOnAccentDisabled : Tok.TextDisabled;
        ColorF fill = enabled ? (on ? Tok.AccentDefault : Tok.FillSubtleTransparent)
                              : (on ? Tok.AccentDisabled : Tok.FillSubtleTransparent);

        var children = new List<Element>(2)
        {
            new TextEl(Glyph) { Size = 16, Color = fg, PressedColor = pressedFg, DisabledColor = disabledFg, FontFamily = Theme.IconFont },
        };
        if (labeled)
            children.Add(new TextEl(Label) { Size = 12, Color = fg, PressedColor = pressedFg, DisabledColor = disabledFg });

        return new BoxEl
        {
            Direction = 1,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Gap = 4,
            MinWidth = IsCompact ? 48 : 68,
            MinHeight = IsCompact ? 48 : 64,        // AppBarThemeCompactHeight 48 / AppBarThemeMinHeight 64
            Padding = IsCompact ? new Edges4(0, 12, 0, 12) : new Edges4(4, 6, 4, 6),
            Corners = Radii.ControlAll,
            Fill = fill,
            HoverFill = on ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
            PressedFill = on ? Tok.AccentTertiary : Tok.FillSubtleTertiary,
            // The checked↔unchecked fill swap on the LIVE node cross-fades over the WinUI 83ms BrushTransition
            // (CheckedHighlightBackground swap; perf2026 AppBarToggleButton InnerBorder BrushTransition).
            BrushTransitionMs = Motion.ControlFaster,
            HoverDurationMs = Motion.ControlFaster, PressDurationMs = Motion.ControlFaster,
            HoverEasing = Easing.FluentPopOpen, PressEasing = Easing.FluentPopOpen,
            // Checked = accent elevation border (AppBarToggleButtonBorderBrushChecked); unchecked/disabled = transparent.
            BorderWidth = (on && enabled) ? 1f : 0f,
            BorderBrush = (on && enabled) ? Tok.AccentControlElevationBorder : null,
            IsEnabled = enabled,
            Focusable = enabled,
            FocusVisualMargin = Edges4.All(-3f),    // FocusVisualMargin -3 (AppBarButton family templates)
            OnClick = () => { bool next = !on; sig.Value = next; OnChange?.Invoke(next); },
            Role = AutomationRole.ToggleButton,
            Children = children.ToArray(),
        };
    }
}
