using System.Collections.Generic;
using System.Runtime.CompilerServices;
using FluentGpu.Localization;

namespace FluentGpu.Forms;

/// <summary>
/// The NEUTRAL fallback floor for the engine's built-in validation rule messages (<see cref="Rules"/>). The default
/// <c>validation.*</c> loc keys must resolve to *some* string even when an app loads no culture table at all — otherwise
/// <see cref="Loc.Get"/> surfaces its visible-missing <c>[validation.required]</c> marker on the form error path. This
/// registers the ship-with-the-assembly English defaults as the terminal fallback (exactly the mechanism the control
/// kit uses for its own keys via a generated module initializer). A translation for the same key in any loaded culture
/// table always wins — the neutral floor is consulted last (see <see cref="Localization.RegisterNeutral"/>).
///
/// <para>Messages are no-argument by design (the keys carry no <c>{n}</c>) — argument-interpolated text is the caller's
/// custom-key cold path per <see cref="Rules"/>; these floor strings read standalone.</para>
/// </summary>
internal static class ValidationNeutral
{
    [ModuleInitializer]
    internal static void Register()
    {
        Localization.Localization.RegisterNeutral(new Dictionary<string, string>
        {
            ["validation.required"] = "Required.",
            ["validation.minlen"]   = "Too short.",
            ["validation.maxlen"]   = "Too long.",
            ["validation.range"]    = "Out of range.",
        });
    }
}
