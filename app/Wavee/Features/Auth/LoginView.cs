using System;
using System.Threading.Tasks;
using FluentGpu.Animation;
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

// ── The full-screen LOGIN TAKEOVER (two-pane) ────────────────────────────────────────────────────────────────────────
// Mounted by WaveeApp's gate when logged out (real backend only). It PROJECTS the rich bridge.Login snapshot: the marquee
// AwaitingApproval state renders the two-pane "OR" card (browser Log in on the left, QR + pairing code on the right, both
// live at once — the device code polls in the background while the browser button races alongside); the other phases render
// a narrow status card. The coarse bridge.Auth flip to Authenticated swaps the whole takeover for the shell.
//
// Intents flow back to WaveeApp: onLoginBrowser (race the browser-loopback), onRetry (restart the device code), onClose
// (quit — the takeover is the whole window when logged out).
sealed class LoginView : Component
{
    internal const string CodeFont = "Consolas";   // a monospace face for the pairing code (Windows-resident; the app is Win-only)
    internal static readonly ColorF SpotifyGreen = ColorF.FromRgba(0x1D, 0xB9, 0x54);
    static readonly ColorF GoldTint = ColorF.FromRgba(0xE9, 0xC4, 0x6A);

    readonly Action _onLoginBrowser;
    readonly Action _onRetry;
    readonly Action _onClose;
    public LoginView(Action onLoginBrowser, Action onRetry, Action onClose)
    { _onLoginBrowser = onLoginBrowser; _onRetry = onRetry; _onClose = onClose; }

    public override Element Render()
    {
        var bridge = UseContext(PlaybackBridge.Slot);
        var snap = bridge?.Login.Value ?? new LoginSnapshot(LoginPhase.LoggedOut);   // subscribe → re-render on phase change

        // Screen-reader live region (UIA): announce the actionable state changes — the pairing code SPELLED OUT, and errors.
        // The announcer is wired by the Windows backend (InputHooks.Announce); null elsewhere → a silent no-op.
        var announce = InputHooks.Current.Default.Announce;
        Context.UseEffect(() =>
        {
            if (announce is null) return;
            switch (snap.Phase)
            {
                case LoginPhase.AwaitingApproval when snap.Challenge is { } c:
                    announce(Loc.Get(Strings.Auth.ScanToLogIn) + ". " + Loc.Get(Strings.Auth.OrGoTo) + " spotify.com/pair, " +
                             Loc.Get(Strings.Auth.EnterCodeColon) + " " + string.Join(" ", c.UserCode.Replace("-", "").ToCharArray()), false);
                    break;
                case LoginPhase.Failed:
                    announce(string.IsNullOrWhiteSpace(snap.Error) ? Loc.Get(Strings.Auth.NetworkError) : snap.Error!, true);
                    break;
                case LoginPhase.ChallengeExpired:
                    announce(Loc.Get(Strings.Auth.CodeExpired), true);
                    break;
            }
        }, (int)snap.Phase);

        Element card = snap.Phase switch
        {
            LoginPhase.AwaitingApproval when snap.Challenge is { } c
                                        => Embed.Comp(() => new TwoPaneLogin(c, _onLoginBrowser, _onClose)),
            LoginPhase.ChallengeExpired => Expired(),
            LoginPhase.Failed           => Failed(snap.Error),
            LoginPhase.PremiumRequired  => Premium(),
            LoginPhase.Finalizing       => Splash(Loc.Get(Strings.Auth.SigningIn), Loc.Get(Strings.Auth.SigningInSub)),
            _                           => Splash(Loc.Get(Strings.Auth.GettingCode), null),   // LoggedOut/SilentResume/RequestingCode/AwaitingBrowser
        };

        // Keyed cross-fade between screens (rise + fade); reduced-motion → instant (the engine honors Motion.ReducedMotion).
        string key = snap.Phase == LoginPhase.AwaitingApproval && snap.Challenge is { } ch ? "pair:" + ch.UserCode : "screen:" + snap.Phase;
        card = card with { Key = key };

        // Full-window Mica backdrop (the shell root runs Mica passthrough) → the centered card.
        return new BoxEl
        {
            Grow = 1f, Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = Edges4.All(WaveeSpace.XXL),
            Children = [card],
        };
    }

    // ── shared chrome ────────────────────────────────────────────────────────────────────────────────────────────────
    internal static BoxEl Card(params Element[] kids) => new BoxEl
    {
        Direction = 1, Width = 440f, MaxWidth = 440f, AlignItems = FlexAlign.Center, Gap = WaveeSpace.L,
        Padding = Edges4.All(32f), Corners = CornerRadius4.All(WaveeRadius.Card), Shadow = Elevation.Card,
        Fill = WaveeColors.Content, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Enter = new EnterExit(Dy: 8f, Opacity: 0f, Active: true),
        Exit = new EnterExit(Dy: -6f, Sx: 0.98f, Sy: 0.98f, Opacity: 0f, Active: true),   // success/dismiss: dissolve out as the shell rises
        Children = kids,
    };

    internal static Element Brand() => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center,
        Children = [new TextEl(Icons.MusicNote) { Size = 30f, FontFamily = Theme.IconFont, Color = Tok.AccentDefault }, WaveeType.PageHero("Wavee")],
    };

    internal static Element GlyphBadge(string glyph, ColorF tint) => new BoxEl
    {
        Width = 52f, Height = 52f, Corners = CornerRadius4.All(16f),
        AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Fill = ColorF.Lerp(Tok.FillCardSecondary, tint, 0.22f),
        Children = [new TextEl(glyph) { Size = 26f, FontFamily = Theme.IconFont, Color = tint }],
    };

    internal static Element CenteredText(TextEl t, float maxW = 330f) => new BoxEl
    {
        AlignSelf = FlexAlign.Stretch, AlignItems = FlexAlign.Center,
        Children = [t with { MaxWidth = maxW, Wrap = TextWrap.Wrap }],
    };

    internal static Element FullAccent(string label, Action onClick) =>
        Button.Accent(label, onClick) with { AlignSelf = FlexAlign.Stretch, MinHeight = 44f };

    internal static void OpenUrl(string url) => InputHooks.Current.Default.OpenUri?.Invoke(url);

    // ── narrow status screens ────────────────────────────────────────────────────────────────────────────────────────
    Element Splash(string status, string? sub) => Card(
        Brand(),
        new BoxEl { Margin = new Edges4(0, WaveeSpace.S, 0, WaveeSpace.S), AlignSelf = FlexAlign.Center, Children = [ProgressBar.Indeterminate(220f)] },
        BodyStrong(status),
        sub is null ? new BoxEl() : Caption(sub).Secondary());

    Element Expired() => Card(
        GlyphBadge(Icons.Important, Tok.SystemFillCaution),
        Subtitle(Loc.Get(Strings.Auth.CodeExpired)),
        CenteredText(Body(Loc.Get(Strings.Auth.CodeExpiredBody)).Secondary()),
        FullAccent(Loc.Get(Strings.Auth.GetNewCode), _onRetry),
        HyperlinkButton.Create(Loc.Get(Strings.Auth.Close), _onClose));

    Element Failed(string? error) => Card(
        GlyphBadge(Icons.Cancel, Tok.SystemFillCritical),
        Subtitle(Loc.Get(Strings.Auth.CouldntSignIn)),
        CenteredText(Body(string.IsNullOrWhiteSpace(error) ? Loc.Get(Strings.Auth.NetworkError) : error!).Secondary(), 350f),
        FullAccent(Loc.Get(Strings.Auth.TryAgain), _onRetry),
        HyperlinkButton.Create(Loc.Get(Strings.Auth.Close), _onClose));

    Element Premium() => Card(
        GlyphBadge(Icons.Important, GoldTint),
        Subtitle(Loc.Get(Strings.Auth.PremiumTitle)),
        CenteredText(Body(Loc.Get(Strings.Auth.PremiumBody)).Secondary(), 360f),
        new BoxEl
        {
            AlignSelf = FlexAlign.Stretch, Direction = 0, Gap = WaveeSpace.M, Justify = FlexJustify.Center,
            Children =
            [
                Button.Accent(Loc.Get(Strings.Auth.Upgrade), () => OpenUrl("https://www.spotify.com/premium")),
                Button.Standard(Loc.Get(Strings.Auth.UseAnotherAccount), _onRetry),
            ],
        },
        HyperlinkButton.Create(Loc.Get(Strings.Auth.Close), _onClose));
}

// ── The two-pane marquee (state 3 / AwaitingApproval) — image-7 layout ───────────────────────────────────────────────
// Left: the official Spotify browser login + the trademark disclaimer. Right: the QR + the pairing code + a copy button.
// An "OR" divider between. A bottom bar: a thin indeterminate poll bar (the device code is polling) + a Close (quit) button.
// Own component so the "copied" feedback is scoped (a fresh code remounts it via the Key on the parent).
sealed class TwoPaneLogin : Component
{
    const float CardW = 900f;

    readonly LoginChallenge _c;
    readonly Action _onLoginBrowser;
    readonly Action _onClose;
    public TwoPaneLogin(LoginChallenge c, Action onLoginBrowser, Action onClose)
    { _c = c; _onLoginBrowser = onLoginBrowser; _onClose = onClose; }

    public override Element Render()
    {
        var copied = UseSignal(false);

        var content = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Stretch, AlignSelf = FlexAlign.Stretch,
            Children = [LeftPane(), OrDivider(), RightPane(copied, () =>
            {
                InputHooks.Current.Default.Clipboard?.SetText(_c.UserCode);
                InputHooks.Current.Default.Announce?.Invoke(Loc.Get(Strings.Auth.Copied), false);   // screen-reader confirm
                copied.Value = true;
            })],
        };

        var bottom = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Stretch, Gap = WaveeSpace.L,
            Margin = new Edges4(0, WaveeSpace.L, 0, 0), Padding = new Edges4(0, WaveeSpace.L, 0, 0),
            BorderColor = Tok.StrokeDividerDefault, BorderWidth = 0f,
            Children =
            [
                new BoxEl { Grow = 1f },
                Button.Standard(Loc.Get(Strings.Auth.Close), _onClose) with { MinWidth = 96f },
            ],
        };

        // A top hairline above the bottom bar (the card's footer separator).
        var footerSep = new BoxEl { Height = 1f, AlignSelf = FlexAlign.Stretch, Margin = new Edges4(0, WaveeSpace.L, 0, 0), Fill = Tok.StrokeDividerDefault };

        return new BoxEl
        {
            Direction = 1, Width = CardW, MaxWidth = CardW, AlignItems = FlexAlign.Stretch,
            Padding = new Edges4(36f, 32f, 36f, 24f), Gap = 0f,
            Corners = CornerRadius4.All(WaveeRadius.Card), Shadow = Elevation.Card,
            Fill = WaveeColors.Content, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Enter = new EnterExit(Dy: 8f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -6f, Sx: 0.98f, Sy: 0.98f, Opacity: 0f, Active: true),   // success: dissolve out as the shell rises in
            Children = [content, footerSep, bottom],
        };
    }

    Element LeftPane() => new BoxEl
    {
        Direction = 1, Grow = 1f, Basis = 0f, Gap = WaveeSpace.L, Justify = FlexJustify.Start,
        Padding = new Edges4(4f, 8f, 36f, 8f),
        Children =
        [
            // Spotify wordmark (green) — identification use; the disclaimer below states the trademark.
            new BoxEl
            {
                Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center,
                Children =
                [
                    new TextEl(Icons.MusicNote) { Size = 30f, FontFamily = Theme.IconFont, Color = LoginView.SpotifyGreen },
                    new TextEl("Spotify") { Size = 26f, Weight = 700, Color = LoginView.SpotifyGreen },
                ],
            },
            new TextEl(Loc.Get(Strings.Auth.SpotifySignInWeb)) { Size = 14f, LineHeight = 20f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MaxWidth = 330f },
            // The primary: open the official Spotify login in the system browser (PKCE loopback races the device code).
            new BoxEl
            {
                AlignSelf = FlexAlign.Stretch, Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, MinHeight = 48f,
                Corners = Radii.ControlAll, Fill = Tok.AccentDefault, HoverFill = Tok.AccentSecondary, PressedFill = Tok.AccentTertiary,
                BrushTransitionMs = Motion.ControlFaster, Role = AutomationRole.Button, Focusable = true, OnClick = _onLoginBrowser,
                Children =
                [
                    new TextEl(Loc.Get(Strings.Auth.LogIn)) { Size = 15f, Weight = 600, Color = Tok.TextOnAccentPrimary },
                    new TextEl(Icons.OpenInNewWindow) { Size = 14f, FontFamily = Theme.IconFont, Color = Tok.TextOnAccentPrimary },
                ],
            },
            new TextEl(Loc.Get(Strings.Auth.Disclaimer)) { Size = 11.5f, LineHeight = 17f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxWidth = 360f },
        ],
    };

    Element OrDivider() => new BoxEl
    {
        Direction = 1, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, Width = 48f, Gap = WaveeSpace.S,
        Children =
        [
            new BoxEl { Width = 1f, Grow = 1f, Fill = Tok.StrokeDividerDefault },
            new BoxEl
            {
                Padding = new Edges4(10f, 4f, 10f, 4f), Corners = CornerRadius4.All(11f), Fill = Tok.FillSubtleSecondary,
                Children = [new TextEl(Loc.Get(Strings.Auth.Or)) { Size = 11f, Weight = 700, Color = Tok.TextSecondary, CharSpacing = 40f }],
            },
            new BoxEl { Width = 1f, Grow = 1f, Fill = Tok.StrokeDividerDefault },
        ],
    };

    Element RightPane(Signal<bool> copied, Action onCopy) => new BoxEl
    {
        Direction = 1, Grow = 1f, Basis = 0f, Gap = WaveeSpace.M, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start,
        Padding = new Edges4(36f, 0f, 4f, 0f),
        Children =
        [
            Embed.Comp(() => new QrGrid(_c.VerificationUriComplete ?? _c.VerificationUri, 220f)),
            BodyStrong(Loc.Get(Strings.Auth.ScanToLogIn)),
            // "Go to [spotify.com/pair ↗] and enter this code:" — the link carries the external-open glyph + tight padding.
            new BoxEl
            {
                Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, AlignSelf = FlexAlign.Center,
                Children =
                [
                    Caption(Loc.Get(Strings.Auth.OrGoTo)).Secondary(),
                    LinkWithIcon(Loc.Get(Strings.Auth.PairUrl), _c.VerificationUri),
                    Caption(Loc.Get(Strings.Auth.EnterCodeColon)).Secondary(),
                ],
            },
            new BoxEl
            {
                Direction = 1, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Center,
                Children =
                [
                    new TextEl(_c.UserCode) { Size = 32f, Weight = 700, CharSpacing = 70f, FontFamily = LoginView.CodeFont, Color = Tok.TextPrimary },
                    new BoxEl
                    {
                        Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center,
                        Children = [Embed.Comp(() => new CopyButton(copied, onCopy)), OpenButton(_c.VerificationUriComplete ?? _c.VerificationUri)],
                    },
                ],
            },
            // Live "Waiting for you to authorize…" (animated dots) + the 1 Hz expiry countdown — replaces the static status.
            new BoxEl
            {
                Direction = 1, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center, AlignSelf = FlexAlign.Center, Margin = new Edges4(0, WaveeSpace.S, 0, 0),
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Gap = WaveeSpace.S, AlignItems = FlexAlign.Center,
                        Children = [Embed.Comp(() => new WaitingDots()), Caption(Loc.Get(Strings.Auth.WaitingApproval)).Secondary()],
                    },
                    Embed.Comp(() => new LoginCountdown(_c.Expiry)),
                ],
            },
        ],
    };

    // A compact inline link with a trailing external-open glyph (tight padding so it sits flush between the words).
    static Element LinkWithIcon(string text, string url) => new BoxEl
    {
        Direction = 0, Gap = 4f, AlignItems = FlexAlign.Center, Padding = new Edges4(3, 1, 3, 1), Corners = CornerRadius4.All(WaveeRadius.Control),
        HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
        Role = AutomationRole.Hyperlink, Focusable = true, OnClick = () => LoginView.OpenUrl(url),
        Children =
        [
            new TextEl(text) { Size = 13f, Weight = 600, Color = Tok.AccentTextPrimary },
            new TextEl(Icons.OpenInNewWindow) { Size = 11f, FontFamily = Theme.IconFont, Color = Tok.AccentTextPrimary },
        ],
    };

    // Open the pairing page (pre-filled with the code via VerificationUriComplete) in the system browser.
    static Element OpenButton(string url) => new BoxEl
    {
        Direction = 0, Gap = WaveeSpace.XS, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Height = 34f, MinWidth = 96f, Padding = new Edges4(10, 0, 12, 0), Corners = CornerRadius4.All(WaveeRadius.Control),
        Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
        BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault, BrushTransitionMs = Motion.ControlFaster,
        Role = AutomationRole.Button, Focusable = true, OnClick = () => LoginView.OpenUrl(url),
        Children =
        [
            new TextEl(Icons.OpenInNewWindow) { Size = 14f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary },
            new TextEl(Loc.Get(Strings.Auth.Open)) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary },
        ],
    };
}

// ── The copy button — an explicit scale-POP on the checkmark when copied (the proven anim-keyframe path, not a keyed Enter
// that the positional reconciler can update-in-place). Reads the `copied` SIGNAL (a component can't take a changing bool prop).
sealed class CopyButton : Component
{
    readonly Signal<bool> _copied;
    readonly Action _onClick;
    public CopyButton(Signal<bool> copied, Action onClick) { _copied = copied; _onClick = onClick; }

    public override Element Render()
    {
        bool copied = _copied.Value;   // subscribe → re-render on copy
        var iconRef = UseRef<NodeHandle>(default);
        UseEffect(() =>
        {
            if (!copied || Motion.ReducedMotion) return;
            var anim = Context.Anim;
            var scene = Context.Scene;
            if (anim is null || scene is null || iconRef.Value.IsNull || !scene.IsLive(iconRef.Value)) return;
            var pop = new Keyframe[] { new(0f, 0.3f, Easing.EaseOut), new(0.55f, 1.18f, Easing.EaseOut), new(1f, 1f, Easing.EaseInOut) };
            anim.Keyframes(iconRef.Value, AnimChannel.ScaleX, pop, 340f, loop: false);
            anim.Keyframes(iconRef.Value, AnimChannel.ScaleY, pop, 340f, loop: false);
        }, copied);

        return new BoxEl
        {
            Direction = 0, Gap = WaveeSpace.XS, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Height = 34f, MinWidth = 116f, Padding = new Edges4(10, 0, 12, 0), Corners = CornerRadius4.All(WaveeRadius.Control),
            Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
            BorderWidth = 1f, BorderColor = Tok.StrokeControlDefault, BrushTransitionMs = Motion.ControlFaster,
            Role = AutomationRole.Button, Focusable = true, OnClick = _onClick,
            Children =
            [
                new BoxEl
                {
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center, OnRealized = h => iconRef.Value = h,
                    Children = [new TextEl(copied ? Icons.Accept : Icons.Copy) { Size = 15f, FontFamily = Theme.IconFont, Color = copied ? Tok.AccentDefault : Tok.TextSecondary }],
                },
                new TextEl(copied ? Loc.Get(Strings.Auth.Copied) : Loc.Get(Strings.Auth.CopyCode)) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary },
            ],
        };
    }
}

// ── The animated "waiting" dots — a left-to-right opacity pulse (the prototype's authorize indicator) ─────────────────
// Each dot loops an opacity wave whose peak is phase-shifted, so the highlight travels across the three. Driven on the
// compositor via AnimEngine.Keyframes (no per-frame re-render); captured node handles, like ProgressBar's IndeterminateBar.
sealed class WaitingDots : Component
{
    public override Element Render()
    {
        var d0 = UseRef<NodeHandle>(default);
        var d1 = UseRef<NodeHandle>(default);
        var d2 = UseRef<NodeHandle>(default);
        UseEffect(() =>
        {
            if (Motion.ReducedMotion) return;   // reduced motion: leave the dots static (no pulse)
            var anim = Context.Anim;
            var scene = Context.Scene;
            if (anim is null || scene is null) return;
            void Drive(NodeHandle h, float peak)
            {
                if (h.IsNull || !scene.IsLive(h)) return;
                anim.Keyframes(h, AnimChannel.TranslateY, new Keyframe[]
                {
                    new(0f, 0f, Easing.EaseInOut),
                    new(peak, -5f, Easing.EaseInOut),                          // hop up
                    new(MathF.Min(peak + 0.26f, 0.98f), 0f, Easing.EaseInOut), // settle back
                    new(1f, 0f, Easing.Linear),
                }, 1100f, loop: true, displayRate: true);
            }
            Drive(d0.Value, 0.16f);   // STAGGERED peaks → a left-to-right hop wave (dot0 → dot1 → dot2)
            Drive(d1.Value, 0.28f);
            Drive(d2.Value, 0.40f);
        }, DepKey.Empty);
        Element Dot(Action<NodeHandle> cap) => new BoxEl { Width = 6f, Height = 6f, Corners = CornerRadius4.All(3f), Fill = Tok.AccentDefault, OnRealized = cap };
        return new BoxEl { Direction = 0, Gap = 5f, AlignItems = FlexAlign.Center, Children = [Dot(h => d0.Value = h), Dot(h => d1.Value = h), Dot(h => d2.Value = h)] };
    }
}

// ── The pairing-code expiry countdown ("Expires in mm:ss") — a per-SECOND signal write, never a per-frame read ────────
sealed class LoginCountdown : Component
{
    readonly DateTimeOffset _expiry;
    public LoginCountdown(DateTimeOffset expiry) => _expiry = expiry;

    public override Element Render()
    {
        var post = Context.UsePost();
        var tick = UseSignal(0);
        var ticker = UseAsyncCommand(cancelOnUnmount: true);
        UseEffect(() => ticker.Run(async ct =>
        {
            while (!ct.IsCancellationRequested)
            {
                await Task.Delay(1000, ct).ConfigureAwait(false);
                post(() => tick.Value++);   // marshal the 1 Hz write to the UI thread (the loop runs off-thread)
            }
        }), DepKey.Empty);
        _ = tick.Value;   // subscribe → re-render each second

        var remaining = _expiry - DateTimeOffset.UtcNow;
        if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
        string txt = ((int)remaining.TotalMinutes).ToString("00") + ":" + remaining.Seconds.ToString("00");
        return new BoxEl
        {
            Direction = 0, Gap = WaveeSpace.XS, AlignItems = FlexAlign.Center,
            Padding = new Edges4(10, 4, 11, 5), Corners = CornerRadius4.All(11f), Fill = Tok.FillSubtleSecondary,
            Children =
            [
                new TextEl(Icons.Clock) { Size = 12f, FontFamily = Theme.IconFont, Color = Tok.TextTertiary },
                new TextEl(Strings.Auth.ExpiresIn(txt)) { Size = 12f, Color = Tok.TextSecondary },
            ],
        };
    }
}
