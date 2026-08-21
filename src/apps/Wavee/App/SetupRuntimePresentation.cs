using System;

namespace Wavee;

/// <summary>Pure presentation math for the setup wizard's live runtime transfer. Kept outside the component so byte
/// overshoot and catalog hash formatting stay deterministic and headlessly testable.</summary>
static class SetupRuntimePresentation
{
    public static float ProgressFraction(long received, long total)
        => total <= 0 ? 0f : Math.Clamp((float)((double)received / total), 0f, 1f);

    public static string ShortHash(string hash) => hash.Length > 8
        ? string.Concat(hash.AsSpan(0, 4), "…", hash.AsSpan(hash.Length - 4, 4))
        : hash;
}
