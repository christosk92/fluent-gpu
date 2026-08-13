using FluentGpu.Hooks;

namespace FluentGpu.Input;

/// <summary>
/// THE a11y announcement seam — the engine-side, backend-agnostic front door to a screen-reader live region.
///
/// <para>The raise itself is the host's: <c>InputHooks.Announce</c> is a <c>(text, assertive)</c> delegate the Windows
/// backend points at <c>UiaRaiseNotificationEvent</c> on the window's UIA provider (<c>Win32Uia.Announce</c>), itself
/// gated on <c>UiaClientsAreListening</c>. A headless host, a probe tree and the VerticalSlice leave it null, so every
/// call here is a no-op BY CONSTRUCTION — nothing needs a test double to stay silent and nothing needs a backend check.
/// A test that WANTS to observe announcements assigns <c>InputHooks.Current.Default.Announce</c> itself.</para>
///
/// <para><b>What this adds over the raw delegate</b> is the one policy every announcing surface would otherwise
/// re-invent: COALESCING. Primer and React-Aria both throttle reorder/status announcements (~100 ms) because a held
/// arrow key, or a pointer crossing rows, emits far more state changes than a reader can speak — an un-throttled
/// channel reads back the third position while the user is already on the tenth.</para>
///
/// <para><b>The honest shape of that throttle:</b> it is LEADING-EDGE and it DROPS what it swallows. There is no timer
/// here, so there is nothing to deliver a trailing message with, and inventing one would mean putting this on the frame
/// clock — a bigger change than the channel is worth today. The contract that makes dropping correct is on the CALLER:
/// every throttled run must END with a plain <see cref="Say"/> stating the settled result ("dropped at position 3 of
/// 12"), which is both more useful than the last intermediate message and immune to the window.</para>
///
/// <para><b>Allocation:</b> nothing here allocates. Callers compose their text on the EDGE that triggered it (a grab, a
/// slot change, a drop) and pass it in; no announcement path may run inside frame phases 6–13.</para>
///
/// <para>Assertive vs polite is the ARIA distinction the backend forwards verbatim: assertive interrupts (an error, a
/// live reorder the user is steering by), polite queues behind speech in progress (a status, "Copied").</para>
/// </summary>
public static class Announcer
{
    /// <summary>The Primer / React-Aria coalescing window for a user-driven run of state changes (a held arrow key, a
    /// pointer drag crossing rows). 100 ms is their shipped value.</summary>
    public const float DefaultThrottleMs = 100f;

    private static long s_lastSpokeMs;

    /// <summary>True when a host has wired the backend — i.e. an announcement can actually reach an assistive client.
    /// Callers test it BEFORE composing a string that nothing would speak: the composition, not the raise, is the cost
    /// (the raise is already gated on <c>UiaClientsAreListening</c> inside the backend).</summary>
    public static bool IsAvailable => InputHooks.Current.Default.Announce is not null;

    /// <summary>Announce <paramref name="text"/> NOW and reopen the throttle window. This is the terminal form: a
    /// settled state supersedes every intermediate one the throttle dropped. Null/empty is a no-op that still reopens
    /// the window, so a caller may pass a resolved-to-nothing string safely.</summary>
    public static void Say(string? text, bool assertive = false)
    {
        s_lastSpokeMs = Now();
        if (string.IsNullOrEmpty(text)) return;
        InputHooks.Current.Default.Announce?.Invoke(text, assertive);
    }

    /// <summary>Announce <paramref name="text"/> only if at least <paramref name="throttleMs"/> has elapsed since the
    /// last announcement; otherwise DROP it (see the class remarks — pair every throttled run with a terminal
    /// <see cref="Say"/>). Returns true when it was spoken.</summary>
    public static bool SayThrottled(string? text, bool assertive = false, float throttleMs = DefaultThrottleMs)
    {
        long now = Now();
        if (now - s_lastSpokeMs < (long)throttleMs) return false;
        s_lastSpokeMs = now;
        if (string.IsNullOrEmpty(text)) return false;
        InputHooks.Current.Default.Announce?.Invoke(text, assertive);
        return true;
    }

    /// <summary>Reopen the throttle window without speaking — the deterministic-test reset, and the right call after a
    /// gesture that ended without an announcement.</summary>
    public static void Reset() => s_lastSpokeMs = 0;

    private static long Now() => System.Environment.TickCount64;
}
