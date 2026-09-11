using System;
using System.Diagnostics;
using System.IO;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using FluentGpu.Rhi;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Scene;
using FluentGpu.Scroll;

namespace FluentGpu;

/// <summary>
/// <c>--dialog-scroll-probe [outDir]</c> — the REAL-PATH repro for "ContentDialog labels vanish after the body is
/// scrolled": a <see cref="ContentDialog"/> with a tall <c>Content</c> (its own <c>ScrollEl { ContentSized }</c>) over
/// a text-heavy page, driven through the scroll kernel's command port exactly as a wheel would, and captured from the
/// presented back buffer at scroll-top, mid-scroll and end. Headless gates prove the DrawList carries every glyph run
/// on every frame (<c>gate.dialog.scrolled-body-text</c>); what they cannot see is the D3D12 backend's per-frame glyph
/// instance bank, its per-segment glyph batching and the partial-repaint replay — this probe renders through all of
/// them and prints, per capture, the repaint route, the dropped-instance count, the glyph-instance count and the
/// segment count, so a blank label is attributable to a mechanism instead of a guess.
/// Exit 0 = every capture kept the command-row + tail-checkbox glyph counts it had at scroll-top; nonzero otherwise.
/// A command-line arm, not a behaviour switch (the <c>--repaint-identity</c> precedent). GPU required.
/// </summary>
internal static class DialogScrollProbe
{
    private const int Width = 1100, Height = 900;

    public static int Run(string? outDir)
    {
        outDir ??= ".tmp/dialog-scroll-probe";
        int exit = 1;
        FluentApp.DiagnosticRun = (host, window, device) =>
        {
            exit = Drive(host, window, device, outDir);
            return true;
        };
        FluentAppHarness.Run(() => new DialogScrollProbeScene(),
            new AppOptions { Title = "FluentGpu — dialog scroll probe", Width = Width, Height = Height, Mica = false, WarmCadenceMs = 0f });
        return exit;
    }

    private static int Drive(AppHost host, IPlatformWindow window, IGpuDevice device, string outDir)
    {
        if (window is not Win32Window w || device is not D3D12Device gpu)
        {
            Console.Error.WriteLine("dialog-scroll-probe: needs the Win32 + D3D12 backend (GPU required).");
            return 2;
        }
        Directory.CreateDirectory(outDir);
        Console.Error.WriteLine($"[dialog-scroll-probe] drive start → {Path.GetFullPath(outDir)}");
        Diag.Enabled = Diag.CompiledIn;   // the per-frame d3d12/text counters below are gated on this

        Settle(host, w, 12);
        var vp = FindScrollable(host.Scene, host.Scene.Root);
        if (vp.IsNull)
        {
            Console.Error.WriteLine("dialog-scroll-probe: no scroll viewport found (the dialog did not open?).");
            return 3;
        }
        int node = (int)vp.Raw.Index;
        host.Scene.TryGetScroll(vp, out var sc);
        Console.Error.WriteLine($"[dialog-scroll-probe] viewport node={node} content={sc.ContentH:0.#} viewport={sc.ViewportH:0.#} scale={w.Scale:0.###}");

        int fails = 0;
        Report(host, gpu, outDir, "top");
        foreach (var (name, offset) in new[] { ("scrolled", 240f), ("end", MathF.Max(0f, sc.ContentH - sc.ViewportH)), ("back-to-top", 0f) })
        {
            host.Scene.ScrollPort?.Post(ScrollInput.ScrollTo(node, offset, immediate: true));
            Settle(host, w, 12);
            fails += Report(host, gpu, outDir, name);
        }
        return fails == 0 ? 0 : 1;
    }

    private static int Report(AppHost host, D3D12Device gpu, string outDir, string name)
    {
        byte[] px = Capture(host, gpu, out int cw, out int ch);
        string path = Path.Combine(outDir, $"dialog-{name}.png");
        if (px.Length > 0) PngWriter.WriteBgra(path, px, cw, ch);
        string diag = Diag.Enabled ? Diag.Snapshot() : "(diag not compiled in)";
        Console.Error.WriteLine($"[dialog-scroll-probe] {name}: route={gpu.LastRepaintRoute} full={gpu.LastRepaintFullReason} rects={gpu.LastReplayRectCount} " +
                                $"dropped={gpu.LastDroppedInstanceCount} glyphInsts={gpu.LastGlyphInstanceCount} segments={gpu.LastSegmentCount} → {path}");
        Console.Error.WriteLine($"[dialog-scroll-probe] {name} diag: {diag}");
        return gpu.LastDroppedInstanceCount == 0 ? 0 : 1;
    }

    // The DIALOG's scroller is the ContentSized one (ContentDialog.cs: `ScrollEl { ContentSized = true }`); the page
    // behind it scrolls too, but that viewport is a plain window-sized boundary.
    private static NodeHandle FindScrollable(SceneStore s, NodeHandle n)
    {
        if (n.IsNull) return NodeHandle.Null;
        if (s.HasScroll(n) && s.TryGetScroll(n, out var st) && st.ContentSized) return n;
        for (var c = s.FirstChild(n); !c.IsNull; c = s.NextSibling(c))
        {
            var r = FindScrollable(s, c);
            if (!r.IsNull) return r;
        }
        return NodeHandle.Null;
    }

    private static void Settle(AppHost host, Win32Window w, int quietFrames)
    {
        var sw = Stopwatch.StartNew();
        int quiet = 0;
        while (quiet < quietFrames && sw.ElapsedMilliseconds < 4000 && !w.IsClosed)
        {
            ulong before = host.PublishSequence;
            long skippedBefore = host.FramesSkippedSubmit;
            host.RunFrame();
            if (host.PublishSequence != before || host.FramesSkippedSubmit != skippedBefore) quiet = 0;
            else quiet++;
            w.WaitForWork(Math.Clamp(host.RecommendedWaitMs(), 1, 16));
        }
        ulong target = host.PublishSequence;
        while (host.RenderPresentSeq < target && sw.ElapsedMilliseconds < 6000 && !w.IsClosed) w.WaitForWork(1);
    }

    private static byte[] Capture(AppHost host, D3D12Device gpu, out int width, out int height)
    {
        byte[]? px = null;
        int cw = 0, ch = 0;
        host.RunWithRenderThreadParked(() => { px = gpu.CaptureBgra(out cw, out ch); });
        width = cw; height = ch;
        return px ?? [];
    }
}

/// <summary>A text-heavy page (a Settings-like list) with a tall-bodied <see cref="ContentDialog"/> opened on mount —
/// the "Report a problem" shape: paragraphs, then a CheckBox at the very end, then the command row.</summary>
sealed class DialogScrollProbeScene : Component
{
    public override Element Render()
    {
        // `--dense`: three columns of rows, so the frame's glyph-quad count crosses GlyphRenderer.MaxGlyphs (8192) —
        // the bank-overflow arm of the repro (a real Settings page + sidebar + player bar behind the dialog is that dense).
        bool dense = Array.IndexOf(Environment.GetCommandLineArgs(), "--dense") >= 0;
        int cols = dense ? 6 : 1;
        var rows = new Element[36];
        for (int i = 0; i < rows.Length; i++)
        {
            var cells = new Element[cols];
            for (int c = 0; c < cols; c++)
                cells[c] = new BoxEl
                {
                    Direction = 1, Gap = 2f, Padding = new Edges4(16f, 8f, 16f, 8f), Grow = 1f, Basis = 0f, MinWidth = 0f,
                    Children =
                    [
                        new TextEl($"Setting row {i + 1} — a header that reads like a real settings entry") { Size = 14f, Wrap = TextWrap.Wrap, MinWidth = 0f },
                        new TextEl("A secondary description line explaining what the setting does and when it applies to the session.") { Size = 12f, Wrap = TextWrap.Wrap, MinWidth = 0f },
                    ],
                };
            rows[i] = new BoxEl { Direction = 0, Children = cells };
        }
        // The page is a window-sized scroller (not a 1900-DIP column) so the overlay host — and the dialog centred in
        // it — stays inside the window; the settings list overflows behind the plate exactly like the real About tab.
        var page = new ScrollEl
        {
            Grow = 1f, Shrink = 1f, MinHeight = 0f, EdgeCues = ScrollEdgeCues.None,
            Content = new BoxEl { Direction = 1, Fill = ColorF.FromRgba(32, 32, 32), Children = rows },
        };

        var paras = new Element[22];
        for (int i = 0; i < paras.Length; i++)
            paras[i] = new TextEl($"Paragraph {i + 1}. Wavee opens the matching GitHub form with the version, install source, architecture and Windows build filled in, and copies a redacted report to the clipboard for you to paste.")
            { Size = 14f, Wrap = TextWrap.Wrap, MinWidth = 0f };
        // The "Report a problem" body ends with the redacted-report PREVIEW: a monospace block of ~120 log lines that
        // sits BELOW the fold at scroll-top (record-time culling drops it) and enters the clip only once the body is
        // scrolled — thousands of glyph quads arriving at once, ahead of the tail (checkbox + command row) in stream order.
        var previewLines = new System.Text.StringBuilder();
        for (int i = 0; i < 120; i++)
            previewLines.Append("seq=").Append(1000 + i).Append(" tid=12 t=1788354392297 sid=1afba092 pid=27724 I [hydration] hydration.tracks.gaps - track hydration still has field gaps n=180 surface=PlaylistOpen level=Open").Append('\n');
        var preview = new TextEl(previewLines.ToString()) { Size = 11f, FontFamily = "Consolas", Wrap = TextWrap.Wrap, MinWidth = 0f };
        var body = new BoxEl
        {
            Direction = 1, Gap = 12f, MinWidth = 0f,
            Children = [.. paras, preview, CheckBox.Create("Include the redacted report in the clipboard", isChecked: null, onChange: null)],
        };

        Element dialog = Embed.Comp(() => new ContentDialog
        {
            TriggerLabel = "Report a problem…",
            Title = "Report a problem",
            Content = body,
            PrimaryText = "Report on GitHub",
            CloseText = "Not now",
            DefaultButton = ContentDialog.DefaultBtn.Primary,
            OpenOnMount = true,
        });

        return Embed.Comp(() => new OverlayHost { Child = new BoxEl { Grow = 1f, ZStack = true, ClipToBounds = true, Fill = ColorF.FromRgba(32, 32, 32), Children = [page, dialog] } });
    }
}
