using System;
using System.IO;
using System.Text;
using FluentGpu.WindowsApi.Packaging;

namespace FluentGpu;

/// <summary>
/// Identity readback probe (<c>--packaging-probe</c>). Runs BEFORE any window/GPU stack spins up (mirrors the other
/// console-style probe arms in <c>Program.cs</c>), reads <see cref="PackageIdentity"/> for the CURRENT process, and
/// writes the six identity fields to a fixed, non-virtualized path the orchestrator can read from outside the process,
/// then returns 0 (the caller <c>Environment.Exit</c>s before <c>FluentApp.Run</c>). When launched via the packaged-app
/// AppExecutionAlias the process bears package identity, so <c>IsPackaged</c> reports True and the real
/// PackageFullName/FamilyName/AUMID are read back.
/// </summary>
/// <remarks>
/// <para>
/// <b>Readback path.</b> A PACKAGED full-trust Win32 app's <c>%TEMP%</c> / <see cref="Path.GetTempPath"/> can be
/// redirected into the package container by filesystem virtualization, so a file written there is NOT reliably
/// readable at the orchestrator's literal <c>%TEMP%</c> location. The PRIMARY sink is therefore the non-virtualized,
/// orchestrator-owned <c>C:\WAVEE\fluent-gpu\.tmp-shots\pkg-probe.txt</c> (a <c>runFullTrust</c> app holds the
/// launching user's token and writes there unvirtualized). <c>%TEMP%</c> and stdout are written as best-effort
/// secondaries (a WinExe has no console unless the alias keeps stdout attached).
/// </para>
/// <para>
/// <b>Report-only.</b> This arm always returns 0 — its job is to REPORT identity, not to fail on an unpackaged run.
/// The register/launch script asserts <c>IsPackaged=True</c> from the primary file. (The existing probes reserve
/// 2 = repro / 3 = harness-error for their own semantics; returning nonzero for a benign unpackaged iteration would
/// confuse the alias launcher.)
/// </para>
/// </remarks>
internal static class PackagingProbe
{
    // Non-virtualized, orchestrator-owned primary readback (repo .tmp-shots/ is not gitignored).
    private const string PrimaryPath = @"C:\WAVEE\fluent-gpu\.tmp-shots\pkg-probe.txt";

    public static int Run(string[] args)
    {
        var sb = new StringBuilder();
        void Line(string s) { sb.Append(s).Append('\n'); try { Console.WriteLine(s); } catch { /* no console on a WinExe */ } }

        // Read identity (cold, cached, never throws; null/false when unpackaged).
        Line("IsPackaged=" + PackageIdentity.IsPackaged);
        Line("PackageFullName=" + (PackageIdentity.PackageFullName ?? "<null>"));
        Line("PackageFamilyName=" + (PackageIdentity.PackageFamilyName ?? "<null>"));
        Line("ApplicationUserModelId=" + (PackageIdentity.ApplicationUserModelId ?? "<null>"));
        Line("Version=" + (PackageIdentity.Version?.ToString() ?? "<null>"));
        Line("InstalledLocation=" + (PackageIdentity.InstalledLocation ?? "<null>"));
        Line("ProcessPath=" + (Environment.ProcessPath ?? "<null>"));

        string text = sb.ToString();

        // PRIMARY: fixed non-virtualized path (reliable readback from the orchestrator).
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(PrimaryPath)!);
            File.WriteAllText(PrimaryPath, text);
        }
        catch (Exception ex) { try { Console.WriteLine("[packaging-probe] primary write failed: " + ex.Message); } catch { } }

        // SECONDARY: %TEMP% (may be virtualized under packaging — best-effort, matches the mission's stated path).
        try
        {
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "fluentgpu-pkg-probe.txt"), text);
        }
        catch { /* best-effort */ }

        return 0;   // report-only; the register/launch script asserts IsPackaged=True from PrimaryPath.
    }
}
