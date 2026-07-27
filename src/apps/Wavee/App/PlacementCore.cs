using System;

namespace Wavee;

/// <summary>Where a movable surface (today: the now-playing music video; next: lyrics / queue / now-playing) is shown.
/// Ordered by COMMITMENT — <see cref="Docked"/> costs the user nothing, <see cref="Detached"/> spawns a whole OS window —
/// because the fallback ladder walks that order (see <see cref="PlacementCore.FirstAvailable"/>).</summary>
public enum SurfacePlacement : byte
{
    /// <summary>Not shown at all. The only value that means "off".</summary>
    None = 0,
    /// <summary>Inline in the shell (the right rail / an inline panel). Reserved — no video surface honors it yet.</summary>
    Docked = 1,
    /// <summary>The in-window, draggable + resizable mini player (<c>InWindowVideoPip</c>). The DEFAULT: it is the
    /// lowest-commitment way to say "show me the video" — no new OS window, dismissible, stays with the app.</summary>
    Floating = 2,
    /// <summary>A separate always-on-top OS window (<c>PopOutVideoWindow</c>). The most committing placement.</summary>
    Detached = 3,
    /// <summary>Fills the shell. A MODE, not a home: entering remembers <see cref="PlacementState.ReturnTo"/> and exiting
    /// restores it, and it is never persisted as <see cref="PlacementState.Preferred"/>. Reserved (M8).</summary>
    Fullscreen = 4,
}

/// <summary>A set of <see cref="SurfacePlacement"/> values. Used for both what a surface ALLOWS (its policy) and what is
/// AVAILABLE right now (allowed ∧ the host can do it ∧ the content supports it) — one bit-set, so "this track has no
/// video" and "a second swapchain cannot be opened" degrade through the exact same path.</summary>
[Flags]
public enum PlacementSet : byte
{
    None = 0,
    Docked = 1,
    Floating = 2,
    Detached = 4,
    Fullscreen = 8,
}

/// <summary>What a given surface allows and where it opens by default.</summary>
/// <param name="Allowed">The placements this surface can ever occupy.</param>
/// <param name="Default">The initial <see cref="PlacementState.Preferred"/> — where an unlit primary click opens it.</param>
public readonly record struct PlacementPolicy(PlacementSet Allowed, SurfacePlacement Default)
{
    /// <summary>The music-video surface: the in-window mini player (default) or a detached pop-out window. Docked and
    /// Fullscreen are deliberately NOT allowed yet — there is no surface to honor them, and an unhonorable placement
    /// would resolve to a mount nobody performs.</summary>
    public static readonly PlacementPolicy Video =
        new(PlacementSet.Floating | PlacementSet.Detached, SurfacePlacement.Floating);
}

/// <summary>
/// The COMPLETE placement state of one surface, as a single value. Everything the affordances, the surfaces and the
/// owner read is derived from this — there is no second flag anywhere, which is what makes the historical bugs
/// unrepresentable rather than merely fixed:
/// <list type="bullet">
/// <item><b>Intent vs reality.</b> <see cref="Requested"/>/<see cref="Preferred"/> are what the USER asked for;
/// <see cref="Live"/> is what the host actually has mounted and is written ONLY by that host. A stuck toggle is exactly
/// those two disagreeing with nowhere to say so — here they are separate fields and the button reads
/// <see cref="PlacementCore.Resolve"/>, never a standalone flag.</item>
/// <item><b>One placement, not a set of booleans.</b> <see cref="Requested"/> is an enum, so "mounted in two places at
/// once" (the Media Foundation double-pump hazard: exactly one mounted surface may pump a given player) cannot be
/// expressed.</item>
/// <item><b>Closing IS off (2026-07-26).</b> There is no "hidden but still on" state to represent: every user-initiated
/// close writes <see cref="Requested"/> = <see cref="SurfacePlacement.None"/>, so the only way a surface comes back is
/// the user asking for it again. The old content-scoped dismiss (a <c>DismissedGen</c> compared against a per-track
/// <c>ContentGen</c>, which expired by itself on the next track) is DELETED — it is what made a closed video re-open on
/// the following song, and keeping the fields around would leave "off, but it will come back" expressible.</item>
/// </list>
/// </summary>
/// <param name="Requested">The user's live intent: the placement they asked for, or <see cref="SurfacePlacement.None"/>
/// for "off". STICKY across content changes in BOTH directions — an on intent carries video from track to track, and an
/// off intent keeps every subsequent track on audio until the user turns video back on.</param>
/// <param name="Preferred">The last non-off, non-fullscreen placement the user chose. Where an unlit primary click
/// opens, and the only placement worth persisting ("persist where you like to work").</param>
/// <param name="ReturnTo">Where <see cref="SurfacePlacement.Fullscreen"/> exits back to.</param>
/// <param name="Live">What the owner actually has mounted right now. Written ONLY by the owner/host.</param>
/// <param name="Available">Allowed ∧ host-capable ∧ content-capable, right now.</param>
public readonly record struct PlacementState(
    SurfacePlacement Requested,
    SurfacePlacement Preferred,
    SurfacePlacement ReturnTo,
    SurfacePlacement Live,
    PlacementSet Available)
{
    /// <summary>The off, nothing-mounted, nothing-available starting state for a surface with the given policy.</summary>
    public static PlacementState Initial(in PlacementPolicy policy) => new(
        Requested: SurfacePlacement.None,
        Preferred: policy.Default,
        ReturnTo: SurfacePlacement.None,
        Live: SurfacePlacement.None,
        Available: PlacementSet.None);
}

/// <summary>What an owner must do to make reality match intent.</summary>
public enum MountAction : byte
{
    /// <summary>Reality already matches — do nothing.</summary>
    None,
    /// <summary>Nothing is mounted and something should be.</summary>
    Open,
    /// <summary>Something is mounted and nothing should be.</summary>
    Close,
    /// <summary>Something is mounted in the wrong placement — hand it over (close then open, in that order: exactly one
    /// surface may be mounted at a time).</summary>
    Move,
}

/// <summary>The kinds of thing that can happen to a surface. Exists so the whole state machine can be driven from data
/// (and therefore property-tested over arbitrary command sequences) — the app calls the named transitions directly.</summary>
public enum PlacementCommandKind : byte
{
    TogglePrimary, OpenAt, TurnOff, Availability, HostClosed, EnterFullscreen, ExitFullscreen, LiveChanged,
}

/// <summary>One command for <see cref="PlacementCore.Apply"/>. Unused fields are ignored per <paramref name="Kind"/>.</summary>
public readonly record struct PlacementCommand(
    PlacementCommandKind Kind,
    SurfacePlacement Placement = SurfacePlacement.None,
    PlacementSet Available = PlacementSet.None);

/// <summary>
/// How a surface's placement and geometry survive a restart, as pure string ↔ value conversions (the app layer owns the
/// actual <c>IAppSettings</c> keys). The rule the shapes encode: <b>persist where the user likes to work; never persist
/// whether something is running.</b> So the PREFERRED placement and the geometry round-trip, while
/// <see cref="SurfacePlacement.None"/> (off) and <see cref="SurfacePlacement.Fullscreen"/> (a mode) deliberately do not —
/// restoring "off" would be meaningless and restoring fullscreen would trap the user in it on the next launch.
/// </summary>
public static class PlacementPersistence
{
    /// <summary>The stored form of a preferred placement. Deliberately a NAME, not the enum's number: the numeric values
    /// encode the commitment ladder, so a future reordering would silently reinterpret everyone's saved preference.
    /// Returns an empty string for values that must never be persisted (off / fullscreen).</summary>
    public static string SavePlacement(SurfacePlacement p) => p switch
    {
        SurfacePlacement.Docked => "docked",
        SurfacePlacement.Floating => "floating",
        SurfacePlacement.Detached => "detached",
        _ => "",
    };

    /// <summary>Read a stored preference back, falling back to the surface's default for anything unrecognised, empty, or
    /// no longer <c>Allowed</c> — a saved placement whose surface has since been removed must not resurrect a placement
    /// nothing can honor.</summary>
    public static SurfacePlacement LoadPlacement(string? raw, in PlacementPolicy policy)
    {
        var p = (raw ?? "").Trim().ToLowerInvariant() switch
        {
            "docked" => SurfacePlacement.Docked,
            "floating" => SurfacePlacement.Floating,
            "detached" => SurfacePlacement.Detached,
            _ => SurfacePlacement.None,
        };
        return p != SurfacePlacement.None && PlacementCore.Allows(policy.Allowed, p) ? p : policy.Default;
    }

    /// <summary>Geometry as a comma-separated rect (the settings store holds strings; this matches the existing
    /// comma-joined conventions). Rounded to whole units — sub-pixel drag precision is not worth persisting.</summary>
    public static string SaveRect(float x, float y, float w, float h)
        => w <= 0f || h <= 0f
            ? ""
            : ((int)MathF.Round(x)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
              ((int)MathF.Round(y)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
              ((int)MathF.Round(w)).ToString(System.Globalization.CultureInfo.InvariantCulture) + "," +
              ((int)MathF.Round(h)).ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Parse geometry back. Returns false for anything malformed or degenerate, so a corrupt value falls back to
    /// the surface's default position instead of opening a 0×0 window somewhere off-screen.</summary>
    public static bool TryLoadRect(string? raw, out float x, out float y, out float w, out float h)
    {
        x = y = w = h = 0f;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var parts = raw.Split(',');
        if (parts.Length != 4) return false;
        var ci = System.Globalization.CultureInfo.InvariantCulture;
        if (!float.TryParse(parts[0], System.Globalization.NumberStyles.Float, ci, out x)) return false;
        if (!float.TryParse(parts[1], System.Globalization.NumberStyles.Float, ci, out y)) return false;
        if (!float.TryParse(parts[2], System.Globalization.NumberStyles.Float, ci, out w)) return false;
        if (!float.TryParse(parts[3], System.Globalization.NumberStyles.Float, ci, out h)) return false;
        if (!(w > 0f) || !(h > 0f) || float.IsNaN(x) || float.IsNaN(y)) { x = y = w = h = 0f; return false; }
        return true;
    }
}

/// <summary>
/// The PURE, engine-free placement state machine — no <c>Signal&lt;T&gt;</c>, no FluentGpu type, nothing but
/// <see cref="System"/>. Every rule the surfaces, the owner and the player-bar affordance obey lives here exactly once,
/// so the behavior is verifiable by the engine-free unit-test project (which source-includes this file) instead of only
/// by running the app.
///
/// <para>This file is deliberately app-local for now but has ZERO app dependencies: when the generic
/// <c>SurfaceHost</c> primitive lands it moves verbatim to <c>FluentGpu.Engine/Surfaces/PlacementCore.cs</c> and gains
/// its owner doc. Keep it <see cref="System"/>-only.</para>
/// </summary>
public static class PlacementCore
{
    // The commitment ladder (cheap → expensive). Fallback walks DOWN it first (prefer the less committing surface) and
    // only then up, so losing a detached window lands you in the mini player rather than turning the feature off.
    static readonly SurfacePlacement[] Ladder =
    [
        SurfacePlacement.Docked, SurfacePlacement.Floating, SurfacePlacement.Detached,
    ];

    /// <summary>The single-bit <see cref="PlacementSet"/> for a placement (<see cref="PlacementSet.None"/> for
    /// <see cref="SurfacePlacement.None"/>).</summary>
    public static PlacementSet Bit(SurfacePlacement p) => p switch
    {
        SurfacePlacement.Docked => PlacementSet.Docked,
        SurfacePlacement.Floating => PlacementSet.Floating,
        SurfacePlacement.Detached => PlacementSet.Detached,
        SurfacePlacement.Fullscreen => PlacementSet.Fullscreen,
        _ => PlacementSet.None,
    };

    /// <summary>Whether <paramref name="set"/> contains <paramref name="p"/> (never true for
    /// <see cref="SurfacePlacement.None"/> — "off" is not a placement you can be available at).</summary>
    public static bool Allows(PlacementSet set, SurfacePlacement p)
        => p != SurfacePlacement.None && (set & Bit(p)) != PlacementSet.None;

    /// <summary>The placement to actually mount for <paramref name="want"/>: itself when available, else the nearest
    /// available placement walking DOWN the commitment ladder first and then up, else <see cref="SurfacePlacement.None"/>.</summary>
    public static SurfacePlacement FirstAvailable(SurfacePlacement want, PlacementSet available)
    {
        if (want == SurfacePlacement.None) return SurfacePlacement.None;
        if (Allows(available, want)) return want;
        int i = LadderIndex(want);
        for (int d = i - 1; d >= 0; d--) if (Allows(available, Ladder[d])) return Ladder[d];
        for (int u = i + 1; u < Ladder.Length; u++) if (Allows(available, Ladder[u])) return Ladder[u];
        return SurfacePlacement.None;
    }

    // Fullscreen sits above the ladder: when it is unavailable the walk starts at the most committing real placement.
    static int LadderIndex(SurfacePlacement p) => p switch
    {
        SurfacePlacement.Docked => 0,
        SurfacePlacement.Floating => 1,
        SurfacePlacement.Detached => 2,
        _ => Ladder.Length,
    };

    /// <summary>THE derived truth every surface, owner and affordance reads: the placement that should be mounted right
    /// now, or <see cref="SurfacePlacement.None"/>. Off when the user turned it off (including by closing it) or when
    /// nothing is available (e.g. the track has no video).</summary>
    public static SurfacePlacement Resolve(in PlacementState s) => ResolveWith(s, s.Available);

    /// <summary><see cref="Resolve"/> with the availability overridden — for asking "what WOULD be resolved for
    /// <em>that</em> content?" without mutating state (the playback path asks this per track).</summary>
    public static SurfacePlacement ResolveWith(in PlacementState s, PlacementSet available)
    {
        if (s.Requested == SurfacePlacement.None) return SurfacePlacement.None;
        return FirstAvailable(s.Requested, available);
    }

    /// <summary>Whether anything is resolved (the surface should be visible / the media should be video).</summary>
    public static bool IsActive(in PlacementState s) => Resolve(s) != SurfacePlacement.None;

    // ── transitions ─────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The primary affordance: SYMMETRIC. Lit (anything resolved) → always off, from ANY placement. Unlit →
    /// open at <see cref="PlacementState.Preferred"/>. This symmetry is the whole reason the toggle cannot get stuck.</summary>
    public static PlacementState TogglePrimary(in PlacementState s)
        => IsActive(s) ? TurnOff(s) : OpenAt(s, s.Preferred);

    /// <summary>Show the surface at <paramref name="target"/> — the ONE way video comes back on after any close — and,
    /// for a real home (not Fullscreen), make the target the new <see cref="PlacementState.Preferred"/>.</summary>
    public static PlacementState OpenAt(in PlacementState s, SurfacePlacement target)
    {
        if (target == SurfacePlacement.None) return TurnOff(s);
        if (target == SurfacePlacement.Fullscreen) return EnterFullscreen(s);
        return s with
        {
            Requested = target,
            Preferred = target,
            ReturnTo = SurfacePlacement.None,
        };
    }

    /// <summary>Off — GLOBALLY and stickily, which since 2026-07-26 is what EVERY user-initiated close means (the
    /// surface's own ✕, the picker's "turn off video", the primary's "switch to audio"). No subsequent track re-opens
    /// the surface; only an explicit <see cref="OpenAt"/>/<see cref="TogglePrimary"/> does. Keeps
    /// <see cref="PlacementState.Preferred"/> (so that next open returns to the user's home) and clears the transient
    /// fullscreen bookkeeping.</summary>
    public static PlacementState TurnOff(in PlacementState s) => s with
    {
        Requested = SurfacePlacement.None,
        ReturnTo = SurfacePlacement.None,
    };

    /// <summary>Republish what is possible right now (allowed ∧ host-capable ∧ content-capable). Deliberately does NOT
    /// rewrite <see cref="PlacementState.Requested"/>: intent outlives a temporary loss of availability, so when the
    /// next track has a video (or the second window becomes possible again) the surface returns to where the user had
    /// it instead of to a fallback it silently got stuck in.</summary>
    public static PlacementState WithAvailability(in PlacementState s, PlacementSet available) => s with { Available = available };

    /// <summary>Owner-only: record what is actually mounted.</summary>
    public static PlacementState WithLive(in PlacementState s, SurfacePlacement live) => s with { Live = live };

    /// <summary>
    /// Fold ONE surface's mounted/unmounted report into the single <see cref="PlacementState.Live"/> field without
    /// letting it speak for any other surface: a surface may claim <c>Live</c> for itself, and may only release it if it
    /// still holds it. This scoping is load-bearing because every surface watches the same state independently — an
    /// unscoped "I am not mounted" from the mini player would erase the pop-out's claim moments after it opened, and the
    /// reality field would settle on a lie (which is the very thing intent-vs-reality exists to prevent).
    /// </summary>
    /// <param name="live">The currently recorded live placement.</param>
    /// <param name="surface">The reporting surface's own placement.</param>
    /// <param name="mounted">Whether that surface is mounted right now.</param>
    public static SurfacePlacement LiveAfterReport(SurfacePlacement live, SurfacePlacement surface, bool mounted)
        => mounted ? surface
         : live == surface ? SurfacePlacement.None
         : live;

    /// <summary>Enter fullscreen, remembering where to go back to. Never touches <see cref="PlacementState.Preferred"/>
    /// — fullscreen is a mode, and persisting it would trap the user in it on the next launch.</summary>
    public static PlacementState EnterFullscreen(in PlacementState s) => s with
    {
        ReturnTo = s.Requested == SurfacePlacement.Fullscreen ? s.ReturnTo : s.Requested,
        Requested = SurfacePlacement.Fullscreen,
    };

    /// <summary>Leave fullscreen for <see cref="PlacementState.ReturnTo"/> (or the preferred home if it entered from
    /// off). A no-op when not in fullscreen.</summary>
    public static PlacementState ExitFullscreen(in PlacementState s)
    {
        if (s.Requested != SurfacePlacement.Fullscreen) return s;
        var back = s.ReturnTo != SurfacePlacement.None ? s.ReturnTo : s.Preferred;
        return s with { Requested = back, ReturnTo = SurfacePlacement.None };
    }

    /// <summary>
    /// The user closed the surface by its OWN chrome (an OS ✕ / Alt+F4 on the detached window, the mini player's ✕).
    /// This is the transition that used to be missing entirely, and it is why the toggle could be left lit pointing at
    /// a window that no longer existed:
    /// <list type="bullet">
    /// <item>Closing the DETACHED window means "not in a separate window", not "stop watching" → fall to the next
    /// available less-committing placement (the mini player) and make that the new preference. Only if nothing is left
    /// does it turn off. (The user still sees video, in the surface they fell back to — so this is not the "I closed the
    /// video" gesture; closing THAT fallback surface is, and it turns video off like any other close.)</item>
    /// <item>Closing an in-app surface is <see cref="TurnOff"/>: video is off, globally and stickily, until the user
    /// asks for it again. It used to be a per-song dismiss that expired on the next track — which is exactly the
    /// "I closed the video and the next song opened it again" complaint (fixed 2026-07-26).</item>
    /// <item>Leaving fullscreen by its own chrome is <see cref="ExitFullscreen"/>.</item>
    /// </list>
    /// A close reported for a placement that is no longer resolved is STALE (a newer placement already won the race)
    /// and is ignored — that is the identity guard the owner would otherwise have to open-code.
    /// </summary>
    public static PlacementState HostClosed(in PlacementState s, SurfacePlacement closed)
    {
        if (closed == SurfacePlacement.None || Resolve(s) != closed) return s;
        if (closed == SurfacePlacement.Fullscreen) return ExitFullscreen(s);
        if (closed != SurfacePlacement.Detached) return TurnOff(s);
        var next = FirstAvailable(SurfacePlacement.Floating, s.Available & ~PlacementSet.Detached);
        return next == SurfacePlacement.None ? TurnOff(s) : OpenAt(s, next);
    }

    // ── owner helpers ───────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>What the owner of ALL placements must do to reconcile <paramref name="live"/> with
    /// <paramref name="desired"/>.</summary>
    public static MountAction DecideMount(SurfacePlacement desired, SurfacePlacement live)
        => desired == live ? MountAction.None
         : live == SurfacePlacement.None ? MountAction.Open
         : desired == SurfacePlacement.None ? MountAction.Close
         : MountAction.Move;

    /// <summary>What the owner of ONE placement (e.g. the detached-window host) must do: it opens iff that exact
    /// placement is resolved, and closes whenever it is not — including when a DIFFERENT placement won.</summary>
    public static MountAction DecideOwned(SurfacePlacement resolved, SurfacePlacement owned, bool alive)
        => DecideMount(resolved == owned ? owned : SurfacePlacement.None, alive ? owned : SurfacePlacement.None);

    /// <summary>The async-resolve fence: an in-flight content resolve may only publish if the generation it captured
    /// when it started is still current — otherwise a superseded track's result would overwrite the current one (that
    /// is the "changed track, got the previous video" bug).</summary>
    public static bool IsCurrentGeneration(long capturedGen, long currentGen) => capturedGen == currentGen;

    // ── data-driven driver (property tests) ─────────────────────────────────────────────────────────────────────────

    /// <summary>Apply one command. Equivalent to calling the named transition; exists so arbitrary command SEQUENCES
    /// can be generated and checked against <see cref="Invariant"/>.</summary>
    public static PlacementState Apply(in PlacementState s, in PlacementCommand c) => c.Kind switch
    {
        PlacementCommandKind.TogglePrimary => TogglePrimary(s),
        PlacementCommandKind.OpenAt => OpenAt(s, c.Placement),
        PlacementCommandKind.TurnOff => TurnOff(s),
        PlacementCommandKind.Availability => WithAvailability(s, c.Available),
        PlacementCommandKind.HostClosed => HostClosed(s, c.Placement),
        PlacementCommandKind.EnterFullscreen => EnterFullscreen(s),
        PlacementCommandKind.ExitFullscreen => ExitFullscreen(s),
        PlacementCommandKind.LiveChanged => WithLive(s, c.Placement),
        _ => s,
    };

    /// <summary>The invariants that must hold after EVERY command, whatever the order:
    /// (1) a resolved placement is always actually available — the surface can never be asked to mount somewhere it
    /// cannot; (2) <see cref="PlacementState.Preferred"/> is always a real home to return to (never off, never the
    /// fullscreen mode); (3) nothing is resolved while the surface is off — and, since there is no longer any
    /// "hidden but still on" state, off is also the ONLY thing that hides a surface whose content supports it.</summary>
    public static bool Invariant(in PlacementState s)
    {
        var r = Resolve(s);
        if (r != SurfacePlacement.None && !Allows(s.Available, r)) return false;
        if (s.Preferred == SurfacePlacement.None || s.Preferred == SurfacePlacement.Fullscreen) return false;
        if (s.Requested == SurfacePlacement.None && r != SurfacePlacement.None) return false;
        return true;
    }
}
