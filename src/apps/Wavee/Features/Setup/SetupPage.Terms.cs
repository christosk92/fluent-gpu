using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Page 1 · Terms &amp; privacy (<c>data-step="1"</c>). Right column: a "what Wavee needs" trio of
/// <see cref="SetupCompact.InfoCard"/>s over trademark fine print, pinned to the floor at Wide (no scroll — the
/// column fits its own budget). Left stage: the hero rail plus a "Read the full agreement" link that swaps the rail
/// for a scrollable document card IN PLACE (never a Flyout — floats above the plate and fights the modal's own
/// Escape ordering — nor a TeachingTip — 336×520 cap, fixed 14-px body, wrong semantics; see the work-package plan).
/// Below Wide there is no stage column, so the same link + document swap is appended inline to the (already
/// scrollable) body instead.</summary>
sealed class SetupTermsPage : Component
{
    public override Element Render()
    {
        var viewport = UseContextSignal(Viewport.Size);
        float plateW = SetupLayout.PlateWidth(viewport.Value.Width);
        var tierSig = UseSignal(SetupLayout.NominalTierFor(plateW));
        UseEffect(() =>
        {
            var current = tierSig.Peek();
            var next = SetupLayout.TierFor(plateW, current);
            if (next != current) tierSig.Value = next;
        }, plateW);
        bool wide = SetupLayout.ShowsHero(tierSig.Value);

        var agreementOpen = UseSignal(false);
        void Open() => agreementOpen.Value = true;
        void Close() => agreementOpen.Value = false;

        Element LinkRow() => HyperlinkButton.Create(
            Loc.Get(Strings.Setup.Terms.ReadFull) + " · " + Strings.Setup.Terms.SectionsCount(4), Open)
            with { AlignSelf = FlexAlign.Center };

        Element[] needCards =
        [
            SetupCompact.SectionLabel(Loc.Get(Strings.Setup.Terms.NeedGroup)),
            SetupCompact.InfoCard(Icons.Contact, Loc.Get(Strings.Setup.Terms.PremiumTitle), Loc.Get(Strings.Setup.Terms.PremiumBody)),
            SetupCompact.InfoCard(Icons.Download, Loc.Get(Strings.Setup.Terms.RuntimeTitle), Loc.Get(Strings.Setup.Terms.RuntimeBody)),
            SetupCompact.InfoCard(Icons.Folder, Loc.Get(Strings.Setup.Terms.DataTitle), Loc.Get(Strings.Setup.Terms.DataBody)),
        ];
        Element fine = SetupCompact.FinePrint(Loc.Get(Strings.Setup.Terms.Fine), maxLines: 5);

        Element? stage = wide
            ? Flow.Show(() => agreementOpen.Value,
                AgreementDoc(Close) with { Key = "terms:stage:doc" },
                SetupStage.Column(
                    AgreementPreview(Open),
                    LinkRow(),
                    SetupStage.Spacer(),
                    SetupStage.Caption(Loc.Get(Strings.Setup.Terms.StageCaptionTitle), Loc.Get(Strings.Setup.Terms.StageCaptionSub)))
                    with { Key = "terms:stage:art" })
            : null;

        Element body = wide
            // scrollBody:false → the frame gives this a Grow=1 slot instead of a ScrollEl viewport, so the Spacer
            // between the cards and the fine print can actually reach the floor (a ScrollEl has nothing for it to
            // grow into — SetupPageHost.cs's own reasoning for the flag).
            ? SetupCompact.Column([.. needCards, SetupCompact.Spacer(), fine]) with { Grow = 1f, Shrink = 1f, MinHeight = 0f }
            // No stage column here — the same link + in-place document swap rides along in the (always-scrollable)
            // body instead, so the full agreement is still one click away.
            : SetupCompact.Column([.. needCards, fine, LinkRow(),
                Flow.Show(() => agreementOpen.Value, AgreementDoc(Close) with { Key = "terms:body:doc" })]);

        return SetupPageHost.Frame(SetupPage.Terms, Loc.Get(Strings.Setup.Eyebrow.Terms), Loc.Get(Strings.Setup.Terms.Title),
            body, lead: Loc.Get(Strings.Setup.Terms.Lead), leadMaxLines: 2, stage: stage, scrollBody: !wide);
    }

    /// <summary>The full four-section agreement, in its own card — filling whatever slot hosts it (the stage column
    /// at Wide, inline in the scroll body otherwise). Escape closes it exactly like the rest of the wizard's nested
    /// popups close on Escape without touching the plate's own modal <c>DismissBehavior</c>.</summary>
    // -- The stage's closed state: a document card previewing the four sections (titles + two "text" bars each) with
    // the accept badge hanging off its corner - the approved board's stage, and a click target that opens the doc. --
    static Element AgreementPreview(Action open)
    {
        var rows = new List<Element>
        {
            new TextEl(Loc.Get(Strings.Setup.Terms.AgreementTitle)) { Size = 13f, LineHeight = 18f, Weight = 600, Color = Tok.TextSecondary },
        };
        foreach (string key in new[] { Strings.Setup.Terms.Section1Title, Strings.Setup.Terms.Section2Title, Strings.Setup.Terms.Section3Title, Strings.Setup.Terms.Section4Title })
        {
            rows.Add(new BoxEl
            {
                Direction = 1, Gap = 6f, AlignSelf = FlexAlign.Stretch,
                Children =
                [
                    new TextEl(Loc.Get(key)) { Size = 12f, LineHeight = 16f, Color = Tok.TextPrimary, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                    new BoxEl { Height = 3f, AlignSelf = FlexAlign.Stretch, Corners = CornerRadius4.All(1.5f), Fill = Tok.TextTertiary with { A = 0.35f } },
                    new BoxEl { Height = 3f, Width = 170f, Corners = CornerRadius4.All(1.5f), Fill = Tok.TextTertiary with { A = 0.35f } },
                ],
            });
        }

        Element card = new BoxEl
        {
            Direction = 1, Gap = 12f, AlignSelf = FlexAlign.Stretch,
            Padding = new Edges4(20f, 18f, 20f, 28f), Margin = new Edges4(0f, 0f, 14f, 14f),
            Corners = Radii.CardAll, Fill = Tok.FillSolidBase, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Shadow = Elevation.Card,
            Children = rows.ToArray(),
        };
        Element badge = new BoxEl
        {
            Width = 44f, Height = 44f, Corners = CornerRadius4.All(22f), Shrink = 0f,
            Fill = Tok.AccentDefault, Shadow = Elevation.Card,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Children = [Icon(Icons.Accept, 20f, Tok.TextOnAccentPrimary)],
        };

        return new BoxEl
        {
            ZStack = true, AlignSelf = FlexAlign.Stretch, Shrink = 0f,
            Role = AutomationRole.Button, Focusable = true, Cursor = CursorId.Hand, OnClick = open,
            Children =
            [
                card,
                new BoxEl { Direction = 0, Justify = FlexJustify.End, AlignItems = FlexAlign.End, AlignSelf = FlexAlign.Stretch, HitTestVisible = false, Children = [badge] },
            ],
        };
    }

    static Element AgreementDoc(Action close)
    {
        Element Section(string title, string sectionBody) => new BoxEl
        {
            Direction = 1, Gap = 2f, Shrink = 0f,
            Children =
            [
                new TextEl(title) { Size = 12f, LineHeight = 16f, Weight = 600, Color = Tok.TextPrimary },
                new TextEl(sectionBody) { Size = 12f, LineHeight = 16f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap, MinWidth = 0f },
            ],
        };

        Element header = new BoxEl
        {
            Direction = 0, Height = 40f, Shrink = 0f, AlignItems = FlexAlign.Center,
            Children =
            [
                SetupStage.CardTitle(Loc.Get(Strings.Setup.Terms.AgreementTitle)) with { Grow = 1f, Basis = 0f, MinWidth = 0f },
                IconButton.Create(Icons.Cancel, close, size: ControlSize.Small),
            ],
        };

        Element sections = ScrollView(new BoxEl
        {
            Direction = 1, Gap = Spacing.M, MinWidth = 0f,
            Children =
            [
                Section(Loc.Get(Strings.Setup.Terms.Section1Title), Loc.Get(Strings.Setup.Terms.Section1Body)),
                Section(Loc.Get(Strings.Setup.Terms.Section2Title), Loc.Get(Strings.Setup.Terms.Section2Body)),
                Section(Loc.Get(Strings.Setup.Terms.Section3Title), Loc.Get(Strings.Setup.Terms.Section3Body)),
                Section(Loc.Get(Strings.Setup.Terms.Section4Title), Loc.Get(Strings.Setup.Terms.Section4Body)),
            ],
        }) with { Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f };

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Padding = new Edges4(12f, 10f, 12f, 10f),
            Fill = Tok.FillCardDefault, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Corners = Radii.CardAll,
            Focusable = true,
            OnKeyDown = e => { if (e.KeyCode == Keys.Escape) { close(); e.Handled = true; } },
            Children = [header, sections],
        };
    }
}
