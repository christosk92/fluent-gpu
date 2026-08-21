using System;
using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Page 3 · Local playback (<c>data-step="3"</c>). REUSES <see cref="PlaybackRuntimeSetupModel"/> wholesale
/// (<c>Features/Shell/PlaybackRuntimeSetupCard.cs</c>) — <see cref="SetupPagePlaceholders"/>'s capture wrapper has
/// already constructed it (<see cref="SetupSession.EnsureRuntime"/>) by the time this page mounts, so the phase
/// this page shows is the exact same <see cref="PlaybackRuntimeSetupModel.PhaseSig"/> the footer reads.
///
/// <para>Every phase body below CALLS <c>SetupBody</c>'s own promoted per-phase arms (<see cref="SetupBody.Body"/>,
/// <see cref="SetupBody.BusyRow"/>, <c>Downloading()</c>, <see cref="SetupBody.Untrusted"/>, <c>Ready(...)</c>,
/// <c>Failed()</c>, <c>Advanced()</c>) instead of copying their layout — a second copy of, say, <c>Downloading</c>'s
/// byte formatting is exactly the drift this repo keeps catching. The Offer/Failed "Advanced options" disclosure
/// (folder pick / installed Spotify / choose-a-version / diagnostics) is this page's own composition, because the
/// standalone dialog puts that escape hatch in ITS footer — the wizard's shared footer has no such free slot, so it
/// lives in the body instead, exactly where the prototype puts it.</para></summary>
sealed class SetupLocalPlaybackPage : Component
{
    static readonly Dictionary<PlaybackRuntimeSetupModel.Phase, string> TitleKeys = new()
    {
        [PlaybackRuntimeSetupModel.Phase.Offer] = Strings.Playback.Runtime.Title,
        [PlaybackRuntimeSetupModel.Phase.FetchingCatalog] = Strings.Playback.Runtime.Title,
        [PlaybackRuntimeSetupModel.Phase.Downloading] = Strings.Playback.Runtime.Title,
        [PlaybackRuntimeSetupModel.Phase.Verifying] = Strings.Playback.Runtime.Title,
        [PlaybackRuntimeSetupModel.Phase.Untrusted] = Strings.Playback.Runtime.SignatureInvalid,
        [PlaybackRuntimeSetupModel.Phase.Ready] = Strings.Playback.Runtime.Ready,
        [PlaybackRuntimeSetupModel.Phase.Failed] = Strings.Playback.Runtime.Title,
        [PlaybackRuntimeSetupModel.Phase.Advanced] = Strings.Playback.Runtime.ChooseVersion,
    };

    public override Element Render()
    {
        var svc = UseContext(Services.Slot);
        var bridge = UseContext(PlaybackBridge.Slot);
        var overlay = UseContext(Overlay.Service);
        var go = UseContext(HistoryStore.NavCtx);
        var post = UsePost();

        var session = SetupSession.Current;
        var model = session?.Runtime;
        if (model is null && session is not null && svc?.Settings is { } settings0 && bridge is not null)
            model = session.EnsureRuntime(svc, settings0, bridge, post);

        Element body;
        string title;
        PlaybackRuntimeSetupModel.Phase phase;
        if (model is null)
        {
            body = SetupBody.Body(Loc.Get(Strings.Playback.Runtime.NotActive));
            title = Loc.Get(Strings.Playback.Runtime.Title);
            phase = PlaybackRuntimeSetupModel.Phase.Offer;
        }
        else
        {
            var helper = new SetupBody(model);
            phase = model.PhaseSig.Value;
            body = phase switch
            {
                PlaybackRuntimeSetupModel.Phase.Offer => OfferBody(model),
                PlaybackRuntimeSetupModel.Phase.FetchingCatalog => SetupBody.CatalogWaiting(),
                PlaybackRuntimeSetupModel.Phase.Downloading => helper.Downloading(),
                PlaybackRuntimeSetupModel.Phase.Verifying => helper.Verifying(),
                PlaybackRuntimeSetupModel.Phase.Untrusted => SetupBody.Untrusted(),
                PlaybackRuntimeSetupModel.Phase.Ready => helper.Ready(model.Status, overlay),
                PlaybackRuntimeSetupModel.Phase.Failed => FailedBody(model, helper, go),
                PlaybackRuntimeSetupModel.Phase.Advanced => helper.Advanced(),
                _ => new BoxEl(),
            };
            title = Loc.Get(TitleKeys[phase]);
        }

        body = body with
        {
            Key = "runtime:" + phase,
            Enter = new EnterExit(Dy: 6f, Opacity: 0f, Active: true),
            Exit = new EnterExit(Dy: -4f, Opacity: 0f, Active: true),
            Transition = MotionTok.StandardEnter,
        };

        return SetupPageHost.Frame(SetupPage.LocalPlayback, Loc.Get(Strings.Setup.Eyebrow.LocalPlayback), title, body);
    }

    // ── Offer: the lead + the SAME "Advanced options" disclosure the prototype nests inline on this page (the
    // standalone dialog reaches the equivalent Advanced() arm from its OWN footer link instead — a different door
    // to the same room). "Choose a version" hands off to ShowAdvanced(), which puts the model into Phase.Advanced —
    // the RadioButtons-driven Versions facet — exactly the existing seam, never a second picker built here. ────────
    static Element OfferBody(PlaybackRuntimeSetupModel model) => new BoxEl
    {
        Direction = 1, Gap = Spacing.M,
        Children =
        [
            SetupBody.Body(Loc.Get(Strings.Playback.Runtime.OfferBody)),
            AdvancedDisclosure(model, includeDiagnostics: null),
        ],
    };

    // ── Failed: the SAME error status arm the standalone dialog shows, plus the same disclosure — with a THIRD row
    // (diagnostics) when there is somewhere to send it, per the same "a link with nowhere to go should not exist"
    // rule PlaybackRuntimeSetupCard.SetupFooter's own Failed row now follows. ──────────────────────────────────────
    static Element FailedBody(PlaybackRuntimeSetupModel model, SetupBody helper, Action<string, string?>? go) => new BoxEl
    {
        Direction = 1, Gap = Spacing.M,
        Children =
        [
            helper.Failed(),
            AdvancedDisclosure(model, go),
        ],
    };

    static Element AdvancedDisclosure(PlaybackRuntimeSetupModel model, Action<string, string?>? includeDiagnostics)
    {
        var rows = new List<Element>
        {
            SetupBody.SettingRow(Icons.Folder, Loc.Get(Strings.Playback.Runtime.InstallFromFolder),
                Loc.Get(Strings.Playback.Runtime.InstallFromFolderCaption), model.PickFolder),
            SetupBody.SettingRow(Icons.MusicNote, Loc.Get(Strings.Playback.Runtime.UseInstalled),
                Loc.Get(Strings.Playback.Runtime.UseInstalledCaption), model.UseInstalled),
            SetupBody.SettingRow(Icons.List, Loc.Get(Strings.Playback.Runtime.ChooseVersion),
                Loc.Get(Strings.Setup.LocalPlayback.ChooseVersionCaption), model.ShowAdvanced),
        };
        if (includeDiagnostics is { } go)
            rows.Add(SetupBody.SettingRow(Icons.Important, Loc.Get(Strings.Playback.Runtime.ViewDiagnostics),
                Loc.Get(Strings.Setup.LocalPlayback.DiagnosticsCaption), () => model.OpenDiagnostics(go)));

        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Playback.Runtime.Advanced),
            Description = Loc.Get(Strings.Setup.LocalPlayback.AdvancedSub),
            Items = rows,
        }) with { Key = "setup:runtime:advanced" };
    }
}
