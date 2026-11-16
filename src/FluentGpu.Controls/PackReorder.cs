using FluentGpu.Foundation;
using FluentGpu.Dsl;
using FluentGpu.Input;

namespace FluentGpu.Controls;

/// <summary>The reorder milestones a screen reader hears (see <see cref="PackReorder{T}.AnnounceText"/>).
/// <see cref="Edge"/>: a step that could not move ("is at the start / end").</summary>
public enum PackAnnounceKind : byte { Grab, Move, Drop, Cancel, Edge }

/// <summary>One announcement: the item, its 1-based position in its group, the group's size and the group ordinal.</summary>
public readonly record struct PackAnnounce(PackAnnounceKind Kind, string Key, int Position, int Count, int Group);

/// <summary>One committed move, in entry-index space of the list the grid was given (group heads are entries).</summary>
public readonly record struct PackMove(string Key, int From, int To);

/// <summary>Keyboard model of a <see cref="PackReorder{T}"/>. <see cref="LiftMode"/>: Space grabs, arrows move, Space or Enter
/// drops, Esc cancels.</summary>
public enum PackKeyboard : byte { None, LiftMode }

/// <summary>
/// Reorder for a <see cref="PackGrid"/>: a pointer lift (mouse past <see cref="MouseSlop"/>; touch after a hold), a 2-D
/// projection of the order while lifted (the app's list is untouched until the drop, so a cancel needs no undo and one drag
/// is one command), the concept's oscillation guard (a target triggers once, cannot trigger again until the pointer has left
/// it, and never sooner than <see cref="DwellMs"/> after the last), a slot outline at the packed rect, keyboard lift mode and
/// one-step command twins, drag-in of a foreign payload, and announcements on edges through <see cref="Announcer"/>.
/// </summary>
public sealed class PackReorder<T>
{
    public PackReorder(string kind) => Kind = kind;

    /// <summary>The drag kind the grid's tiles emit and the grid accepts.</summary>
    public string Kind { get; }

    // ── policy (read live) ──
    /// <summary>Group heads and rows never lift; null: every tile lifts.</summary>
    public Func<T, bool>? CanLift;
    /// <summary>(item, entry index): false refuses that landing (e.g. never above the first group head).</summary>
    public Func<T, int, bool>? CanLandAt;
    /// <summary>Classic: only in Edit Home; Zune: only the selected tile. Null: always.</summary>
    public Func<bool>? Enabled;
    /// <summary>Inside another tile this long: the order changes.</summary>
    public float DwellMs = 140f;
    /// <summary>DIP a mouse press travels before it lifts.</summary>
    public float MouseSlop = 6f;
    public DragVisualStyle LiftStyle = new() { Scale = 1.03f };
    /// <summary>The theme's dashed outline at the packed rect of the lifted item (grid space).</summary>
    public Func<RectF, Element>? Slot;
    public PackKeyboard Keyboard = PackKeyboard.None;

    // ── foreign payloads (WinUI's add band, a card dragged onto Classic's Pinned group) ──
    public string[] AcceptKinds = [];
    public Func<object?, bool>? CanAccept;
    /// <summary>The provisional item shown under the pointer while a foreign payload hovers the grid.</summary>
    public Func<object?, T?>? Materialize;

    // ── commits: each is ONE document command in the app ──
    public Action<PackMove>? OnCommit;
    /// <summary>(payload, entry index) of a foreign drop.</summary>
    public Action<object?, int>? OnForeignCommit;
    /// <summary>The lifted key of a lift that ended without a commit.</summary>
    public Action<string>? OnCancel;

    // ── a11y ──
    /// <summary>The app composes and localizes; null text says nothing.</summary>
    public Func<PackAnnounce, string?>? AnnounceText;
    public bool AnnounceAssertive = true;
    public float AnnounceThrottleMs = Announcer.DefaultThrottleMs;

    /// <summary>The mounted grid this reorder drives (bound at mount, cleared at unmount).</summary>
    internal IPackReorderHost? Host;

    // ── keyboard and command twins ──
    /// <summary>Keyboard lift of <paramref name="key"/>; false when it cannot lift.</summary>
    public bool Lift(string key) => Host?.Lift(key) ?? false;
    /// <summary>Drops the keyboard lift where the projection shows it (one <see cref="OnCommit"/> when it moved).</summary>
    public bool Drop() => Host?.Drop() ?? false;
    /// <summary>Cancels the lift in flight; the order goes back.</summary>
    public bool Cancel() => Host?.Cancel() ?? false;
    /// <summary>One step in order, committed at once (no lift).</summary>
    public bool MoveBy(string key, int delta) => Host?.MoveBy(key, delta) ?? false;
    /// <summary>Left / right: one step in order; up / down: onto the nearest entry above / below (the before/after rule).
    /// Committed at once, or moves the keyboard lift when one is in flight.</summary>
    public bool MoveToward(string key, int dx, int dy) => Host?.MoveToward(key, dx, dy) ?? false;
    public bool IsLifted => Host?.LiftedKey is not null;
    public string? LiftedKey => Host?.LiftedKey;
}

/// <summary>What a <see cref="PackReorder{T}"/> asks of the grid it is bound to.</summary>
internal interface IPackReorderHost
{
    bool Lift(string key);
    bool Drop();
    bool Cancel();
    bool MoveBy(string key, int delta);
    bool MoveToward(string key, int dx, int dy);
    string? LiftedKey { get; }
}
