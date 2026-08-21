using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Page 8 · Done (<c>data-step="8"</c>) — the second Zune bookend. The summary is the normal state; the
/// applying pane remains as an honest fallback for a restored in-flight session, but opening Wavee never waits on it.
/// Every required choice was persisted on its own page, and background sidebar/library/playback work continues behind
/// the shell, so <see cref="SetupSession.Primary"/> completes and closes synchronously.</summary>
sealed class SetupDonePage : Component
{
    public override Element Render()
    {
        var session = SetupSession.Current;
        var svc = UseContext(Services.Slot);
        var settings = svc?.Settings;
        var bridge = UseContext(PlaybackBridge.Slot);
        var viewport = UseContextSignal(Viewport.Size);
        var applyState = session?.Apply.Value ?? SetupApplyState.Idle;   // subscribe → re-render on Idle→Running→Done
        bool applying = applyState != SetupApplyState.Idle;
        var failedSig = UseSignal(false);   // never flips true today — see SetupStepList's own doc comment

        float plateW = SetupLayout.PlateWidth(viewport.Value.Width);
        var tierSig = UseSignal(SetupLayout.NominalTierFor(plateW));
        UseEffect(() =>
        {
            var current = tierSig.Peek();
            var next = SetupLayout.TierFor(plateW, current);
            if (next != current) tierSig.Value = next;
        }, plateW);
        bool wide = SetupLayout.ShowsHero(tierSig.Value);

        Element pane = applying
            ? ApplyingPane(session, failedSig)
            : SummaryPane(settings, session, bridge, wide);
        pane = pane with
        {
            Key = "done:" + (applying ? "applying" : "summary"),
            Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
        };

        return SetupPageHost.Frame(SetupPage.Done, "", "", pane, pinnedHeader: false);
    }

    // ── Summary — the Zune bookend: kicker, "You're **in**." (mixed weight), a lead, then the chip row. ────────────
    static Element SummaryPane(IAppSettings? settings, SetupSession? session, PlaybackBridge? bridge, bool wide)
    {
        System.Func<TextSpan[], SpanTextEl> headlineBuilder = wide ? SetupType.Display : SetupType.Small;

        // The prototype personalizes this line ("You're **in**, Christos."). The display name is whatever the real
        // profile reported, so read it live — it arrives with the Finalizing→Authenticated snapshot, which can land
        // while this page is already mounted. The comma and the full stop live INSIDE the loc value, not concatenated
        // here: ", {name}." punctuation and word order are the translator's call.
        string? who = bridge?.Login.Value.User?.DisplayName;
        string suffix = string.IsNullOrWhiteSpace(who)
            ? Loc.Get(Strings.Setup.Done.HeadlineSuffixPlain)
            : Strings.Setup.Done.HeadlineSuffixNamed(who!);

        Element headline = headlineBuilder(
        [
            new TextSpan(Loc.Get(Strings.Setup.Done.HeadlinePrefix)),
            new TextSpan(Loc.Get(Strings.Setup.Done.HeadlineBold), Weight: 600),
            new TextSpan(suffix),
        ]) with { MaxWidth = 480f };

        return new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinHeight = 0f, Justify = FlexJustify.Center,
            Children =
            [
                new TextEl(Loc.Get(Strings.Setup.Complete))
                {
                    Size = 11f, Weight = 600, CharSpacing = WaveeType.EyebrowTracking, Color = Tok.AccentTextPrimary,
                    Margin = new Edges4(0f, 0f, 0f, 14f),
                },
                headline,
                SetupRows.Lead(Loc.Get(Strings.Setup.Done.Lead)) with { MaxWidth = 480f, Margin = new Edges4(0f, 0f, 0f, 18f) },
                new BoxEl { Direction = 0, Wrap = true, Gap = Spacing.S, Children = BuildChips(settings, session, bridge) },
                new TextEl(Loc.Get(Strings.Setup.Done.Fine))
                    { Size = 11.5f, LineHeight = 17f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, Margin = new Edges4(14f, 0f, 0f, 0f) },
            ],
        };
    }

    static Element[] BuildChips(IAppSettings? settings, SetupSession? session, PlaybackBridge? bridge)
    {
        if (settings is null) return [];

        string themeLabel = settings.Get(WaveeSettings.ThemeMode) switch
        {
            1 => Loc.Get(Strings.Settings.Choice.Light),
            2 => Loc.Get(Strings.Settings.Choice.Dark),
            _ => Loc.Get(Strings.Settings.Choice.System),
        };
        string sidebarLabel = (SidebarDesign)settings.Get(WaveeSettings.SidebarDesign) switch
        {
            SidebarDesign.Classic => Loc.Get(Strings.Sidebar.Design.Classic),
            SidebarDesign.LibraryV3 => Loc.Get(Strings.Sidebar.Design.V3),
            _ => Loc.Get(Strings.Sidebar.Design.Custom),
        };
        string qualityLabel = settings.Get(WaveeSettings.PlaybackQuality) switch
        {
            0 => Loc.Get(Strings.Settings.Playback.QualityNormal),
            1 => Loc.Get(Strings.Settings.Playback.QualityHigh),
            _ => Loc.Get(Strings.Settings.Playback.QualityVeryHigh),
        };
        bool crossfade = settings.Get(WaveeSettings.CrossfadeEnabled);
        bool notifyWindows = settings.Get(WaveeSettings.NotifyWindows);
        bool runtimeOn = !(session?.RuntimeDeclined ?? false) && (bridge?.RuntimeStatus.Value.IsReady ?? false);

        return
        [
            Chip(true, themeLabel),
            Chip(true, sidebarLabel),
            Chip(true, qualityLabel),
            Chip(crossfade, Loc.Get(Strings.Settings.Sound.Crossfade)),
            Chip(runtimeOn, Loc.Get(Strings.Playback.Runtime.Title)),
            Chip(notifyWindows, Loc.Get(Strings.Settings.Notify.Windows)),
        ];
    }

    static Element Chip(bool on, string label) => new BoxEl
    {
        Direction = 0, Gap = 6f, AlignItems = FlexAlign.Center, Height = 28f, Shrink = 0f,
        Padding = new Edges4(10f, 0f, 10f, 0f), Corners = CornerRadius4.All(14f),
        Fill = Tok.FillCardSecondary,
        Children =
        [
            new BoxEl { Width = 9f, Height = 9f, Corners = CornerRadius4.All(4.5f), Fill = on ? Tok.AccentDefault : Tok.TextTertiary },
            new TextEl(label) { Size = 12.5f, Color = Tok.TextSecondary },
        ],
    };

    // ── Applying — the same Zune kicker/headline treatment, then the SetupStepList checklist. ───────────────────────
    static Element ApplyingPane(SetupSession? session, Signal<bool> failed)
    {
        Element headline = SetupType.Small(
        [
            new TextSpan(Loc.Get(Strings.Setup.Done.ApplyingHeadlinePrefix)),
            new TextSpan(Loc.Get(Strings.Setup.Done.ApplyingHeadlineBold), Weight: 600),
            new TextSpan("."),
        ]);

        Element steps = session is not null
            ? SetupStepList.Column(session.ApplyStage, failed,
              [
                  (0, Loc.Get(Strings.Setup.Done.StepSettings)),
                  (1, Loc.Get(Strings.Setup.Done.StepSidebar)),
                  (2, Loc.Get(Strings.Setup.Done.StepLibrary)),
                  (3, Loc.Get(Strings.Setup.Done.StepRuntime)),
              ])
            : new BoxEl();

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.M,
            Children =
            [
                new TextEl(Loc.Get(Strings.Setup.Done.ApplyingKicker))
                {
                    Size = 11f, Weight = 600, CharSpacing = WaveeType.EyebrowTracking, Color = Tok.AccentTextPrimary,
                    Margin = new Edges4(0f, 0f, 0f, 14f),
                },
                headline,
                new BoxEl { Margin = new Edges4(18f, 0f, 0f, 0f), Children = [steps] },
                new TextEl(Loc.Get(Strings.Setup.Done.ApplyingFine))
                    { Size = 11.5f, LineHeight = 17f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, Margin = new Edges4(16f, 0f, 0f, 0f) },
            ],
        };
    }
}
