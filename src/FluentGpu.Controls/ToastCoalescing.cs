using System;

namespace FluentGpu.Controls;

// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
//  Toast de-duplication — the DECISION half of ToastController.Show, split out so it is a pure value function.
//
//  It lives in its own file (and takes no Element, no Signal, no host) for one reason: the rule "two calls that mean
//  the SAME thing must not stack two cards" is a correctness rule, and a rule that can only be exercised by standing
//  up a window is a rule nobody tests. Wavee.Tests source-includes this file and pins the semantics
//  (ToastCoalescingTests); ToastController just asks it and then does the mechanical part (restart the countdown,
//  adopt the newer action).
// ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

/// <summary>Decides whether an incoming toast is the SAME toast as one already on screen. See
/// <see cref="IsDuplicate"/> for the rule; <c>ToastOptions.DedupeKey</c> is the caller's half of the contract.</summary>
public static class ToastCoalescing
{
    /// <summary>What a toast is identified BY: its explicit <c>DedupeKey</c> when it has one, otherwise its own message
    /// text.
    ///
    /// <para>The message fallback is what makes the guard work for the callers that never think about keys — two
    /// <c>Toast.Show("Couldn't reach the server")</c> calls are, by any user-visible measure, one notification. The
    /// explicit key exists for the opposite case: several independent lanes can each raise a card for ONE failure
    /// (a dialog surfacing the module's own words, a playback bridge surfacing its generic sentence), and they must
    /// collapse to one card even though their sentences differ. Only a key both lanes agree on can express that, so
    /// the key WINS over the message whenever it is present.</para>
    ///
    /// <para>A whitespace-only key is treated as no key at all (it identifies nothing, and silently swallowing an
    /// unrelated toast is worse than showing two).</para></summary>
    /// <param name="dedupeKey">The toast's explicit key, or null.</param>
    /// <param name="message">The toast's message text (null ⇒ empty).</param>
    /// <returns>The identity to compare on; empty when the toast carries no identity at all.</returns>
    public static string EffectiveKey(string? dedupeKey, string? message)
    {
        if (!string.IsNullOrWhiteSpace(dedupeKey)) return dedupeKey.Trim();
        return message ?? "";
    }

    /// <summary>Is the incoming toast the same notification as one already showing? Effective keys
    /// (<see cref="EffectiveKey"/>) compared ORDINALLY — two spellings are two notifications, and a case-insensitive
    /// or culture-aware compare would coalesce cards the user meant to see separately.
    ///
    /// <para>An EMPTY effective key (no key, no message) never matches anything, including another empty one: nothing
    /// identifies those two cards as the same, so the safe answer is "show both".</para></summary>
    /// <param name="existingKey">The visible toast's <c>DedupeKey</c> (may be null).</param>
    /// <param name="existingMessage">The visible toast's message.</param>
    /// <param name="incomingKey">The new toast's <c>DedupeKey</c> (may be null).</param>
    /// <param name="incomingMessage">The new toast's message.</param>
    /// <returns>True when the incoming toast should REFRESH the existing card instead of stacking a second one.</returns>
    public static bool IsDuplicate(string? existingKey, string? existingMessage,
                                   string? incomingKey, string? incomingMessage)
    {
        string a = EffectiveKey(existingKey, existingMessage);
        if (a.Length == 0) return false;
        string b = EffectiveKey(incomingKey, incomingMessage);
        return b.Length != 0 && string.Equals(a, b, StringComparison.Ordinal);
    }
}
