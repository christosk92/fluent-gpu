using System;
using System.Collections.Generic;
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
// to the takeover with NO process restart.
sealed class ProfileMenu : Component
{
    static readonly ColorF Gold = ColorF.FromRgba(0xE6, 0xC2, 0x6C);
    const float MenuWidth = 304f;

    readonly PlaybackBridge _b;
    readonly bool _showName;
    public ProfileMenu(PlaybackBridge b, bool showName) { _b = b; _showName = showName; }

    public override Element Render()
    {
        var services = UseContext(Services.Slot);
        var overlay = UseContext(Overlay.Service);
        var requestTheme = UseContext(ThemeControl.Request);
        var go = UseContext(HistoryStore.NavCtx);
        var actions = UseContext(ActionServices.Slot);   // the utility-command bag ("Play file…" needs Svc + Playback)
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);

        var user = _b.User.Value;   // subscribe → chip + menu header follow the session
        string name = string.IsNullOrWhiteSpace(user?.DisplayName) ? "—" : user!.DisplayName;
        bool premium = user?.IsPremium ?? false;
        string avatar = user?.AvatarUrl ?? "";
        string? email = user?.Email;
        var pic = PersonPicture.Create(avatar, 24f, displayName: name);

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
                    close: Close,
                    onAccount: () => { Close(); LoginView.OpenUrl("https://www.spotify.com/account"); },
                    onSettings: () => { Close(); go("settings", null); },
                    onPalette: SetPalette,
                    // "Play file…" is a GLOBAL utility (it belongs to no track), so it lives here rather than in the
                    // per-track menu — and it is absent, not disabled, when the backend cannot play anything locally.
                    onPlayFile: LocalFileActions.CanPlayFiles(actions)
                        ? () => { Close(); LocalFileActions.PickAndPlay(actions); }
                        : null,
                    onLogout: () => { Close(); ConfirmLogout(); }),
                FlyoutPlacement.BottomEdgeAlignedRight,
                // MENU chrome, not FlyoutPresenter: the body IS a MenuFlyout (an account header over
                // MenuFlyout.Create rows + a Palette sub-menu), so it takes MenuPopupThemeTransition — the anchored
                // 250ms unfold with the content readable from the first frame, over a windowed DWM-acrylic popup.
                // PopupChrome.Popup would give it the ordinary-Flyout PopupThemeTransition instead: 83ms of nothing,
                // then an 83ms fade across a 367ms 50px slide (uxtheme TAS_SHOWPOPUP) — correct for arbitrary flyout
                // CONTENT, wrong for a menu, and visibly unlike every other menu in the shell.
                new PopupOptions(FocusTrap: true, DismissBehavior: DismissBehavior.LightDismiss, Chrome: PopupChrome.Flyout)
                {
                    ConstrainToRootBounds = false,
                });
            handle.Value.ClosedAction = () => handle.Value = null;
        }

        return new BoxEl
        {
            Direction = 0, Gap = 8f, AlignItems = FlexAlign.Center, Height = 32f,
            Padding = new Edges4(4f, 0f, _showName ? 10f : 4f, 0f), Corners = CornerRadius4.All(Radii.Control),
            Role = AutomationRole.Button, Focusable = true,
            OnClick = OpenMenu, OnRealized = h => anchor.Value = h,
            Children = _showName ? new Element[] { pic, Caption(name).Primary() } : new Element[] { pic },
        }.Interactive(Interaction.Subtle);
    }

    // The dropdown: a compact account header over stock WinUI menu rows.
    static Element MenuContent(string name, bool premium, string avatar, string? email,
        Action close, Action onAccount, Action onSettings, Action<string> onPalette, Action? onPlayFile, Action onLogout)
    {
        string active = Tok.Palette.Id;
        var paletteItems = new MenuFlyoutItem[]
        {
            MenuFlyoutItem.RadioItem("Warm", active == "warm", () => onPalette("warm")),
            MenuFlyoutItem.RadioItem("Slate", active == "slate", () => onPalette("slate")),
            MenuFlyoutItem.RadioItem("Neutral", active == "neutral", () => onPalette("neutral")),
            MenuFlyoutItem.RadioItem("Accent", active == "accent", () => onPalette("accent")),
        };
        var rows = new List<MenuFlyoutItem>(7)
        {
            new(Loc.Get(Strings.Auth.Account), Icons.Contact, Invoke: onAccount),
            new(Loc.Get(Strings.Auth.Settings), Icons.Settings, Invoke: onSettings),
        };
        if (onPlayFile is not null)
            rows.Add(new MenuFlyoutItem(Loc.Get(Strings.LocalFile.PlayFile), Icons.MusicNote, Invoke: onPlayFile));
        rows.Add(MenuFlyoutItem.Separator);
        rows.Add(MenuFlyoutItem.SubMenu("Palette", paletteItems, Icons.Brush));
        rows.Add(MenuFlyoutItem.Separator);
        rows.Add(new MenuFlyoutItem(Loc.Get(Strings.Auth.LogOut), Icons.SignOut, Invoke: onLogout));
        var items = rows.ToArray();

        return new BoxEl
        {
            Direction = 1,
            MinWidth = MenuWidth,
            MaxWidth = MenuWidth,
            // 6 + the menu presenter's own MenuFlyoutPresenterThemePadding (0,2,0,2, applied by FlyoutSurface's
            // Flyout branch) = the 8px inset this card has always had.
            Padding = new Edges4(0, 6, 0, 6),
            Children =
            [
                AccountHeader(name, premium, avatar, email),
                HeaderSeparator(),
                MenuFlyout.Create(items, close, MenuWidth),
            ],
        };
    }

    static Element HeaderSeparator() => new BoxEl
    {
        Height = 1f,
        Margin = new Edges4(8, 4, 8, 4),
        Fill = Tok.StrokeDividerDefault,
    };

    static Element AccountHeader(string name, bool premium, string avatar, string? email) => new BoxEl
    {
        Direction = 0,
        Gap = 12f,
        AlignItems = FlexAlign.Center,
        Padding = new Edges4(14, 10, 14, 10),
        Children =
        [
            PersonPicture.Create(avatar, 40f, displayName: name),
            new BoxEl
            {
                Direction = 1,
                Gap = 2f,
                Grow = 1f,
                Basis = 0f,
                ClipToBounds = true,
                Children =
                [
                    new TextEl(name)
                    {
                        Size = 14f,
                        Weight = 600,
                        Color = Tok.TextPrimary,
                        MaxLines = 1,
                        Trim = TextTrim.CharacterEllipsis,
                    },
                    TierLine(premium),
                    email is { Length: > 0 }
                        ? new TextEl(email)
                        {
                            Size = 12f,
                            Color = Tok.TextTertiary,
                            MaxLines = 1,
                            Trim = TextTrim.CharacterEllipsis,
                        }
                        : new BoxEl(),
                ],
            },
        ],
    };

    static Element TierBadge(bool premium)
    {
        if (!premium)
            return new TextEl(Loc.Get(Strings.Auth.FreeBadge)) { Size = 12f, Color = Tok.TextSecondary };
        ColorF goldInk = Theme.Dark ? Gold : ColorF.FromRgba(0x8A, 0x63, 0x12);
        return new TextEl(Loc.Get(Strings.Auth.PremiumBadge)) { Size = 12f, Color = goldInk };
    }

    static Element TierLine(bool premium)
    {
        ColorF fg = premium ? (Theme.Dark ? Gold : ColorF.FromRgba(0x8A, 0x63, 0x12)) : Tok.TextSecondary;
        return new BoxEl
        {
            Direction = 0,
            Gap = 5f,
            AlignItems = FlexAlign.Center,
            Children =
            [
                premium ? Icon(Icons.FavoriteStar, 10f, fg) : new BoxEl { Width = 10f },
                TierBadge(premium),
            ],
        };
    }

    // A focused modal confirm card (reuses the engine's dialog tokens + the Overlay.Service modal chrome).
    static Element ConfirmCard(string title, string message, string confirmLabel, Action onConfirm, Action onCancel) => new BoxEl
    {
        Direction = 1, Width = 380f, MinWidth = 320f, MaxWidth = 420f,
        Corners = Radii.OverlayAll, Fill = Tok.FillSolidBase, BorderColor = Tok.StrokeSurfaceDefault, BorderWidth = 1f,
        Shadow = Elevation.Dialog, Padding = Edges4.All(24f), Gap = Spacing.M,
        Children =
        [
            new TextEl(title) { Size = 20f, Weight = 600, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
            new TextEl(message) { Size = 14f, Color = Tok.TextPrimary, Wrap = TextWrap.Wrap },
            new BoxEl
            {
                Direction = 0, Gap = Spacing.S, Justify = FlexJustify.End, Margin = new Edges4(0, Spacing.M, 0, 0),
                Children =
                [
                    Button.Standard(Loc.Get(Strings.Auth.Cancel), onCancel) with { MinWidth = 96f },
                    Button.Accent(confirmLabel, onConfirm) with { MinWidth = 96f },
                ],
            },
        ],
    };
}
