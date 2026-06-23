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
    public SaveButton(string uri, float glyph = 16f, float box = 40f) { _uri = uri; _glyph = glyph; _box = box; }

    public override Element Render()
    {
        var lib = UseContext(LibraryBridge.Slot);
        if (lib is null) return new BoxEl();                 // no Mutations source → no affordance (capability gate)
        bool saved = lib.IsSaved(_uri);                      // subscribe → re-skin on any saved-set change
        return new BoxEl
        {
            Width = _box, Height = _box, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(_box / 2f),
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            HoverScale = 1.06f, PressScale = 0.9f,
            Role = AutomationRole.Button,
            OnClick = () => lib.ToggleSaved(_uri),
            Children = [Icon(saved ? Mdl.HeartFill : Icons.Heart, _glyph, saved ? Tok.AccentTextPrimary : Tok.TextSecondary)],
        };
    }
}

/// <summary>A Follow / Following pill — for artists + playlists (the "save" verb for a profile). Accent border + text when followed.</summary>
sealed class FollowButton : Component
{
    readonly string _uri;
    public FollowButton(string uri) { _uri = uri; }

    public override Element Render()
    {
        var lib = UseContext(LibraryBridge.Slot);
        if (lib is null) return new BoxEl();                 // capability gate
        bool following = lib.IsSaved(_uri);                  // subscribe
        return new BoxEl
        {
            Direction = 0, Height = 36f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(WaveeSpace.L, 0f, WaveeSpace.L, 0f), Corners = CornerRadius4.All(18f),
            BorderWidth = 1.5f, BorderColor = following ? Tok.AccentDefault : Tok.StrokeControlDefault,
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button,
            OnClick = () => lib.ToggleSaved(_uri),
            Children = [new TextEl(Loc.Get(following ? Strings.Artist.Following : Strings.Artist.Follow))
                { Size = 13f, Weight = 700, Color = following ? Tok.AccentTextPrimary : Tok.TextSecondary }],
        };
    }
}
