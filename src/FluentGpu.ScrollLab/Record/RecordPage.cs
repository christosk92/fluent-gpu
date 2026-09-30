using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.ScrollLab.Lab;
using FluentGpu.ScrollLab.Surfaces;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace FluentGpu.ScrollLab.Record;

/// <summary>
/// The Record page (scroll-lab plan §3 Record/Sessions): name + probe level + start/stop (the same toggle as F10 —
/// sessions record whatever the Surface page's active surface does), and the saved sessions, newest first, each with
/// Analyze and Open dir. Replay / Promote to fixture arrive with phases 2–3.
/// </summary>
public sealed class RecordPage : Component
{
    static readonly string[] Levels = { "Summary (notches, poses, presents)", "Trace (every stage)" };

    public override Element Render()
    {
        int version = LabState.SessionsVersion.Value;
        var sessions = SessionWriter.List(100);
        var rows = new Element[sessions.Count];
        for (int i = 0; i < sessions.Count; i++)
        {
            var info = sessions[i];
            rows[i] = new BoxEl
            {
                Key = info.Folder,
                Direction = 0,
                Gap = 8f,
                Padding = new Edges4(10f, 6f, 10f, 6f),
                AlignItems = FlexAlign.Center,
                Corners = CornerRadius4.All(6f),
                HoverFill = Tok.FillSubtleSecondary,
                Children =
                [
                    new TextEl(info.Name) { Size = 13f, Grow = 1f, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new TextEl(info.Written.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)) { Size = 12f, Color = Tok.TextSecondary },
                    Controls.Button.Create("Analyze", () => LabState.AnalyzeFolder(info.Folder), ButtonAppearance.Standard, ControlSize.Small),
                    Controls.Button.Create("Open dir", () => OpenFolder(info.Folder), ButtonAppearance.Subtle, ControlSize.Small),
                ],
            };
        }

        return new BoxEl
        {
            Direction = 1,
            Grow = 1f,
            Gap = 12f,
            Padding = new Edges4(24f, 8f, 24f, 16f),
            Children =
            [
                new TextEl("Record a session") { Size = 16f, Weight = 600 },
                new TextEl("Recording captures the active surface on the Surface page: switch there, press F10, scroll, press F10 again. F8 marks the moments that felt wrong.")
                {
                    Size = 13f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap,
                },
                new BoxEl
                {
                    Direction = 0,
                    Gap = 12f,
                    AlignItems = FlexAlign.End,
                    Children =
                    [
                        TextBox.Create(LabState.SessionName, options: new TextBox.TextBoxOptions { Width = 220f, Header = "Session name" }),
                        ComboBox.Create(Levels, LabState.RecordLevel, width: 260f, header: "Probe level"),
                        Embed.Comp(() => new StartStop()),
                    ],
                },
                new TextEl(Prop.Of(() => "Surface: " + ScrollSurfaces.NameOf((ScrollSurfaceKind)LabState.Surface.Value)
                                         + "   ·   sessions folder: " + SessionWriter.SessionsDir))
                {
                    Size = 12f, Color = Tok.TextSecondary,
                },
                new TextEl("Sessions (" + sessions.Count.ToString(CultureInfo.InvariantCulture) + ")") { Size = 14f, Weight = 600, Key = "hdr-" + version },
                new BoxEl
                {
                    Direction = 1,
                    Grow = 1f,
                    Basis = 0f,
                    MinHeight = 0f,
                    Children =
                    [
                        sessions.Count == 0
                            ? (Element)new TextEl("No sessions yet.") { Size = 13f, Color = Tok.TextTertiary }
                            : ScrollView(new BoxEl { Direction = 1, Gap = 2f, Children = rows }) with { Grow = 1f },
                    ],
                },
            ],
        };
    }

    static void OpenFolder(string dir)
    {
        try
        {
            if (Directory.Exists(dir)) Process.Start(new ProcessStartInfo("explorer.exe", "\"" + dir + "\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            LabState.Status.Value = "Could not open " + dir + ": " + ex.Message;
        }
    }

    sealed class StartStop : Component
    {
        public override Element Render()
        {
            bool rec = LabState.Recording.Value;
            return rec
                ? Controls.Button.Create("Stop (F10)", LabState.StopRecording, ButtonAppearance.Accent)
                : Controls.Button.Create("Start (F10)", LabState.StartRecording, ButtonAppearance.Accent);
        }
    }
}
