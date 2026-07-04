using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// ── The authenticated account chip + dropdown + logout confirm ────────────────────────────────────────────────────────
// WinUI-desktop parity: the avatar top-right opens a flyout (account / settings / log out). Reuses the shell's already-
// mounted Overlay.Service (no new host). "Log out" opens a modal confirm → Services.LogoutAsync, which flips the gate back
// to the takeover with NO process restart. The avatar carries MorphId="account:me" for the success/logout morph.
sealed class ProfileMenu : Component
{
    static readonly ColorF Gold = ColorF.FromRgba(0xE6, 0xC2, 0x6C);
    static readonly ColorF GoldFill = ColorF.FromRgba(0xE9, 0xC4, 0x6A, 0x26);
    static readonly ColorF GoldLine = ColorF.FromRgba(0xE9, 0xC4, 0x6A, 0x55);

    readonly PlaybackBridge _b;
    readonly bool _showName;
    public ProfileMenu(PlaybackBridge b, bool showName) { _b = b; _showName = showName; }

    public override Element Render()
    {
        var services = UseContext(Services.Slot);
        var overlay = UseContext(Overlay.Service);
        var requestTheme = UseContext(ThemeControl.Request);
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);

        var user = _b.User.Value;   // subscribe → chip + menu header follow the session
        string name = string.IsNullOrWhiteSpace(user?.DisplayName) ? "—" : user!.DisplayName;
        bool premium = user?.IsPremium ?? false;
        string avatar = user?.AvatarUrl ?? "";
        string? email = user?.Email;
        var pic = PersonPicture.Create(avatar, 24f, displayName: name) with { MorphId = "account:me" };

        void Close() => handle.Value?.Close();

        void ConfirmLogout()
        {
            OverlayHandle? h = null;
            h = overlay.Open(
                () => NodeHandle.Null,
                () => ConfirmCard(
                    Loc.Get(Strings.Auth.LogoutConfirmTitle),
                    Loc.Get(Strings.Auth.LogoutConfirmBody),
                    Loc.Get(Strings.Auth.LogOut),
                    onConfirm: () => { h?.Close(); _ = services?.LogoutAsync(); },
                    onCancel: () => h?.Close()),
                FlyoutPlacement.BottomCenter,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.Modal, Chrome: PopupChrome.Modal));
        }

        void SetPalette(string id)
        {
            WaveeTheme.ApplyPalette(id, services?.Settings);
            requestTheme?.Invoke(250f);
        }

        void OpenMenu()
        {
            if (handle.Value is { IsOpen: true }) { Close(); return; }
            handle.Value = overlay.Open(
                () => anchor.Value,
                () => MenuContent(name, premium, avatar, email,
                    onAccount: () => { Close(); LoginView.OpenUrl("https://www.spotify.com/account"); },
                    onSettings: () => Close(),
                    onPalette: SetPalette,
                    onLogout: () => { Close(); ConfirmLogout(); }),
                FlyoutPlacement.BottomEdgeAlignedRight,
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss) { ConstrainToRootBounds = false });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return new BoxEl
        {
            Direction = 0, Gap = 8f, AlignItems = FlexAlign.Center, Height = 32f,
            Padding = new Edges4(4f, 0f, _showName ? 10f : 4f, 0f), Corners = CornerRadius4.All(WaveeRadius.Control),
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, Focusable = true,
            OnClick = OpenMenu, OnRealized = h => anchor.Value = h,
            Children = _showName ? new Element[] { pic, Caption(name).Primary() } : new Element[] { pic },
        };
    }

    // The dropdown: an account header (gold-ringed avatar + name + tier badge) over icon menu rows.
    static Element MenuContent(string name, bool premium, string avatar, string? email,
        Action onAccount, Action onSettings, Action<string> onPalette, Action onLogout) => new BoxEl
    {
        Direction = 1, MinWidth = 280f, Padding = new Edges4(0, 6, 0, 6),
        Children =
        [
            new BoxEl
            {
                Direction = 0, Gap = 12f, AlignItems = FlexAlign.Center, Padding = new Edges4(16, 12, 16, 14),
                Children =
                [
                    // The premium avatar gets a soft gold ring (matches the concept).
                    new BoxEl
                    {
                        Width = 46f, Height = 46f, Corners = CornerRadius4.All(23f), AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                        BorderWidth = premium ? 2f : 0f, BorderColor = premium ? GoldLine : ColorF.Transparent,
                        Children = [PersonPicture.Create(avatar, 40f, displayName: name)],
                    },
                    new BoxEl
                    {
                        Direction = 1, Gap = 5f, Grow = 1f, Basis = 0f,
                        Children =
                        [
                            BodyStrong(name) with { Size = 15f },
                            TierBadge(premium),
                            email is { Length: > 0 } ? new TextEl(email) { Size = 12f, Color = Tok.TextTertiary } : (Element)new BoxEl(),
                        ],
                    },
                ],
            },
            Divider(),
            MenuRow(Icons.Contact, Loc.Get(Strings.Auth.Account), onAccount, trailing: Icons.OpenInNewWindow),
            MenuRow(Icons.Settings, Loc.Get(Strings.Auth.Settings), onSettings),
            PaletteSwatchRow(onPalette),
            Divider(),
            MenuRow(Icons.SignOut, Loc.Get(Strings.Auth.LogOut), onLogout, danger: true),
        ],
    };

    static Element PaletteSwatchRow(Action<string> onPalette)
    {
        string active = Tok.Palette.Id;
        return new BoxEl
        {
            Direction = 1, Gap = 6f, Padding = new Edges4(14, 4, 14, 4), Margin = new Edges4(6, 0, 6, 0),
            Children =
            [
                new TextEl("Palette") { Size = 12f, Weight = 600, Color = Tok.TextTertiary },
                new BoxEl
                {
                    Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center,
                    Children =
                    [
                        PaletteSwatch("warm", "Warm", WaveeColors.PresetSwatch(Tok.WarmPalette), active, onPalette),
                        PaletteSwatch("slate", "Slate", WaveeColors.PresetSwatch(Tok.SlatePalette), active, onPalette),
                        PaletteSwatch("neutral", "Neutral", WaveeColors.PresetSwatch(Tok.NeutralPalette), active, onPalette),
                        PaletteSwatch("accent", "Accent", WaveeColors.PresetSwatch(Tok.AccentTintedPalette), active, onPalette),
                    ],
                },
            ],
        };
    }

    static Element PaletteSwatch(string id, string label, ColorF fill, string activeId, Action<string> onPalette)
    {
        bool on = activeId == id;
        return new BoxEl
        {
            Direction = 1, Gap = 4f, AlignItems = FlexAlign.Center, Width = 52f,
            Role = AutomationRole.Button, Focusable = true, OnClick = () => onPalette(id),
            Children =
            [
                new BoxEl
                {
                    Width = 28f, Height = 28f, Corners = CornerRadius4.All(14f), Fill = fill,
                    BorderWidth = on ? 2f : 1f,
                    BorderColor = on ? Tok.AccentDefault : Tok.StrokeControlDefault,
                },
                new TextEl(label) { Size = 10f, Color = on ? Tok.TextPrimary : Tok.TextTertiary },
            ],
        };
    }

    static Element Divider() => new BoxEl { Height = 1f, Margin = new Edges4(10, 4, 10, 4), Fill = Tok.StrokeDividerDefault };

    static Element MenuRow(string glyph, string label, Action onClick, bool danger = false, string? trailing = null)
    {
        ColorF fg = danger ? Tok.SystemFillCritical : Tok.TextPrimary;
        ColorF ig = danger ? Tok.SystemFillCritical : Tok.TextSecondary;
        return new BoxEl
        {
            Direction = 0, Height = 40f, AlignItems = FlexAlign.Center, Gap = 13f,
            Padding = new Edges4(14, 0, 14, 0), Margin = new Edges4(6, 1, 6, 1),
            Corners = CornerRadius4.All(WaveeRadius.Control),
            HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.MenuItem, Focusable = true, OnClick = onClick,
            Children =
            [
                new TextEl(glyph) { Size = 16f, FontFamily = Theme.IconFont, Color = ig },
                new TextEl(label) { Size = 14f, Weight = 600, Color = fg, Grow = 1f },
                trailing is { } t ? new TextEl(t) { Size = 13f, FontFamily = Theme.IconFont, Color = Tok.TextTertiary } : new BoxEl(),
            ],
        };
    }

    static Element TierBadge(bool premium)
    {
        if (!premium)
            return new BoxEl
            {
                AlignSelf = FlexAlign.Start, Padding = new Edges4(9, 2, 10, 3), Corners = CornerRadius4.All(11f),
                Fill = Tok.FillSubtleSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault,
                Children = [new TextEl(Loc.Get(Strings.Auth.FreeBadge)) { Size = 11.5f, Weight = 700, Color = Tok.TextSecondary }],
            };
        ColorF goldInk = Theme.Dark ? Gold : ColorF.FromRgba(0x8A, 0x63, 0x12);   // warm-Light: a darker amber reads on the pale badge
        return new BoxEl
        {
            AlignSelf = FlexAlign.Start, Direction = 0, Gap = 5f, AlignItems = FlexAlign.Center,
            Padding = new Edges4(8, 3, 10, 4), Corners = CornerRadius4.All(11f),
            Fill = GoldFill, BorderWidth = 1f, BorderColor = GoldLine,
            Children =
            [
                new TextEl(Icons.FavoriteStar) { Size = 11f, FontFamily = Theme.IconFont, Color = goldInk },
                new TextEl(Loc.Get(Strings.Auth.PremiumBadge)) { Size = 11.5f, Weight = 700, Color = goldInk },
            ],
        };
    }

    // A focused modal confirm card (reuses the engine's dialog tokens + the Overlay.Service modal chrome).
    static Element ConfirmCard(string title, string message, string confirmLabel, Action onConfirm, Action onCancel) => new BoxEl
    {
        Direction = 1, Width = 380f, MinWidth = 320f, MaxWidth = 420f,
        Corners = Radii.OverlayAll, Fill = Tok.FillSolidBase, BorderColor = Tok.StrokeSurfaceDefault, BorderWidth = 1f,
        Shadow = Elevation.Dialog, Padding = Edges4.All(24f), Gap = WaveeSpace.M,
        Children =
        [
            new TextEl(title) { Size = 20f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
            new TextEl(message) { Size = 14f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
            new BoxEl
            {
                Direction = 0, Gap = WaveeSpace.S, Justify = FlexJustify.End, Margin = new Edges4(0, WaveeSpace.M, 0, 0),
                Children =
                [
                    Button.Standard(Loc.Get(Strings.Auth.Cancel), onCancel) with { MinWidth = 96f },
                    Button.Accent(confirmLabel, onConfirm) with { MinWidth = 96f },
                ],
            },
        ],
    };
}
