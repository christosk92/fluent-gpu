using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// The Mutations-facet affordances (docs/architecture.md §8.3 "capability-driven affordances"). Both read the live
// saved-set off LibraryBridge so they re-skin the instant the state flips (optimistic) and survive a restart (persisted).
// They render NOTHING when no Mutations source is connected — the affordance is GATED on the declared capability, not
// hardcoded. Used by the detail rail, the track rows' player bar, the artist page and the about-artist card.

/// <summary>A like / save heart — filled (accent) when the uri is saved, outline otherwise. For tracks (like) + albums (save).</summary>
sealed class SaveButton : Component
{
    readonly string _uri;
    readonly float _glyph;
    readonly float _box;
    readonly string? _name;   // display-only: names the item in the notification-center activity entry
    public SaveButton(string uri, float glyph = 16f, float box = 40f, string? name = null) { _uri = uri; _glyph = glyph; _box = box; _name = name; }

    public override Element Render()
    {
        var lib = UseContext(LibraryBridge.Slot);
        if (lib is null) return new BoxEl();                 // no Mutations source → no affordance (capability gate)
        bool saved = lib.IsSaved(_uri);                      // subscribe → re-skin on any saved-set change
        return new BoxEl
        {
            Width = _box, Height = _box, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(_box / 2f),
            HoverScale = 1.06f, PressScale = 0.9f,
            Role = AutomationRole.Button,
            OnClick = () => lib.ToggleSaved(_uri, _name),
            Children = [Icon(saved ? Icons.HeartFill : Icons.Heart, _glyph, saved ? Tok.AccentTextPrimary : Tok.TextSecondary)],
        }.Interactive(Interaction.Subtle);
    }
}

/// <summary>A Follow / Following pill — for artists + playlists (the "save" verb for a profile). Accent border + text when followed.</summary>
sealed class FollowButton : Component
{
    readonly string _uri;
    readonly string? _name;   // display-only: names the profile in the notification-center activity entry
    readonly ColorF? _foreground;
    public FollowButton(string uri, string? name = null, ColorF? foreground = null)
    { _uri = uri; _name = name; _foreground = foreground; }

    public override Element Render()
    {
        var lib = UseContext(LibraryBridge.Slot);
        if (lib is null) return new BoxEl();                 // capability gate
        bool following = lib.IsSaved(_uri);                  // subscribe
        ColorF idleInk = _foreground ?? Tok.TextPrimary;
        return new BoxEl
        {
            Direction = 0, Height = 36f, Gap = Spacing.S, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(Spacing.L, 0f, Spacing.L, 0f), Corners = CornerRadius4.All(18f),
            BorderWidth = 1.5f, BorderColor = following ? Tok.AccentDefault
                : _foreground is { } fg ? fg with { A = 0.42f } : Tok.StrokeControlDefault,
            HoverFill = _foreground is { } hover ? hover with { A = 0.12f } : Tok.FillSubtleSecondary,
            PressedFill = _foreground is { } press ? press with { A = 0.18f } : Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Cursor = CursorId.Hand,
            OnClick = () => lib.ToggleSaved(_uri, _name),
            Children =
            [
                Icon(following ? Icons.HeartFill : Icons.Heart, 14f, following ? Tok.AccentTextPrimary : idleInk),
                new TextEl(Loc.Get(following ? Strings.Artist.Following : Strings.Artist.Follow))
                    { Size = 13f, Weight = 700, Color = following ? Tok.AccentTextPrimary : idleInk },
            ],
        };
    }

    // The skeleton shape the deriver walks (SkeletonProxy at the Embed.Comp site): the real pill so it shimmers as a
    // bordered pill, not a full-width default bar that would stretch across the actions row.
    public static Element SkeletonShape() => new BoxEl
    {
        Direction = 0, Height = 36f, Gap = Spacing.S, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Padding = new Edges4(Spacing.L, 0f, Spacing.L, 0f), Corners = CornerRadius4.All(18f),
        BorderWidth = 1.5f, BorderColor = Tok.StrokeControlDefault,
        Children =
        [
            Icon(Icons.Heart, 14f, Tok.TextPrimary),
            new TextEl(Loc.Get(Strings.Artist.Follow)) { Size = 13f, Weight = 700, Color = Tok.TextPrimary },
        ],
    };
}
