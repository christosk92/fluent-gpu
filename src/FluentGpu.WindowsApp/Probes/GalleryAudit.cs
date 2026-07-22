using System;
using System.Collections.Generic;
using System.IO;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Text.Headless;
using static FluentGpu.Dsl.Ui;

namespace FluentGpu;

/// <summary>
/// The <c>--gallery-audit</c> headless gate (WS7 W7.1): for EVERY entry in the generated
/// <c>FluentGpu.Generated.GalleryRegistry</c> it constructs, mounts, and runs 3 frames on the headless seam, asserting
/// no throw; it verifies page-key uniqueness and that every routable <c>RelatedLinks</c>/PageInfo Related key resolves
/// to a registry page. VerticalSlice can't reference the app, so this app-side arm is the registry's integrity gate.
/// Exit code = failure count (0 = clean).
/// </summary>
internal static class GalleryAudit
{
    public static int Run(string[] args)
    {
        var strings = new StringTable();
        var pages = FluentGpu.Generated.GalleryRegistry.Pages;
        int failures = 0;

        // 1) Key uniqueness (the generator already errors FGG010 at compile; this is the runtime backstop).
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var p in pages)
            if (!keys.Add(p.Key)) { Console.WriteLine($"[gallery-audit] DUPLICATE KEY '{p.Key}'"); failures++; }

        // 2) Every page constructs + mounts + survives 3 frames on a fresh headless host.
        foreach (var p in pages)
        {
            try
            {
                using var app = new HeadlessPlatformApp();
                var window = new HeadlessWindow(new WindowDesc("audit-" + p.Key, new Size2(1240, 820), 1f));
                window.Show();
                using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings),
                    strings, new AuditRoot { Key = p.Key });
                for (int f = 0; f < 3; f++) host.RunFrame();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[gallery-audit] THROW mounting '{p.Key}': {ex.GetType().Name}: {ex.Message}");
                failures++;
            }
        }

        // 3) Related-key resolution: every routable PageInfo Related key must be a registered page key.
        foreach (var p in pages)
        {
            var meta = PageInfo.Find(p.Key);
            if (meta is null) continue;
            foreach (var rel in PageInfo.RoutableRelated(meta))
                if (!keys.Contains(rel)) { Console.WriteLine($"[gallery-audit] page '{p.Key}' Related key '{rel}' does not resolve to a page"); failures++; }
        }

        // 4) Wavee link contract (G8c2): every [GalleryPage(WaveeUse=…, WaveePath=…)] must name BOTH, and WaveePath must
        // exist on disk relative to the repo root. A "See this in Wavee" pointer that rots is worse than none.
        string? repo = RepoRoot();
        int waveeTagged = 0;
        foreach (var p in pages)
        {
            bool hasUse = p.WaveeUse.Length > 0, hasPath = p.WaveePath.Length > 0;
            if (!hasUse && !hasPath) continue;
            waveeTagged++;
            if (hasUse != hasPath)
            {
                Console.WriteLine($"[gallery-audit] page '{p.Key}' WaveeUse/WaveePath must be set together (Use='{p.WaveeUse}' Path='{p.WaveePath}')"); failures++; continue;
            }
            if (repo is null) { Console.WriteLine($"[gallery-audit] page '{p.Key}' WaveePath '{p.WaveePath}' — cannot verify (repo root not found)"); failures++; continue; }
            string full = System.IO.Path.Combine(repo, p.WaveePath.Replace('/', System.IO.Path.DirectorySeparatorChar));
            if (!System.IO.File.Exists(full)) { Console.WriteLine($"[gallery-audit] page '{p.Key}' WaveePath '{p.WaveePath}' does not exist on disk"); failures++; }
        }

        // 5) Knob/code coherence (G8c2, the research trap-3 guard): a knobbed [Sample] whose extracted Code text does not
        // mention a knob's label is drift (the shown code doesn't match the live example's wiring). Pragmatic string
        // containment over the generated GallerySamples registry — no reflection (AOT-clean).
        int samplesChecked = 0, knobbed = 0;
        foreach (var (owner, sample) in FluentGpu.Generated.GallerySamples.All)
        {
            samplesChecked++;
            var probe = new FluentGpu.GalleryKit.Knobs();
            try { _ = sample.Factory(probe); }
            catch (Exception ex) { Console.WriteLine($"[gallery-audit] sample '{owner}.{sample.Title}' threw while probing knobs: {ex.GetType().Name}"); failures++; continue; }
            if (probe.Labels.Count == 0) continue;
            knobbed++;
            foreach (var label in probe.Labels)
                if (!sample.Code.Contains(label, StringComparison.Ordinal))
                {
                    Console.WriteLine($"[gallery-audit] sample '{owner}.{sample.Title}' knob label '{label}' is not present in its shown code (knob/code drift)"); failures++;
                }
        }

        Console.WriteLine($"[gallery-audit] {pages.Length} registry pages checked; {waveeTagged} Wavee-tagged; {samplesChecked} samples ({knobbed} knobbed); {failures} failure(s).");
        return failures;
    }

    /// <summary>Walk up from the working dir / base dir to the repo root (the folder holding <c>src/FluentGpu.slnx</c>).</summary>
    private static string? RepoRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(start);
            while (dir is not null)
            {
                if (File.Exists(System.IO.Path.Combine(dir.FullName, "src", "FluentGpu.slnx"))) return dir.FullName;
                dir = dir.Parent;
            }
        }
        return null;
    }

    /// <summary>The <c>--shot-list</c> sweep contract: one <c>page:&lt;key&gt;</c> id per registry page whose
    /// <see cref="FluentGpu.GalleryKit.ShotMode"/> is not <c>Skip</c>, plus the whole-shell id. Consumed by
    /// <c>scripts/gallery-shot-sweep.ps1</c>.</summary>
    public static int ShotList()
    {
        Console.WriteLine("gallery");   // the whole registry-driven shell (regression scene)
        foreach (var p in FluentGpu.Generated.GalleryRegistry.Pages)
        {
            if (p.ShotMode == FluentGpu.GalleryKit.ShotMode.Skip) continue;
            Console.WriteLine($"page:{p.Key}\t{p.ShotMode}");
        }
        return 0;
    }
}

/// <summary>Headless mount root for the audit: wraps the resolved page in an OverlayHost (so overlay-using pages have a
/// service) — the same composition-root shape the live shell provides.</summary>
internal sealed class AuditRoot : Component
{
    public string Key = "";
    public override Element Render()
        => Embed.Comp(() => new FluentGpu.Controls.OverlayHost
        {
            Child = FluentGpu.Generated.GalleryRegistry.Create(Key) ?? new BoxEl(),
        });
}
