using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Backend.Audio;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

sealed partial class SettingsPage
{
    const string FeedbackUrl = "https://github.com/christosk92/fluent-gpu/issues";
    const string WebsiteUrl = "https://github.com/christosk92/fluent-gpu";

    static readonly (string Name, string Kind, string Body)[] s_licenses =
    [
        ("Wavee", "MIT",
            "Copyright (c) 2026 Christos Karapasias\n\n" +
            "Permission is hereby granted, free of charge, to any person obtaining a copy of this software and associated " +
            "documentation files (the \"Software\"), to deal in the Software without restriction, including without limitation " +
            "the rights to use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of the Software, and " +
            "to permit persons to whom the Software is furnished to do so, subject to the following conditions:\n\n" +
            "The above copyright notice and this permission notice shall be included in all copies or substantial portions of " +
            "the Software.\n\n" +
            "THE SOFTWARE IS PROVIDED \"AS IS\", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO " +
            "THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE " +
            "AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF " +
            "CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER " +
            "DEALINGS IN THE SOFTWARE."),
        ("Google.Protobuf 3.35.0", "BSD-3-Clause",
            "Protocol Buffers runtime for C#. Copyright © Google LLC. Used for the Spotify wire protocol."),
        ("Microsoft.Data.Sqlite 10.0.8 · SQLitePCLRaw 3.0.0", "MIT",
            "SQLite data provider used for the library database. Copyright © .NET Foundation and contributors. " +
            "SQLite itself is public domain."),
        ("NVorbis (vendored)", "MIT",
            "Pure-managed Ogg Vorbis decoder. Copyright © Andrew Ward and contributors."),
        ("ZstdSharp.Port 0.8.6 · FlacBox 1.0.0 · ProtectedData 9.0", "MIT / BSD",
            "Zstandard decompression (© Oleg Stepanischev), FLAC decoding, and Windows DPAPI credential protection " +
            "(© .NET Foundation)."),
    ];

    static SettingsExpander.Style LicenseExpanderStyle => new()
    {
        ItemCardStyle = SettingsCard.DefaultStyle with
        {
            Padding = new Edges4(16f, 12f, 16f, 16f),
            MinHeight = 0f,
            CornerRadius = 0f,
            WrapThreshold = 0f,
            WrapNoIconThreshold = 0f,
        },
    };

    static string AppVersion
    {
        get
        {
            string? v = typeof(SettingsPage).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
            if (string.IsNullOrEmpty(v)) return "dev";
            int plus = v.IndexOf('+');
            return plus > 0 ? v[..plus] : v;
        }
    }

    Element AboutTab(Services? svc, InputHooks hooks)
    {
        string version = AppVersion;
        string os = RuntimeInformation.OSDescription + " (" + RuntimeInformation.OSArchitecture + ")";
        string dotnet = ".NET " + Environment.Version;

        string DiagInfo() =>
            $"Wavee {version}\nOS: {os}\nEngine: FluentGpu · {dotnet}\nData folder: {SettingsShared.AppDataRoot}\n" +
            $"Playback runtime: {(svc?.Playback.RuntimeStatus.Value ?? PlaybackRuntimeStatus.NotApplicable).Outcome}";

        var kids = new List<Element>
        {
            AboutHero(version),
            InfoBar.Create(InfoBarSeverity.Informational,
                Strings.Settings.About.Build(version),
                $"{os} · Engine: FluentGpu · {dotnet}",
                isClosable: false),
            AboutLinksCard(hooks, DiagInfo, os),
            SettingsSectionHeader(Loc.Get(Strings.Settings.About.Licenses), Icons.Document),
        };
        kids.AddRange(LicenseExpanders());
        return SettingsTabStack(kids.ToArray());
    }

    static Element AboutHero(string version) => new BoxEl
    {
        Direction = 1, Gap = Spacing.S, AlignItems = FlexAlign.Center,
        Padding = new Edges4(Spacing.XL, Spacing.L, Spacing.XL, Spacing.L),
        Corners = CornerRadius4.All(Radii.Card),
        Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
        Children =
        [
            Icon(Icons.MusicNote, 48f, Tok.AccentDefault),
            new TextEl("Wavee") { Size = 28f, Weight = 700, Color = Tok.TextPrimary },
            new TextEl(Strings.Settings.About.Version(version)) { Size = 13f, Weight = 600, Color = Tok.TextSecondary },
            new TextEl("© 2026 Christos Karapasias") { Size = 12f, Color = Tok.TextTertiary },
        ],
    };

    static Element AboutLinksCard(InputHooks hooks, Func<string> diagInfo, string os) => SettingsCard.Create(new SettingsCard.Options
    {
        Alignment = SettingsCard.ContentAlignment.Left,
        Content = new BoxEl
        {
            Direction = 1, Gap = 4f, Margin = new Edges4(-12f, 0f, 0f, 0f),
            Children =
            [
                HyperlinkButton.Create(Loc.Get(Strings.Settings.About.SendFeedback), FeedbackUrl),
                HyperlinkButton.Create(Loc.Get(Strings.Settings.About.Website), WebsiteUrl),
                HyperlinkButton.Create(Loc.Get(Strings.Settings.About.CopyDiagnostics), () =>
                {
                    hooks.Clipboard?.SetText(diagInfo());
                    Toasts.Show(Loc.Get(Strings.Settings.About.DiagnosticsCopied), ToastSeverity.Success);
                }),
                HyperlinkButton.Create(Loc.Get(Strings.Settings.About.OpenDataFolder),
                    () => SettingsShared.OpenFolder(SettingsShared.AppDataRoot)),
                new TextEl(os) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                new TextEl(SettingsShared.AppDataRoot) { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Wrap = TextWrap.Wrap },
            ],
        },
    });

    static Element[] LicenseExpanders()
    {
        var style = LicenseExpanderStyle;
        return
        [
            ..s_licenses.Select(lic => SettingsExpander.Create(new SettingsExpander.Options
            {
                Header = lic.Name,
                Description = lic.Kind,
                InitiallyExpanded = false,
                Style = style,
                Items =
                [
                    SettingsExpander.Item("", null,
                        new TextEl(lic.Body) { Size = 12f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code", Wrap = TextWrap.Wrap },
                        alignment: SettingsCard.ContentAlignment.Left,
                        style: style),
                ],
            })),
        ];
    }
}
