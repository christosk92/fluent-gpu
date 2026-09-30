using System;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.ScrollLab.Analysis;
using FluentGpu.ScrollLab.Record;
using FluentGpu.ScrollLab.Surfaces;
using FluentGpu.ScrollLab.Tuning;
using FluentGpu.Scroll.Runtime;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace FluentGpu.ScrollLab.Lab;

/// <summary>
/// The lab's main window (scroll-lab plan §3):
/// <code>
/// ┌ Scroll Lab  [Surface|Record|Analysis]                 ● REC  [Felt wrong F8][Record F10][Tuning Ctrl+T][Tuning window] ┐
/// │ [Fixed 100k|Measured|Edge cases]                                                                                        │
/// │ ┌ surface (the active ScrollHandle) ──────────────────────────────┐ ┌ HUD ──────────┐ ┌ tuning pane (Ctrl+T) ───────┐ │
/// │ │ rows … barcode ▌                                                 │ │ offset / v    │ │ preset, rows, diff          │ │
/// │ └──────────────────────────────────────────────────────────────────┘ └───────────────┘ └─────────────────────────────┘ │
/// │ F8 felt wrong   F10 record/stop   Ctrl+T tuning pane                                              status line          │
/// └─────────────────────────────────────────────────────────────────────────────────────────────────────────────────────────┘
/// </code>
/// Pages stay mounted (KeepAlive) so the surface keeps its handle bound and its position while Record/Analysis show.
/// </summary>
public sealed class LabShell : Component
{
    static readonly string[] PageLabels = { "Surface", "Record", "Analysis" };

    public override Element Render()
    {
        var hooks = UseContext(InputHooks.Current);
        return new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            Children =
            [
                Toolbar(hooks),
                new BoxEl
                {
                    Direction = 1,
                    Grow = 1f,
                    Basis = 0f,
                    MinHeight = 0f,
                    IsolateLayout = true,
                    ClipToBounds = true,
                    Children =
                    [
                        Flow.KeepAlive(() => LabState.Page.Value, static p => "page-" + p, static p => p switch
                        {
                            (int)LabPage.Record => Embed.Comp(() => new RecordPage()),
                            (int)LabPage.Analysis => Embed.Comp(() => new AnalysisPage()),
                            _ => Embed.Comp(() => new SurfacePage()),
                        }),
                    ],
                },
                Footer(),
            ],
        };
    }

    static Element Toolbar(InputHooks hooks) => new BoxEl
    {
        Direction = 0,
        Gap = 12f,
        Padding = new Edges4(16f, 10f, 16f, 10f),
        AlignItems = FlexAlign.Center,
        Children =
        [
            new TextEl("Scroll Lab") { Size = 18f, Weight = 600 },
            SelectorBar.Create(PageLabels, LabState.Page),
            Spacer(),
            new BoxEl
            {
                Visible = Prop.Of(() => LabState.Recording.Value),
                Padding = new Edges4(10f, 4f, 10f, 4f),
                Corners = CornerRadius4.All(10f),
                Fill = Tok.SystemFillCriticalBackground,
                Children = [new TextEl("● REC") { Size = 12f, Weight = 600, Color = Tok.SystemFillCritical }],
            },
            Hotkeys.Button("Felt wrong (F8)", LabState.MarkFelt, Hotkeys.FeltWrong),
            Embed.Comp(() => new RecordButton()),
            Hotkeys.Button("Tuning pane (Ctrl+T)", Hotkeys.ToggleTuningPane, Hotkeys.TuningPane),
            Hotkeys.Button("Tuning window (Ctrl+Shift+T)", () => LabState.OpenTuningWindow(hooks), Hotkeys.TuningWindow, ButtonAppearance.Subtle),
        ],
    };

    static Element Footer() => new BoxEl
    {
        Direction = 0,
        Gap = 16f,
        Padding = new Edges4(16f, 6f, 16f, 8f),
        AlignItems = FlexAlign.Center,
        Children =
        [
            new TextEl(Hotkeys.Hint) { Size = 12f, Color = Tok.TextSecondary },
            Spacer(),
            new TextEl(LabState.Status) { Size = 12f, Color = Tok.TextSecondary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
        ],
    };

    /// <summary>The F10 button: its label follows the recorder.</summary>
    sealed class RecordButton : Component
    {
        public override Element Render()
        {
            bool rec = LabState.Recording.Value;
            return Hotkeys.Button(rec ? "Stop (F10)" : "Record (F10)", LabState.ToggleRecording, Hotkeys.Record,
                rec ? ButtonAppearance.Accent : ButtonAppearance.Standard);
        }
    }
}

/// <summary>The Surface page: surface picker + the active surface, the HUD, and the collapsible tuning pane.</summary>
public sealed class SurfacePage : Component
{
    static readonly string[] SurfaceLabels = BuildLabels();

    static string[] BuildLabels()
    {
        var all = ScrollSurfaces.All;
        var labels = new string[all.Count];
        for (int i = 0; i < labels.Length; i++) labels[i] = all[i].Name;
        return labels;
    }

    public override Element Render() => new BoxEl
    {
        Direction = 0,
        Grow = 1f,
        Gap = 12f,
        Padding = new Edges4(16f, 0f, 16f, 0f),
        Children =
        [
            new BoxEl
            {
                Direction = 1,
                Grow = 1f,
                Basis = 0f,
                MinWidth = 0f,
                Gap = 8f,
                Children =
                [
                    SelectorBar.Create(SurfaceLabels, LabState.Surface),
                    new BoxEl
                    {
                        Direction = 1,
                        Grow = 1f,
                        Basis = 0f,
                        MinHeight = 0f,
                        Corners = CornerRadius4.All(8f),
                        BorderColor = Tok.StrokeCardDefault,
                        BorderWidth = 1f,
                        ClipToBounds = true,
                        IsolateLayout = true,
                        Children = [Embed.Comp(() => new SurfaceArea())],
                    },
                ],
            },
            Embed.Comp(() => new HudPanel()),
            new BoxEl
            {
                Visible = Prop.Of(() => LabState.TuningPaneOpen.Value),
                Direction = 1,
                Width = 560f,
                Shrink = 0f,
                Corners = CornerRadius4.All(8f),
                BorderColor = Tok.StrokeCardDefault,
                BorderWidth = 1f,
                Fill = Tok.FillCardDefault,
                ClipToBounds = true,
                Children = [ScrollView(Embed.Comp(() => new TuningPanel()))],
            },
        ],
    };
}

/// <summary>Remounts the chosen surface (keyed) with a FRESH handle each time the picker changes.</summary>
public sealed class SurfaceArea : Component
{
    public override Element Render()
    {
        int kind = LabState.Surface.Value;
        return new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            MinHeight = 0f,
            Children = [Embed.Comp(() => new SurfaceHost { Kind = (ScrollSurfaceKind)kind }) with { Key = "surface-" + kind }],
        };
    }
}

/// <summary>Owns one surface's <see cref="ScrollHandle"/> and publishes it as <see cref="LabState.ActiveHandle"/> for the
/// HUD, the recorder and the probe viewport filter. <see cref="Kind"/> is mount-frozen: a new kind is a new key.</summary>
public sealed class SurfaceHost : Component
{
    public ScrollSurfaceKind Kind;

    public override Element Render()
    {
        var handleRef = UseRef<ScrollHandle?>(null);
        handleRef.Value ??= new ScrollHandle();
        var handle = handleRef.Value;
        UseEffect(() =>
        {
            LabState.ActiveHandle.Value = handle;
            return () =>
            {
                if (ReferenceEquals(LabState.ActiveHandle.Peek(), handle)) LabState.ActiveHandle.Value = null;
            };
        });
        return ScrollSurfaces.Create(Kind, handle);
    }
}

/// <summary>The live HUD: every readout is a bound text over the <see cref="LabState"/> HUD signals (which
/// <see cref="LabHost"/> throttles), so the panel itself renders once.</summary>
public sealed class HudPanel : Component
{
    static string F(double v, string format) => double.IsNaN(v) ? "—" : v.ToString(format, CultureInfo.InvariantCulture);

    static Element Row(string label, Func<string> value) => new BoxEl
    {
        Direction = 0,
        Gap = 8f,
        AlignItems = FlexAlign.Center,
        Children =
        [
            new TextEl(label) { Size = 12f, Color = Tok.TextSecondary, Width = 110f },
            new TextEl(Prop.Of(value)) { Size = 13f, Weight = 600 },
        ],
    };

    public override Element Render() => new BoxEl
    {
        Direction = 1,
        Width = 260f,
        Shrink = 0f,
        Gap = 8f,
        Padding = new Edges4(14f, 12f, 14f, 12f),
        Corners = CornerRadius4.All(8f),
        Fill = Tok.FillCardDefault,
        BorderColor = Tok.StrokeCardDefault,
        BorderWidth = 1f,
        AlignSelf = FlexAlign.Start,
        Children =
        [
            new TextEl("HUD") { Size = 14f, Weight = 600 },
            Row("offset", () => F(LabState.HudOffset.Value, "#,0.0") + " DIP"),
            Row("velocity", () => F(LabState.HudVelocity.Value, "#,0") + " DIP/s"),
            Row("motion", () => LabState.HudKind.Value.ToString()),
            Row("notch→pose", () => F(LabState.HudLagMs.Value, "0.0") + " ms"),
            Row("DIP / notch", () => F(LabState.HudDipPerNotch.Value, "0.0") + " DIP"),
            Row("notches", () => LabState.HudNotches.Value.ToString(CultureInfo.InvariantCulture)),
            Row("coverage clamps", () => LabState.HudClamps.Value.ToString(CultureInfo.InvariantCulture)),
            Row("lost rows", () => LabState.HudLostRows.Value.ToString(CultureInfo.InvariantCulture)),
            Row("feel", () =>
            {
                string preset = LabState.PresetName.Value;
                var feel = LabState.Feel.Value;
                return FeelStore.PresetFeel(preset) is { } baseFeel && baseFeel == feel ? preset : preset + " ★ edited";
            }),
            new TextEl("pose strip (|Δp| per frame)") { Size = 12f, Color = Tok.TextSecondary },
            Embed.Comp(() => new PoseStrip()),
            ToggleSwitch.Create(LabState.GpuPasses, LabHost.SetGpuPasses, header: "GPU passes"),
            new TextEl(LabState.HudGpuPasses) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxLines = 4 },
        ],
    };

    sealed class PoseStrip : Component
    {
        static readonly SparkBars.Style StripStyle = SparkBars.DefaultStyle with { Height = 48f, Gap = 1f, MinBar = 1f };

        public override Element Render()
        {
            var model = LabState.HudPoseStrip.Value;
            return new BoxEl
            {
                Direction = 0,
                Height = 48f,
                Children = model.Bars.Length == 0
                    ? [new TextEl("scroll to fill") { Size = 12f, Color = Tok.TextTertiary }]
                    : [SparkBars.Create(model, StripStyle)],
            };
        }
    }
}
