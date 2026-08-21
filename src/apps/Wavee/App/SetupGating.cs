using System;

namespace Wavee;

/// <summary>The pages of the first-run setup wizard, in display order. The numeric values are NOT persisted (unlike
/// <c>SidebarDesign</c>'s ints) — nothing writes a <see cref="SetupPage"/> to settings, so renumbering this enum is
/// safe. <see cref="SetupGating.NextPage"/>/<see cref="SetupGating.PrevPage"/> walk it; <see cref="SetupGating.StepNumber"/>
/// maps the middle seven onto the footer's "Step N of 7".</summary>
public enum SetupPage { Welcome = 0, Terms = 1, SignIn = 2, LocalPlayback = 3, Appearance = 4, Sidebar = 5, Sound = 6, Notifications = 7, Done = 8 }

/// <summary>§(setup wizard) — the PURE decisions behind the wizard shell: whether it is armed/completed, the two
/// exit-path writes (<see cref="MarkCompleted"/>/<see cref="MarkDeferred"/>), which page comes next/previous when
/// sign-in is being skipped, and the footer's progress label/fraction.
///
/// ENGINE-FREE BY CONSTRUCTION (System + IAppSettings + the generated Strings consts). That is load-bearing exactly
/// like <c>SidebarDesignGating</c>: this file is source-included by <c>Wavee.Tests</c> (which has no FluentGpu.Engine
/// reference), so <c>SetupGatingTests</c> drives the REAL state machine instead of a copy of it. Nothing here may
/// reference <c>Signal&lt;T&gt;</c>, <c>Element</c>, <c>Loc</c> or any other engine type — the wizard's visuals and
/// command labels live elsewhere (<c>SetupCommands.cs</c> for the latter).
///
/// WHY A SEPARATE FILE. Exactly the <c>SidebarDesignGating</c> rationale: getting either the gate or the two markers
/// wrong is unrecoverable per install — a marker burned too early denies the wizard to the fresh install it exists
/// for, and a marker never written re-shows a "one-time" wizard on every launch. <see cref="MarkDeferred"/> being a
/// no-op once <see cref="MarkCompleted"/> has run is the one invariant most likely to be gotten backwards, and is
/// exactly the kind of thing a unit test pins and a code review does not.</summary>
static class SetupGating
{
    /// <summary>Is the wizard armed and not yet shown to completion or deferral? Mirrors
    /// <c>SidebarDesignGating.ShouldShowChooser</c>'s one-boolean-read shape. Null-tolerant: no settings store ⇒ the
    /// wizard never shows (nothing to persist its exit, so showing it would be a dialog with no memory).</summary>
    public static bool IsPending(IAppSettings? settings)
        => settings is not null && settings.Get(WaveeSettings.SetupPending);

    /// <summary>Has the user reached Done at least once, ever? Independent of <see cref="IsPending"/> — a deferred
    /// wizard leaves this false forever until the user actually finishes it.</summary>
    public static bool IsCompleted(IAppSettings? settings)
        => settings is not null && settings.Get(WaveeSettings.SetupCompleted);

    /// <summary>Reaching Done. Sets <c>SetupCompleted</c> and clears <c>SetupPending</c>. Idempotent; returns true only
    /// on the transition (the log line / a test's "flipped once" assertion). Completed beats a later
    /// <see cref="MarkDeferred"/> call by construction — deferring never un-sets Completed, so calling this first and
    /// deferring afterward (which should not happen, but must not corrupt state if it does) leaves Completed true.</summary>
    public static bool MarkCompleted(IAppSettings? settings)
    {
        if (settings is null || settings.Get(WaveeSettings.SetupCompleted)) return false;
        settings.Set(WaveeSettings.SetupCompleted, true);
        settings.Set(WaveeSettings.SetupPending, false);
        return true;
    }

    /// <summary>Deferring — "Not now", Escape, light-of-modal, a shutdown-time close. Clears <c>SetupPending</c> only;
    /// deliberately does NOT touch <c>SetupCompleted</c>. A "one-time" dialog that comes back on the next launch is the
    /// failure mode this whole file exists to prevent: deferred means "don't show again automatically", while Settings
    /// can still offer a manual re-run. No-op once the wizard is already completed (idempotent either way) — a
    /// stray deferral after Done must never make a finished wizard look unfinished.</summary>
    public static bool MarkDeferred(IAppSettings? settings)
    {
        if (settings is null || settings.Get(WaveeSettings.SetupCompleted)) return false;
        if (!settings.Get(WaveeSettings.SetupPending)) return false;
        settings.Set(WaveeSettings.SetupPending, false);
        return true;
    }

    /// <summary>Skip the SignIn page when the user is already authenticated — re-showing a login screen to someone
    /// already logged in (the "run the wizard again from Settings" path) would be nonsensical.</summary>
    public static bool SkipSignIn(bool authed) => authed;

    /// <summary>Whether closing the PRE-auth overlay is an auth-gate handoff rather than a real wizard dismissal.
    /// All three witnesses are required: a post-auth dialog close must clean up normally; a pending logged-out wizard
    /// must stay put; and a completed/re-auth flow has no unfinished first-run session to carry forward.</summary>
    public static bool CarriesAcrossAuthGate(bool bare, bool pending, bool authenticated)
        => bare && pending && authenticated;

    /// <summary>The next page from <paramref name="page"/>, skipping <see cref="SetupPage.SignIn"/> when
    /// <paramref name="skipSignIn"/>, clamped at <see cref="SetupPage.Done"/>.</summary>
    public static SetupPage NextPage(SetupPage page, bool skipSignIn)
    {
        var next = (SetupPage)Math.Min((int)page + 1, (int)SetupPage.Done);
        if (skipSignIn && next == SetupPage.SignIn)
            next = (SetupPage)Math.Min((int)next + 1, (int)SetupPage.Done);
        return next;
    }

    /// <summary>The previous page from <paramref name="page"/>, skipping <see cref="SetupPage.SignIn"/> when
    /// <paramref name="skipSignIn"/>, clamped at <see cref="SetupPage.Welcome"/>.</summary>
    public static SetupPage PrevPage(SetupPage page, bool skipSignIn)
    {
        var prev = (SetupPage)Math.Max((int)page - 1, (int)SetupPage.Welcome);
        if (skipSignIn && prev == SetupPage.SignIn)
            prev = (SetupPage)Math.Max((int)prev - 1, (int)SetupPage.Welcome);
        return prev;
    }

    /// <summary>The footer's progress-label loc KEY for <see cref="SetupPage.Welcome"/> and <see cref="SetupPage.Done"/>
    /// — the two pages whose label is a fixed phrase rather than "Step N of 7". Returns null for every other page: the
    /// caller pairs <see cref="StepNumber"/> with the parameterized <c>Strings.Setup.StepOf(n, total)</c> method for
    /// those (a loc VALUE with placeholders is a format method, not a plain key — see en-US.json's <c>setup.stepOf</c>).</summary>
    public static string? StepLabelKey(SetupPage page) => page switch
    {
        SetupPage.Welcome => Strings.Setup.PreSetup,
        SetupPage.Done => Strings.Setup.Complete,
        _ => null,
    };

    /// <summary>The "Step N of 7" numbers for the seven middle pages (null for Welcome/Done). Deliberately keyed on the
    /// page identity, NOT on a running count of pages actually shown: the numbers must be IDENTICAL whether or not
    /// SignIn was skipped on a re-run, so the wizard reads as the same product both times rather than a shorter one.</summary>
    public static (int Step, int Total)? StepNumber(SetupPage page) => page switch
    {
        SetupPage.Welcome or SetupPage.Done => null,
        _ => ((int)page, 7),
    };

    /// <summary>The footer's progress fraction: 0 at Welcome, 1 at Done, <c>n/7</c> for the seven middle pages —
    /// independent of <see cref="StepNumber"/> skipping SignIn, for the same "same product either way" reason.</summary>
    public static float Progress(SetupPage page) => page switch
    {
        SetupPage.Welcome => 0f,
        SetupPage.Done => 1f,
        _ => (int)page / 7f,
    };
}
