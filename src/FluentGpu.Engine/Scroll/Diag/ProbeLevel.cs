namespace FluentGpu.Scroll.Diag;

/// <summary>
/// Runtime-switchable diagnostics granularity for <see cref="ScrollProbe"/> (scroll-rework design B.9). There is
/// deliberately no compile-time gate here (no <c>#if</c>, no env var): the Diagnostics page flips
/// <see cref="ScrollProbe.Level"/> at runtime, in every build, including a shipping Release binary — "diagnostics
/// runtime-switchable + live-tunable" is a hard rule of the rework, not a debug-only nicety.
/// </summary>
public enum ProbeLevel : byte
{
    /// <summary>Every <c>ScrollProbe.Record*</c> call is one volatile byte read plus a branch — no ring write, no
    /// allocation, no measurable cost.</summary>
    Off = 0,

    /// <summary>Records only the subset of events <see cref="BurstSummary"/> and a recorded session need (notches,
    /// contact-stream input reports, poses, non-anchored extent jumps, cost samples, present-truth samples, and markers)
    /// — cheap enough to leave on by default.</summary>
    Summary = 1,

    /// <summary>Records every event on every <c>Record*</c> call: the full per-stage pipeline trace, for a live
    /// capture session exported with <see cref="ScrollProbe.ExportCsv"/>.</summary>
    Trace = 2,
}
