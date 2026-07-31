using System;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>
/// The ZERO-PIN state, shared by every mode (R3.0: one renderer ⇒ one drop zone). A MOVE out of <c>WaveeSidebar.cs</c>
/// (where it lived while Classic hand-built its own pane) into shared scope, plus the R3.1.5 restyle.
///
/// <para>WHAT CHANGED AND WHY. At rest it used to be a 40-DIP row carrying ONE dim caption and no border — indistinguishable
/// from an empty-section hint, so nothing said "this is a target". It is now a real 56-DIP CARD: a solid 1px
/// <c>StrokeCardDefault</c> hairline, the pin mark, and two lines that name the gesture AND its alternative ("or use the
/// pin action on any library item"), so a user who will never drag still learns how to pin. The dashed accent border,
/// the <c>AccentSubtle</c> fill and the 72-DIP growth are now reserved for a live COMPATIBLE drag — the state where a
/// bigger, louder target is actually useful. That is the whole trade the user's screenshot review asked for: the empty
/// pinned section stops dominating the pane while the generous target survives exactly when it matters.</para>
///
/// <para>A TRACK drag shares the generic resource discriminator but fails the payload capability test, so no pin
/// affordance appears. Pin eligibility remains centralized in <c>WaveeResourceDragPayload.CanPin</c>.</para>
///
/// <para>Its own Component so <c>UseDragState</c> — which re-renders its consumer every frame while any typed drag is
/// live — is scoped to this card instead of the whole sidebar.</para>
/// </summary>
sealed class SidebarPinDropZone : Component
{
    /// <summary>The resting card height. Read by a virtualizing host that must size the slot before building the row
    /// (the pane's plan extent); the 56↔72 growth is a measured reflow on top of it.</summary>
    public const float RestHeight = 56f;

    /// <summary>The height while a compatible drag is live.</summary>
    public const float ActiveHeight = 72f;

    static readonly LayoutTransition Resize = new(
        TransitionChannels.Size, MotionTok.ContentResize.ToDynamics(),
        Size: SizeMode.Reflow, Anchor: SizeAnchor.Trailing);

    readonly Action<object?, int> _accept;

    public SidebarPinDropZone(Action<object?, int> accept) => _accept = accept;

    public override Element Render()
    {
        var drag = UseDragState();
        var over = UseSignal(false);
        var spec = UseMemo(() => new DropTargetSpec(
            [WaveeDragKinds.Resource],
            OnEnter: s => over.Value = WaveeResourceDrag.Unwrap(s.Payload) is { CanPin: true },
            OnOver: s => over.Value = WaveeResourceDrag.Unwrap(s.Payload) is { CanPin: true },
            OnLeave: _ => over.Value = false,
            OnDrop: s => { over.Value = false; _accept(s.Payload, 0); })
        {
            CanAccept = static s => WaveeResourceDrag.Unwrap(s.Payload) is { CanPin: true },
        }, DepKey.Empty);

        bool compatible = drag.Active
            && string.Equals(drag.Kind, WaveeDragKinds.Resource, StringComparison.Ordinal)
            && WaveeResourceDrag.Unwrap(drag.Payload) is { CanPin: true };
        bool hovering = over.Value;
        bool active = compatible || hovering;

        return new BoxEl
        {
            Key = "pins-empty",
            Height = active ? ActiveHeight : RestHeight,
            // The pane already owns the 8-DIP horizontal inset (SidebarPaneMetrics.PanePad), so the card only needs its
            // own trailing gap — a second 4-DIP inset here would push it out of the row band.
            Margin = new Edges4(0f, 0f, 0f, Spacing.XS),
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.S,
            Padding = new Edges4(Spacing.M, 0f, Spacing.M, 0f),
            Corners = Radii.ControlAll,
            DropTarget = spec,
            Fill = active ? Tok.AccentSubtle : ColorF.Transparent,
            // REST: a solid hairline card (it must read as a container, not as a caption).
            // ACTIVE: the dashed accent target — the dash pattern is what says "drop here", so it is drag-only.
            BorderColor = active ? Tok.AccentDefault : Tok.StrokeCardDefault,
            BorderWidth = 1f,
            BorderDashOn = active ? Spacing.XS : 0f,
            BorderDashOff = active ? Spacing.XXS : 0f,
            Transition = MotionTok.ControlFaster,
            Layout = Resize,
            Children =
            [
                Icon(Icons.Pin, 16f, active ? Tok.AccentTextPrimary : Tok.TextTertiary),
                new BoxEl
                {
                    Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, Gap = Spacing.XXS,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Sidebar.DropToPin))
                        {
                            Size = 12f, Weight = (ushort)(active ? 600 : 400),
                            Color = active ? Tok.AccentTextPrimary : Tok.TextSecondary,
                            MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                        // ONE line, ellipsised: the hint is a nudge, not a paragraph. (A two-line wrap is what made the
                        // resting state feel like a billboard.)
                        new TextEl(Loc.Get(Strings.Sidebar.Pin.EmptyHint))
                        {
                            Size = 11f, Color = Tok.TextTertiary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                },
            ],
        };
    }
}

/// <summary>
/// A "+" create affordance: a subtle icon button that creates a playlist directly. Shared by the pane's PlaylistTree
/// header, the compact rail's footer and Library V3's header/rail — a MOVE out of <c>WaveeSidebar.cs</c> (unchanged
/// visuals) so retiring Classic's hand-built body does not take it down with it.
///
/// <para>§3.1.6: it used to open a MenuFlyout offering "Playlist" / "Folder", but Spotify folder create/rename/delete
/// is deferred and the "Folder" arm was a no-op — dead UI that promises a command we do not have. The
/// flyout, the Overlay.Service/OverlayHandle plumbing and the anchor ref are gone with it; the button is a direct invoke
/// wearing the create-playlist tooltip.</para>
/// </summary>
sealed class SidebarCreateButton : Component
{
    readonly Action _onPlaylist;
    readonly float _box, _glyph;

    public SidebarCreateButton(Action onPlaylist, float box = 24f, float glyph = 14f)
    {
        _onPlaylist = onPlaylist; _box = box; _glyph = glyph;
    }

    public override Element Render() => ToolTip.Wrap(
        new BoxEl
        {
            Width = _box, Height = _box, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Corners = CornerRadius4.All(4f),
            Role = AutomationRole.Button, Cursor = CursorId.Hand, Focusable = true,
            OnClick = _onPlaylist,
            Children = [ Icon(Icons.Add, _glyph, Tok.TextSecondary) ],
        }.Interactive(Interaction.Subtle),
        Loc.Get(Strings.Sidebar.CreatePlaylistTooltip));
}
