using System;
using System.Reflection;

namespace Wavee;

/// <summary>
/// The one place the app answers "what version am I?". Reads <see cref="AssemblyInformationalVersionAttribute"/> off the
/// Wavee assembly and strips any <c>+build.metadata</c> suffix the SDK appends (SemVer 2 build metadata is not part of
/// the version's identity, and the update checker must never compare against it).
/// <para>
/// A dev build carries the csproj's <c>0.1.1-dev</c>; a packaged/published build is stamped with the 4-part package
/// version by <c>ops/build/pack-wavee-msix.ps1</c> (<c>/p:InformationalVersion=$Version</c>). An assembly with no
/// attribute at all (a headless test host) reports <c>"dev"</c> — the historical fallback the About tab shipped.
/// </para>
/// </summary>
static class AppVersion
{
    static readonly string s_current = Resolve();

    /// <summary>The running build's version — e.g. <c>0.1.1-dev</c>, <c>0.1.1.42</c>, or <c>dev</c> when unstamped.</summary>
    public static string Current => s_current;

    /// <summary>True for a build that is not a release artifact: the update checker refuses to compare against it, and
    /// the About tab captions it so a screenshot never gets mistaken for a shipped version.</summary>
    public static bool IsDev => s_current.EndsWith("-dev", StringComparison.Ordinal) || s_current == "dev";

    static string Resolve()
    {
        string? v = typeof(AppVersion).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(v)) return "dev";
        int plus = v.IndexOf('+');
        return plus > 0 ? v[..plus] : v;
    }
}
