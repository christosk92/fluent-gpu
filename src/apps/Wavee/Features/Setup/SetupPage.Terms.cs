using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>Page 1 · Terms &amp; privacy (<c>data-step="1"</c>). A lead paragraph, three description-only
/// (no trailing control) <see cref="SettingsCard"/>s for what Wavee needs, a trademark fine-print block, and a
/// <see cref="SettingsExpander"/> holding the four agreement sections.</summary>
sealed class SetupTermsPage : Component
{
    public override Element Render()
    {
        Element body = SetupRows.Stack(
            SetupRows.Lead(Loc.Get(Strings.Setup.Terms.Lead)),
            SetupRows.SectionHeader(Loc.Get(Strings.Setup.Terms.NeedGroup)),
            NeedCard(Loc.Get(Strings.Setup.Terms.PremiumTitle), Loc.Get(Strings.Setup.Terms.PremiumBody)),
            NeedCard(Loc.Get(Strings.Setup.Terms.RuntimeTitle), Loc.Get(Strings.Setup.Terms.RuntimeBody)),
            NeedCard(Loc.Get(Strings.Setup.Terms.DataTitle), Loc.Get(Strings.Setup.Terms.DataBody)),
            SetupRows.SectionHeader(Loc.Get(Strings.Setup.Terms.TrademarksGroup)),
            FinePrint(Loc.Get(Strings.Setup.Terms.Fine)),
            Agreement());

        return SetupPageHost.Frame(SetupPage.Terms, Loc.Get(Strings.Setup.Eyebrow.Terms),
            Loc.Get(Strings.Setup.Terms.Title), body);
    }

    static Element NeedCard(string title, string body) => SettingsCard.Create(new SettingsCard.Options
    {
        Header = title,
        Description = body,
        Alignment = SettingsCard.ContentAlignment.Vertical,
    });

    static Element FinePrint(string text) => new TextEl(text)
    {
        Size = 11.5f, LineHeight = 17f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap, MaxWidth = 620f,
    };

    static Element Agreement()
    {
        Element Section(string title, string body) => SetupRows.Item(title, body);
        var expanderStyle = new SettingsExpander.Style();
        expanderStyle = expanderStyle with
        {
            HeaderCardStyle = expanderStyle.HeaderCardStyle with
            {
                MinHeight = SetupLayout.AgreementHeaderHeight,
                Padding = new Edges4(Spacing.M, Spacing.M, Spacing.XS, Spacing.M),
                HeaderFontSize = 13f,
            },
        };

        return SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Setup.Terms.ReadFull),
            Content = new TextEl(Strings.Setup.Terms.SectionsCount(4))
                { Size = 12f, Color = Tok.TextTertiary, MaxLines = 1 },
            Style = expanderStyle,
            Items =
            [
                Section(Loc.Get(Strings.Setup.Terms.Section1Title), Loc.Get(Strings.Setup.Terms.Section1Body)),
                Section(Loc.Get(Strings.Setup.Terms.Section2Title), Loc.Get(Strings.Setup.Terms.Section2Body)),
                Section(Loc.Get(Strings.Setup.Terms.Section3Title), Loc.Get(Strings.Setup.Terms.Section3Body)),
                Section(Loc.Get(Strings.Setup.Terms.Section4Title), Loc.Get(Strings.Setup.Terms.Section4Body)),
            ],
        }) with { Key = "setup:terms:agreement" };
    }
}
