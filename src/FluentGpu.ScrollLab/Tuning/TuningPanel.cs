using System;
using System.Collections.Generic;
using System.Globalization;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.ScrollLab.Lab;
using FluentGpu.Scroll.Diag;
using FluentGpu.Scroll.Motion;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Dialogs;
using static FluentGpu.Dsl.Ui;

namespace FluentGpu.ScrollLab.Tuning;

/// <summary>
/// The tuning panel (scroll-lab plan §3 Tuning), mounted BOTH in the detached tuning window and in the main window's
/// Ctrl+T pane — both instances edit the one <see cref="LabState.Feel"/>, so they never disagree:
/// <code>
/// Preset [Standard ▾]  name [my-feel] [Save as] [Import JSON…] [Export JSON…] [Reset to preset]
/// ── MotionFeel ── one TunableRow per ScrollTunables.All: name · slider · number box · preset value · ★
/// ── Δ vs Standard ── WheelNotchDip 64 → 56 …
/// </code>
/// Every edit is live on the next notch/lift (<see cref="LabState.ApplyFeel"/> → <see cref="ScrollTunables.Apply"/> +
/// a TunableChanged mark).
/// </summary>
public sealed class TuningPanel : Component
{
    public override Element Render()
    {
        var presetsVersion = UseSignal(0);
        int version = presetsVersion.Value;          // a save re-renders (and re-keys the picker)
        var presets = FeelStore.List();
        var names = new string[presets.Count];
        for (int i = 0; i < names.Length; i++) names[i] = presets[i].BuiltIn ? presets[i].Name : presets[i].Name + " (saved)";
        var selected = UseSignal(0);
        var saveName = UseSignal("my-feel");

        // The picker follows the base preset however it changed (the other panel instance, Save as).
        UseEffect(() =>
        {
            string name = LabState.PresetName.Value;
            var list = FeelStore.List();
            for (int i = 0; i < list.Count; i++)
                if (list[i].Name == name) { if (selected.Peek() != i) selected.Value = i; break; }
        });

        var rows = new List<Element>(ScrollTunables.All.Count);
        foreach (var t in ScrollTunables.All)
        {
            var tunable = t;
            rows.Add(Embed.Comp(() => new TunableRow { T = tunable }) with { Key = "t-" + tunable.Name });
        }

        return new BoxEl
        {
            Direction = 1,
            Gap = 10f,
            Padding = new Edges4(16f, 12f, 16f, 16f),
            Children =
            [
                new TextEl("Tuning") { Size = 16f, Weight = 600 },
                new BoxEl
                {
                    Direction = 0,
                    Gap = 8f,
                    AlignItems = FlexAlign.Center,
                    Children =
                    [
                        new TextEl("Preset") { Size = 13f, Color = Tok.TextSecondary },
                        new BoxEl
                        {
                            Children =
                            [
                                ComboBox.Create(names, selected, width: 240f, onChange: i =>
                                {
                                    if ((uint)i < (uint)presets.Count) LabState.ApplyPreset(presets[i].Name, presets[i].Feel);
                                }) with { Key = "presets-" + version },
                            ],
                        },
                        Button.Create("Reset to preset", () =>
                        {
                            string name = LabState.PresetName.Peek();
                            if (FeelStore.PresetFeel(name) is { } f) LabState.ApplyPreset(name, f);
                        }, ButtonAppearance.Subtle, ControlSize.Small),
                    ],
                },
                new BoxEl
                {
                    Direction = 0,
                    Gap = 8f,
                    AlignItems = FlexAlign.Center,
                    Children =
                    [
                        TextBox.Create(saveName, options: new TextBox.TextBoxOptions { Width = 180f, Placeholder = "preset name" }),
                        Button.Create("Save as", () =>
                        {
                            try
                            {
                                string stored = FeelStore.SaveAs(saveName.Peek(), LabState.Feel.Peek());
                                LabState.PresetName.Value = stored;
                                presetsVersion.Value = presetsVersion.Peek() + 1;
                                LabState.Status.Value = "Saved preset " + stored;
                            }
                            catch (Exception ex) { LabState.Status.Value = "Save failed: " + ex.Message; }
                        }, ButtonAppearance.Standard, ControlSize.Small),
                        Button.Create("Import JSON…", Import, ButtonAppearance.Standard, ControlSize.Small),
                        Button.Create("Export JSON…", Export, ButtonAppearance.Standard, ControlSize.Small),
                    ],
                },
                Embed.Comp(() => new DiffList()),
                new BoxEl
                {
                    Direction = 0,
                    Gap = 8f,
                    Padding = new Edges4(0f, 6f, 0f, 0f),
                    Children =
                    [
                        new TextEl("Knob") { Size = 12f, Color = Tok.TextSecondary, Width = TunableRow.NameWidth },
                        new TextEl("Value") { Size = 12f, Color = Tok.TextSecondary, Width = TunableRow.SliderWidth + 8f + TunableRow.BoxWidth },
                        new TextEl("Preset") { Size = 12f, Color = Tok.TextSecondary, Width = TunableRow.PresetWidth },
                    ],
                },
                new BoxEl { Direction = 1, Gap = 2f, Children = rows.ToArray() },
            ],
        };
    }

    static void Import()
    {
        try
        {
            string? path = FilePicker.OpenFile(FluentApp.WindowHandle, "Import feel JSON", ("Feel JSON", "*.json"));
            if (path is null) return;
            if (FeelStore.TryImport(path, out var feel))
            {
                LabState.ApplyFeel(feel);
                LabState.Status.Value = "Imported " + System.IO.Path.GetFileName(path) + " (base preset unchanged — see the diff)";
            }
            else LabState.Status.Value = "Not a feel JSON: " + System.IO.Path.GetFileName(path);
        }
        catch (Exception ex) { LabState.Status.Value = "Import failed: " + ex.Message; }
    }

    static void Export()
    {
        try
        {
            string? path = FilePicker.SaveFile(FluentApp.WindowHandle, "Export feel JSON", "feel.json", ("Feel JSON", "*.json"));
            if (path is null) return;
            FeelStore.Export(path, LabState.Feel.Peek());
            LabState.Status.Value = "Exported " + System.IO.Path.GetFileName(path);
        }
        catch (Exception ex) { LabState.Status.Value = "Export failed: " + ex.Message; }
    }

    /// <summary>The fields that differ from the base preset ("Δ vs preset").</summary>
    sealed class DiffList : Component
    {
        public override Element Render()
        {
            var feel = LabState.Feel.Value;
            string preset = LabState.PresetName.Value;
            var baseFeel = FeelStore.PresetFeel(preset) ?? FeelProfiles.Standard;
            var lines = new List<Element>();
            foreach (var t in ScrollTunables.All)
            {
                double a = t.Get(baseFeel), b = t.Get(feel);
                if (!TunableRow.Differs(t, a, b)) continue;
                lines.Add(new TextEl(t.Name + "  " + TunableRow.Format(a) + " → " + TunableRow.Format(b)) { Size = 12f, Color = Tok.AccentTextPrimary });
            }
            return new BoxEl
            {
                Direction = 1,
                Gap = 2f,
                Padding = new Edges4(10f, 8f, 10f, 8f),
                Corners = CornerRadius4.All(6f),
                Fill = Tok.FillSubtleSecondary,
                Children =
                [
                    new TextEl("Δ vs " + preset + " (" + lines.Count.ToString(CultureInfo.InvariantCulture) + ")") { Size = 12f, Weight = 600 },
                    .. lines,
                ],
            };
        }
    }
}

/// <summary>
/// One <see cref="TunableF"/> row: name · slider (the tunable's range) · number box · the base preset's value · ★ when
/// changed. <see cref="T"/> is mount-frozen (one row per tunable, keyed by name); the displayed value is synced FROM
/// <see cref="LabState.Feel"/> in an effect (a preset pick, an import or the other panel instance), and user edits
/// write it back through <see cref="LabState.ApplyFeel"/> — live on the next notch.
/// </summary>
public sealed class TunableRow : Component
{
    public const float NameWidth = 170f;
    public const float SliderWidth = 170f;
    public const float BoxWidth = 96f;
    public const float PresetWidth = 70f;

    public TunableF T;

    public static string Format(double v) => v.ToString("0.####", CultureInfo.InvariantCulture);

    /// <summary>Equal up to a millionth of the tunable's range (a float slider round-trip is not an edit).</summary>
    public static bool Differs(in TunableF t, double a, double b) => Math.Abs(a - b) > (t.Max - t.Min) * 1e-6;

    public override Element Render()
    {
        var t = T;
        double initial = t.Get(LabState.Feel.Peek());
        var slider = UseFloatSignal((float)initial);
        var box = UseSignal(initial);

        UseEffect(() =>
        {
            double x = t.Get(LabState.Feel.Value);
            if (Differs(t, slider.Peek(), x)) slider.Value = (float)x;
            if (box.Peek() != x) box.Value = x;
        });

        return new BoxEl
        {
            Direction = 0,
            Gap = 8f,
            AlignItems = FlexAlign.Center,
            Children =
            [
                new TextEl(t.Name) { Size = 12f, Width = NameWidth, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                Slider.Create(slider, v => Edit(t, v), new Slider.SliderOptions
                {
                    Min = (float)t.Min,
                    Max = (float)t.Max,
                    IsThumbToolTipEnabled = false,
                    SmallChange = (float)((t.Max - t.Min) / 100.0),
                    LargeChange = (float)((t.Max - t.Min) / 10.0),
                }, length: SliderWidth),
                NumberBox.Create(box, v => Edit(t, v), new NumberBox.NumberBoxOptions
                {
                    Minimum = t.Min,
                    Maximum = t.Max,
                    SmallChange = (t.Max - t.Min) / 100.0,
                    LargeChange = (t.Max - t.Min) / 10.0,
                    Width = BoxWidth,
                    Formatter = Format,
                }),
                new TextEl(Prop.Of(() => FeelStore.PresetFeel(LabState.PresetName.Value) is { } p ? Format(t.Get(p)) : "—"))
                {
                    Size = 12f, Color = Tok.TextSecondary, Width = PresetWidth,
                },
                new TextEl(Prop.Of(() =>
                {
                    var feel = LabState.Feel.Value;
                    return FeelStore.PresetFeel(LabState.PresetName.Value) is { } p && Differs(t, t.Get(p), t.Get(feel)) ? "★" : "";
                }))
                {
                    Size = 14f, Color = Tok.AccentTextPrimary, Width = 16f,
                },
            ],
        };
    }

    static void Edit(in TunableF t, double v)
    {
        if (double.IsNaN(v)) return;
        var current = LabState.Feel.Peek();
        v = Math.Clamp(v, t.Min, t.Max);
        if (!Differs(t, t.Get(current), v)) return;
        LabState.ApplyFeel(t.With(current, v));
    }
}

/// <summary>The detached tuning window's root: a custom title bar (the window is borderless — its caption region is
/// the drag handle) over the scrolling panel.</summary>
public sealed class TuningWindowRoot : Component
{
    public override Element Render() => new BoxEl
    {
        Direction = 1,
        Grow = 1f,
        Fill = Tok.FillSolidBase,
        Children =
        [
            TitleBar.Create(new TitleBarOptions { Title = "Scroll Lab — Tuning" }),
            new BoxEl
            {
                Direction = 1,
                Grow = 1f,
                Basis = 0f,
                MinHeight = 0f,
                Children = [ScrollView(Embed.Comp(() => new TuningPanel()))],
            },
        ],
    };
}
