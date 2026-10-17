using System;
using System.Collections.Generic;
using System.Text;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using FluentGpu.Theming;
using static FluentGpu.VerticalSlice.Harness.Gate;

namespace FluentGpu.VerticalSlice.Suites;

/// <summary>
/// E14 (EP15): the per-control style table carried by <c>ThemeDefinition.Controls</c> (it replaced <c>Button.StyleHook</c>).
/// Gates: the table is a value (equal tables are equal, a changed table is a non-colour change); the FluentDefault table renders a
/// sample of every kind of control exactly as an explicit table that lists every built-in default does (layout + paint
/// fingerprints of a mounted headless sample, and every control's style record); a definition with another Button style restyles
/// a MOUNTED button on the next frame; restyling several controls and swapping back leaves no stale style behind (nothing is
/// frozen at type load).
/// </summary>
static class ControlStyleTableSuite
{
    public static void Run(StringTable strings)
    {
        var savedDef = Tok.Definition; var savedKind = Tok.Theme;
        try
        {
            TableValueChecks();
            StyleRecordChecks();
            RenderChecks(strings);
            MountedButtonChecks(strings);
        }
        finally { Tok.Use(savedDef, savedKind); }
    }

    // ── the table as a value ────────────────────────────────────────────────────────────────────────────────────────

    static void TableValueChecks()
    {
        var empty = ControlStyleTable.FluentDefault;
        var a = empty.With(ControlStyles.SliderStyle, static () => Slider.FluentStyle() with { TrackHeight = 8f });
        var sameProvider = ControlStyles.FluentExplicit;
        Check("gate.ctl.styletable.default FluentDefault is the empty table: it restyles nothing", empty.Count == 0 && ReferenceEquals(empty, ControlStyleTable.Empty));
        Check("gate.ctl.styletable.with-without With adds/replaces one entry, Without removes it, the original is unchanged",
            a.Count == 1 && a.Contains(ControlStyles.SliderStyle) && !a.Contains(ControlStyles.ToggleSwitchStyle)
            && empty.Count == 0 && a.Without(ControlStyles.SliderStyle).Count == 0 && ReferenceEquals(empty.Without(ControlStyles.SliderStyle), empty));
        Check("gate.ctl.styletable.equality equal entries are equal tables; a different provider is a different table",
            sameProvider.Equals(ControlStyles.FluentExplicit) && !a.Equals(empty) && !a.Equals(sameProvider)
            && empty.With(ControlStyles.SliderStyle, Slider.FluentStyle).Equals(empty.With(ControlStyles.SliderStyle, Slider.FluentStyle)));
        var def = ThemeDefinition.FluentDefault;
        Check("gate.ctl.styletable.non-colour a changed table is a NON-colour difference (applied as a unit), an equal table is not",
            !def.DiffersOnlyInColours(def with { Controls = a }) && def.DiffersOnlyInColours(def with { Controls = ControlStyleTable.Empty }),
            $"changed={def.DiffersOnlyInColours(def with { Controls = a })}");
        Check("gate.ctl.styletable.explicit the explicit Fluent table lists every keyed control", ControlStyles.FluentExplicit.Count == 30, $"count={ControlStyles.FluentExplicit.Count}");
    }

    // ── every control's style record: empty table == explicit built-in table ───────────────────────────────────────────

    static string AllStyles()
    {
        var sb = new StringBuilder();
        foreach (var app in new[] { ButtonAppearance.Standard, ButtonAppearance.Accent, ButtonAppearance.Subtle, ButtonAppearance.Outline })
            foreach (var size in new[] { ControlSize.Small, ControlSize.Medium, ControlSize.Large })
            {
                var s = Button.DefaultStyle(app, size);
                sb.Append(app).Append(size).Append(s).Append(s.BorderBrush?.Stops[0].Color).Append(s.HoverBorderBrush?.Stops[0].Color)
                  .Append(s.PressedBorderBrush?.Stops[0].Color).Append(s.DisabledBorderBrush?.Stops[0].Color).Append('\n');
            }
        sb.Append(AppBarButton.DefaultStyle).Append('\n').Append(CaptionButton.MinMax).Append('\n').Append(CaptionButton.Close).Append('\n')
          .Append(CheckBox.DefaultStyle).Append('\n').Append(ColorPicker.DefaultStyle).Append('\n').Append(HyperlinkButton.DefaultStyle).Append('\n')
          .Append(IconButton.DefaultStyle).Append('\n').Append(NumberBox.DefaultStyle).Append('\n').Append(RadioButton.DefaultStyle).Append('\n')
          .Append(RepeatButton.DefaultStyle).Append('\n').Append(Segmented.DefaultStyle).Append('\n').Append(SettingsCard.DefaultStyle).Append('\n')
          .Append(Slider.DefaultStyle).Append('\n').Append(Splitter.DefaultStyle).Append('\n').Append(ToggleButton.DefaultStyle).Append('\n')
          .Append(ToggleSwitch.DefaultStyle).Append('\n');
        // The overlay / field / status keys (P5): menus, context menu, dialog, combo box, the two field surfaces, info bar, tooltip,
        // ring, bar, and the keys over the existing SelectorBar / SettingsExpander / toast-lane records.
        sb.Append(MenuFlyout.DefaultStyle).Append('\n').Append(ContextMenu.DefaultStyle).Append('\n').Append(ContentDialog.DefaultStyle).Append('\n')
          .Append(ComboBox.DefaultStyle).Append('\n').Append(TextBox.DefaultFieldStyle).Append('\n').Append(AutoSuggestBox.DefaultFieldStyle).Append('\n')
          .Append(InfoBar.DefaultStyle).Append('\n').Append(ToolTip.DefaultStyle).Append('\n').Append(ProgressRing.DefaultStyle).Append('\n')
          .Append(ProgressBar.DefaultStyle).Append('\n').Append(SelectorBar.ThemeStyle).Append('\n').Append(SettingsExpander.DefaultStyle).Append('\n')
          .Append(ControlStyles.Resolve(ControlStyles.ToastLaneStyle, static () => ToastLaneStyle.Default)).Append('\n');
        return sb.ToString();
    }

    static void StyleRecordChecks()
    {
        foreach (var kind in new[] { ThemeKind.Dark, ThemeKind.Light })
        {
            Tok.Use(ThemeDefinition.FluentDefault, kind);
            string fromEmpty = AllStyles();
            Tok.Use(ThemeDefinition.FluentDefault with { Controls = ControlStyles.FluentExplicit }, kind);
            string fromExplicit = AllStyles();
            Check($"gate.ctl.styletable.records-{kind} all 30 keyed style records
 are identical under the empty table and the explicit built-in table",
                fromEmpty == fromExplicit, fromEmpty == fromExplicit ? null : $"first difference at {FirstDiff(fromEmpty, fromExplicit)}");
        }
    }

    // ── rendered samples ────────────────────────────────────────────────────────────────────────────────────────────

    sealed class SamplePanel : Component
    {
        readonly Signal<bool> _on = new(true);
        readonly Signal<bool> _checked = new(true);
        readonly FloatSignal _value = new(0.4f);

        public override Element Render()
        {
            var kids = new List<Element>();
            foreach (var app in new[] { ButtonAppearance.Standard, ButtonAppearance.Accent, ButtonAppearance.Subtle, ButtonAppearance.Outline })
                foreach (var size in new[] { ControlSize.Small, ControlSize.Medium, ControlSize.Large })
                    kids.Add(Button.Create("Button", () => { }, app, size));
            kids.Add(IconButton.Create(Icons.Play, () => { }));
            kids.Add(ToggleSwitch.Create(_on, null, "Toggle"));
            kids.Add(CheckBox.Create("Check", _checked));
            kids.Add(Slider.Create(_value));
            kids.Add(RadioButton.Create("Radio", true));
            kids.Add(ToggleButton.Create("Toggle button", _on));
            kids.Add(HyperlinkButton.Create("Link", () => { }));
            kids.Add(RepeatButton.Create("Repeat", () => { }));
            kids.Add(AppBarButton.Create(Icons.Play, "Play", () => { }));
            return new BoxEl { Direction = 1, Gap = 4f, Padding = Edges4.All(8f), Children = kids.ToArray() };
        }
    }

    static string Fingerprint(AppHost host)
    {
        var sb = new StringBuilder();
        Walk(host.Scene, host.Scene.Root, sb);
        return sb.ToString();
    }

    static void Walk(SceneStore s, NodeHandle n, StringBuilder sb)
    {
        ref readonly RectF b = ref s.Bounds(n);
        ref readonly NodePaint p = ref s.Paint(n);
        sb.Append(b).Append('|').Append(p.VisualKind).Append('|').Append(p.Opacity).Append('|').Append(p.Fill).Append('|').Append(p.HoverFill)
          .Append('|').Append(p.PressedFill).Append('|').Append(p.BorderColor).Append('|').Append(p.HoverBorderColor).Append('|').Append(p.PressedBorderColor)
          .Append('|').Append(p.BorderWidth).Append('|').Append(p.Corners).Append('|').Append(p.TextColor).Append('|').Append(p.TextHoverColor)
          .Append('|').Append(p.TextPressedColor).Append('|').Append(p.TextDisabledColor).Append('|').Append(p.Text).Append('\n');
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c)) Walk(s, c, sb);
    }

    static void Settle(AppHost host)
    {
        for (int i = 0; i < 3; i++) host.RunFrame();
        for (int i = 0; i < 120 && host.Animation.TrackCount > 0; i++)
        {
            double before = host.FrameClockMsForTest;
            host.RunFrame();
            if (host.FrameClockMsForTest == before) host.Paint(0);
        }
    }

    static int FirstDiff(string a, string b)
    {
        int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) if (a[i] != b[i]) return i;
        return n;
    }

    static void RenderChecks(StringTable strings)
    {
        var baseDef = ThemeDefinition.FluentDefault;
        Tok.Use(baseDef, ThemeKind.Dark);
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("control-styles", new Size2(640, 1400), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new SamplePanel());
        Settle(host);
        string original = Fingerprint(host);

        Tok.Use(baseDef with { Controls = ControlStyles.FluentExplicit }, ThemeKind.Dark);
        Settle(host);
        string explicitTable = Fingerprint(host);
        Check("gate.ctl.styletable.render-identical the sample of every control kind renders identically under FluentDefault and the explicit built-in table (layout + paint)",
            original == explicitTable && original.Length > 2000, original == explicitTable ? $"{original.Length} chars" : $"first difference at {FirstDiff(original, explicitTable)}");

        var restyle = ControlStyleTable.Empty
            .With(ControlStyles.ButtonStyle, static (a, s) => s == ControlSize.Medium ? Button.FluentStyle(a, s) with { MinHeight = 44f } : null)
            .With(ControlStyles.SliderStyle, static () => Slider.FluentStyle() with { TrackHeight = 8f })
            .With(ControlStyles.ToggleSwitchStyle, static () => ToggleSwitch.FluentStyle() with { TrackWidth = 52f })
            .With(ControlStyles.CheckBoxStyle, static () => CheckBox.FluentStyle() with { BoxSize = 24f })
            .With(ControlStyles.RadioButtonStyle, static () => RadioButton.FluentStyle() with { RingSize = 24f })
            .With(ControlStyles.IconButtonStyle, static () => IconButton.FluentStyle() with { Size = 44f })
            .With(ControlStyles.ToggleButtonStyle, static () => ToggleButton.FluentStyle() with { MinHeight = 44f });
        Tok.Use(baseDef with { Controls = restyle }, ThemeKind.Dark);
        Settle(host);
        string restyled = Fingerprint(host);
        Check("gate.ctl.styletable.restyle a theme swap restyles the mounted sample (Button, Slider, ToggleSwitch, CheckBox, RadioButton, IconButton, ToggleButton)",
            restyled != original);

        Tok.Use(baseDef, ThemeKind.Dark);
        Settle(host);
        string back = Fingerprint(host);
        Check("gate.ctl.styletable.no-stale swapping back to FluentDefault restores the original sample exactly (no style frozen at type load)",
            back == original, back == original ? null : $"first difference at {FirstDiff(original, back)}");
    }

    // ── a mounted button picks the new style up on the next frame ───────────────────────────────────────────────────

    sealed class OneButton : Component
    {
        public override Element Render() => new BoxEl { Direction = 1, Children = [ Button.Create("Go", () => { }) ] };
    }

    static void MountedButtonChecks(StringTable strings)
    {
        var baseDef = ThemeDefinition.FluentDefault;
        Tok.Use(baseDef, ThemeKind.Dark);
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("control-styles-button", new Size2(320, 200), 1f));
        window.Show();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new OneButton());
        Settle(host);
        var scene = host.Scene;
        NodeHandle Btn() => scene.FirstChild(scene.Root);
        float before = scene.Bounds(Btn()).H;

        var red = ColorF.FromRgba(200, 30, 30);
        var table = ControlStyleTable.Empty.With(ControlStyles.ButtonStyle,
            (a, s) => a == ButtonAppearance.Standard && s == ControlSize.Medium ? new Button.Style { Background = red, MinHeight = 56f } : null);
        Tok.Use(baseDef with { Id = "tall-red-button", Controls = table }, ThemeKind.Dark);
        host.RunFrame();   // the next frame
        float after = scene.Bounds(Btn()).H;
        Check("gate.ctl.styletable.mounted-button a definition with another Button style restyles a MOUNTED button on the next frame",
            Math.Abs(before - 32f) < 0.5f && Math.Abs(after - 56f) < 0.5f, $"height {before} -> {after}");
        Settle(host);
        Check("gate.ctl.styletable.mounted-button-fill the restyled button paints the new fill",
            scene.Paint(Btn()).Fill == red, $"fill={scene.Paint(Btn()).Fill}");

        Tok.Use(baseDef, ThemeKind.Dark);
        Settle(host);
        Check("gate.ctl.styletable.mounted-button-back the old style returns with the old definition",
            Math.Abs(scene.Bounds(Btn()).H - 32f) < 0.5f, $"height={scene.Bounds(Btn()).H}");
    }
}
