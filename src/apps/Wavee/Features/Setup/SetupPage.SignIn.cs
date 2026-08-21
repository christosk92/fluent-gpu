using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Page 2 · Sign in (<c>data-step="2"</c>). NO NEW STATE: the truth is <see cref="PlaybackBridge.Login"/> +
/// <see cref="PlaybackBridge.Auth"/>, folded by the already-written pure <see cref="SetupCommands.Project"/> into
/// the six <see cref="SetupSignInPhase"/> facets this page's LEFT PANE switches on — the footer reads the exact
/// same two signals through the exact same projection (<see cref="SetupSession.BuildCtx"/>), so neither ever drifts
/// from the other.
///
/// <para>Layout mirrors the prototype's 3-column row (left pane / OR divider / compact QR pane), reusing
/// <see cref="LoginView.OrDivider"/>/<see cref="LoginView.CompactRightPane"/> for the QR column, and the takeover's own
/// <see cref="LoginStepBar"/>/
/// <see cref="LoginStepRow"/> for the busy state — none of that is re-authored here.</para></summary>
sealed class SetupSignInPage : Component
{
    public override Element Render()
    {
        var bridge = UseContext(PlaybackBridge.Slot);
        var viewport = UseContextSignal(Viewport.Size);
        var snap = bridge?.Login.Value ?? new LoginSnapshot(LoginPhase.LoggedOut);   // subscribe → re-render on phase change
        var auth = bridge?.Auth.Value ?? AuthStatus.LoggedOut;                       // subscribe → re-render on the auth flip
        var facet = SetupCommands.Project(snap.Phase, snap.Step, auth);
        var session = SetupSession.Current;
        var activePage = session?.Page.Value ?? SetupPage.SignIn;
        bool needsChallenge = SetupCommands.NeedsPairingChallenge(
            activePage, snap.Phase, snap.Challenge is not null);
        UseEffect(() =>
        {
            if (needsChallenge) session?.RestartCode?.Invoke();
        }, needsChallenge);
        float plateW = SetupLayout.PlateWidth(viewport.Value.Width);
        var tierSig = UseSignal(SetupLayout.NominalTierFor(plateW));
        UseEffect(() =>
        {
            var current = tierSig.Peek();
            var next = SetupLayout.TierFor(plateW, current);
            if (next != current) tierSig.Value = next;
        }, plateW);
        var tier = tierSig.Value;

        // Auto-advance to LocalPlayback ~900ms after Authenticated — the prototype's own beat — but ONLY if the user
        // is still on this page when the timer fires (Peek: they may have gone Back in the meantime).
        UseTimeout(() =>
        {
            var session = SetupSession.Current;
            if (session is null) return;
            if (bridge?.Auth.Peek() == AuthStatus.Authenticated && session.Page.Peek() == SetupPage.SignIn)
                session.Advance(SetupGating.NextPage(SetupPage.SignIn, session.SkipSignIn));
        }, 900f, auth == AuthStatus.Authenticated);

        Element left = LeftPane(facet, snap, bridge, session?.StartBrowser) with
        {
            Key = "signin:" + facet,
            Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
            Transition = MotionTok.StandardEnter,
        };

        // The QR pane dims to ~22% while Busy (the prototype's `.login.busy`) and disappears once the flow has
        // moved past it (Done/Failed/Expired/Premium) — showing a live pairing code next to a result screen reads
        // as a second, contradicting affordance.
        float rightOpacity = facet switch { SetupSignInPhase.Idle => 1f, SetupSignInPhase.Busy => 0.22f, _ => 0f };
        bool rightInteractive = facet == SetupSignInPhase.Idle;

        // The pairing code is minted asynchronously (and deliberately not until the wizard REACHES this page, so it
        // cannot expire while the user reads Welcome/Terms). Until it lands, show that it is coming: an empty column
        // here left the "OR" divider dangling beside nothing, which reads as a broken layout rather than a wait.
        Element right = snap.Challenge is { } challenge
            ? LoginView.CompactRightPane(challenge)
            : new BoxEl
            {
                Width = SetupLayout.CompactPairingWidth, Shrink = 0f, Direction = 1, AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center, Gap = Spacing.M, MinHeight = 180f,
                Children =
                [
                    ProgressRing.Indeterminate(size: 24f),
                    new TextEl(Loc.Get(Strings.Auth.GettingCode))
                    {
                        Size = 12.5f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxWidth = 176f,
                    },
                ],
            };
        right = right with
        {
            Key = snap.Challenge is { } c ? "signin:challenge:" + c.UserCode : "signin:challenge:pending",
            Enter = new EnterExit(Dy: 4f, Sx: 0.97f, Sy: 0.97f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -2f, Opacity: 0f, Active: true),
            Transition = MotionTok.StandardEnter,
        };

        Element leftHost = new BoxEl
        {
            Grow = 1f, Basis = 0f, Shrink = 1f, MinWidth = 0f,
            Padding = new Edges4(Spacing.XXS, Spacing.XXS, Spacing.XL, Spacing.S),
            Children = [left],
        };
        Element divider = new BoxEl
        {
            Shrink = 0f, AlignSelf = FlexAlign.Stretch, Opacity = rightOpacity,
            HitTestVisible = rightInteractive,
            Children = [LoginView.OrDivider(SetupLayout.CompactDividerWidth, SetupLayout.StacksSignIn(tier))],
        };
        Element rightHost = new BoxEl
        {
            Shrink = 0f, AlignSelf = SetupLayout.StacksSignIn(tier) ? FlexAlign.Center : FlexAlign.Stretch,
            Opacity = rightOpacity, HitTestVisible = rightInteractive, Children = [right],
        };

        Element login = SetupLayout.StacksSignIn(tier)
            ? new BoxEl
            {
                Key = "signin:layout:" + (int)tier,
                Direction = 1, Gap = Spacing.M, MinWidth = 0f, MinHeight = 0f,
                Children = rightOpacity > 0f ? [leftHost, divider, rightHost] : [leftHost],
            }
            : new BoxEl
            {
                Key = "signin:layout:" + (int)tier,
                Direction = 0, AlignItems = FlexAlign.Start, MinWidth = 0f, MinHeight = 0f,
                Children = [leftHost, divider, rightHost],
            };

        // The content lane is intentionally taller than the row itself. Centering the login composition in it restores
        // the old takeover's balanced vertical rhythm while the surrounding page scroller still handles short windows.
        Element body = new BoxEl
        {
            Direction = 1, MinWidth = 0f, MinHeight = SetupLayout.SignInBodyMinHeight,
            Justify = FlexJustify.Center,
            Children = [login],
        };

        return SetupPageHost.Frame(SetupPage.SignIn, Loc.Get(Strings.Setup.Eyebrow.SignIn),
            Loc.Get(Strings.Setup.SignIn.Title), body);
    }

    static Element LeftPane(SetupSignInPhase facet, LoginSnapshot snap, PlaybackBridge? bridge, Action? startBrowser) => facet switch
    {
        SetupSignInPhase.Idle => IdleLeft(startBrowser),
        SetupSignInPhase.Busy => BusyLeft(bridge),
        SetupSignInPhase.Done => DoneLeft(snap.User),
        SetupSignInPhase.Failed => FailedLeft(snap.Error),
        SetupSignInPhase.Expired => ExpiredLeft(),
        SetupSignInPhase.Premium => PremiumLeft(),
        _ => new BoxEl(),
    };

    // ── Idle: preserve the old takeover's identity and direct browser action inside the roomy left pane. ──
    static Element IdleLeft(Action? startBrowser) => new BoxEl
    {
        Direction = 1, Gap = Spacing.M,
        Children =
        [
            LoginView.SpotifyBrand(),
            SetupRows.Lead(Loc.Get(Strings.Auth.SpotifySignInWeb)),
            LoginView.BrowserLoginButton(startBrowser ?? Noop),
            new TextEl(Loc.Get(Strings.Auth.Disclaimer))
                { Size = 11.5f, LineHeight = 17f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap },
        ],
    };

    static void Noop() { }

    // ── Busy: the same step bar + four step rows the login takeover's own Finalizing splash uses, reading the SAME
    // bridge.Login signal — this page's own state, not a second dialog stacked on top of it. ─────────────────────
    static Element BusyLeft(PlaybackBridge? bridge)
    {
        if (bridge is null) return new BoxEl { Children = [new TextEl(Loc.Get(Strings.Auth.SigningIn)) { Size = 14f, Weight = 600, Color = Tok.TextPrimary }] };
        Element Row(LoginStep step, string label) => Embed.Comp(() => new LoginStepRow(bridge.Login, step, label));
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M,
            Children =
            [
                new TextEl(Loc.Get(Strings.Auth.SigningIn)) { Size = 14f, Weight = 600, Color = Tok.TextPrimary },
                new BoxEl { AlignSelf = FlexAlign.Start, Children = [Embed.Comp(() => new LoginStepBar(bridge.Login))] },
                new BoxEl
                {
                    Direction = 1, Gap = Spacing.XS, AlignSelf = FlexAlign.Stretch,
                    Stagger = Motion.ReducedMotion ? 0f : WaveeMotion.StaggerMs,
                    Children =
                    [
                        Row(LoginStep.Connecting, Loc.Get(Strings.Auth.StepConnecting)),
                        Row(LoginStep.Metadata, Loc.Get(Strings.Auth.StepMetadata)),
                        Row(LoginStep.Audio, Loc.Get(Strings.Auth.StepAudio)),
                        Row(LoginStep.Profile, Loc.Get(Strings.Auth.StepProfile)),
                    ],
                },
                new TextEl(Loc.Get(Strings.Setup.SignIn.BusyNote))
                    { Size = 11.5f, LineHeight = 17f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap },
            ],
        };
    }

    // ── Done: an identity card (avatar initial, display name, email, Premium pill) + a lead. ──────────────────────
    static Element DoneLeft(WaveeUser? user)
    {
        string initial = user?.DisplayName is { Length: > 0 } name ? name[..1].ToUpperInvariant() : "?";
        var kids = new List<Element>
        {
            new TextEl(user?.DisplayName ?? "") { Size = 14.5f, Weight = 600, Color = Tok.TextPrimary },
        };
        if (!string.IsNullOrWhiteSpace(user?.Email))
            kids.Add(new TextEl(user!.Email!) { Size = 12f, Color = Tok.TextSecondary });
        if (user?.IsPremium == true) kids.Add(PremiumPill());

        Element idCard = new BoxEl
        {
            Direction = 0, Gap = Spacing.M, AlignItems = FlexAlign.Center, Padding = Edges4.All(14f),
            Corners = CornerRadius4.All(Radii.Card), Fill = Tok.FillCardDefault,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl
                {
                    Width = 44f, Height = 44f, Corners = CornerRadius4.All(22f), Shrink = 0f,
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Fill = LoginView.SpotifyGreen,
                    Children = [new TextEl(initial) { Size = 17f, Weight = 600, Color = ColorF.FromRgba(11, 26, 18) }],
                },
                new BoxEl { Direction = 1, Gap = 3f, MinWidth = 0f, Children = kids.ToArray() },
            ],
        };

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M,
            Children = [idCard, SetupRows.Lead(Loc.Get(Strings.Setup.SignIn.DoneLead))],
        };
    }

    static Element PremiumPill() => new BoxEl
    {
        Direction = 0, Gap = 5f, AlignItems = FlexAlign.Center, Height = 19f, Shrink = 0f,
        Padding = new Edges4(8f, 0f, 8f, 0f), Corners = CornerRadius4.All(9.5f),
        Fill = LoginView.SpotifyGreen with { A = 0.24f },
        Children = [new TextEl(Loc.Get(Strings.Auth.PremiumBadge)) { Size = 11f, Weight = 600, Color = LoginView.SpotifyGreen }],
    };

    // ── Failed / Expired / Premium: an InfoBar, reusing the exact same copy the login takeover shows. ──────────────
    static Element FailedLeft(string? error) => InfoBar.Create(
        InfoBarSeverity.Error,
        Loc.Get(Strings.Auth.CouldntSignIn),
        string.IsNullOrWhiteSpace(error) ? Loc.Get(Strings.Auth.NetworkError) : error!);

    static Element ExpiredLeft() => InfoBar.Create(
        InfoBarSeverity.Error, Loc.Get(Strings.Auth.CodeExpired), Loc.Get(Strings.Auth.CodeExpiredBody));

    static Element PremiumLeft() => InfoBar.Create(
        InfoBarSeverity.Error, Loc.Get(Strings.Auth.PremiumTitle), Loc.Get(Strings.Auth.PremiumBody));
}
