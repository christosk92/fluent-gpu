using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Signals;
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
            $"Playback runtime: {(svc?.Playback.RuntimeStatus.Value ?? PlaybackRuntimeStatus.NotApplicable).Outcome}\n" +
            WaveeNowReceipts.LastCopyText;

        var kids = new List<Element>
        {
            AboutHero(version),
            InfoBar.Create(InfoBarSeverity.Informational,
                Strings.Settings.About.Build(version),
                $"{os} · Engine: FluentGpu · {dotnet}",
                isClosable: false),
            SettingsSectionHeader("Wavee right now", Icons.Info),
            Embed.Comp(() => new WaveeNowReceipts()),
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
                    Toast.Show(Loc.Get(Strings.Settings.About.DiagnosticsCopied), new ToastOptions { Severity = InfoBarSeverity.Success });
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

    /// <summary>
    /// Settings → About "Wavee right now" receipts. A 5s <see cref="Component.UseInterval"/> composes the strings;
    /// Render never reads process/GPU/FPS itself (no per-frame <see cref="FluentGpu.Hosting.FrameDiagnostics"/> subscribe).
    /// Mounted via Embed.Comp so the interval lives on this child, not behind SettingsPage's tab switch.
    /// </summary>
    sealed class WaveeNowReceipts : Component
    {
        internal static string LastCopyText { get; private set; } = "";

        const float TickMs = 5000f;
        readonly Signal<string> _workingSet = new("—");
        readonly Signal<string> _managed = new("—");
        readonly Signal<string> _uptime = new("—");
        readonly Signal<string> _fps = new("—");
        readonly Signal<string> _gpuAssets = new("—");
        readonly Signal<string> _appExcl = new("—");
        readonly Signal<string> _detail = new("—");

        public override Element Render()
        {
            UseEffect(Tick, DepKey.Empty);
            UseInterval(Tick, TickMs);
            return SettingsCard.Create(new SettingsCard.Options
            {
                Alignment = SettingsCard.ContentAlignment.Left,
                Content = new BoxEl
                {
                    Direction = 1, Gap = Spacing.XS,
                    Children =
                    [
                        ReceiptLine(_workingSet, "Working set"),
                        ReceiptLine(_managed, "Managed heap"),
                        ReceiptLine(_uptime, "Uptime"),
                        ReceiptLine(_fps, "FPS"),
                        ReceiptLine(_gpuAssets, "GPU assets"),
                        ReceiptLine(_appExcl, "App memory excl. GPU assets"),
                        new TextEl(_detail) { Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap },
                    ],
                },
            });
        }

        void Tick()
        {
            using var proc = Process.GetCurrentProcess();
            proc.Refresh();
            long ws = proc.WorkingSet64;
            long managed = GC.GetTotalMemory(forceFullCollection: false);
            TimeSpan up = DateTime.Now - proc.StartTime;
            double fps = WaveeStartupBench.Host?.LastStats.Fps ?? 0;
            var snap = D3D12Device.LastVideoMemory;

            _workingSet.Value = FormatBytes(ws);
            _managed.Value = FormatBytes(managed);
            _uptime.Value = FormatUptime(up);
            _fps.Value = fps > 0 ? fps.ToString("0.0", CultureInfo.InvariantCulture) : "—";

            if (!snap.Valid)
            {
                _gpuAssets.Value = "— (no Present yet)";
                _appExcl.Value = "—";
                _detail.Value = "GPU video-memory snapshot publishes on the render thread after the first Present.";
            }
            else
            {
                bool sharedIgpu = ClassifySharedIgpu(in snap);
                ulong sharedSeg = sharedIgpu ? snap.LocalCurrentUsage : snap.NonLocalCurrentUsage;
                long excl = ws - (long)sharedSeg;
                if (excl < 0) excl = 0;
                string kind = sharedIgpu ? "shared / iGPU" : "discrete";
                _gpuAssets.Value = FormatBytes((long)snap.LocalCurrentUsage)
                    + " local  ·  " + FormatBytes((long)snap.NonLocalCurrentUsage) + " non-local";
                _appExcl.Value = FormatBytes(excl) + "  (" + kind + ")";
                _detail.Value =
                    "Local budget " + FormatBytes((long)snap.LocalBudget)
                    + "  ·  non-local budget " + FormatBytes((long)snap.NonLocalBudget)
                    + "  ·  tracked D3D12 " + FormatBytes(snap.TrackedResourceBytes)
                    + " (" + snap.TrackedResourceCount.ToString(CultureInfo.InvariantCulture) + ")"
                    + "  ·  atlas " + snap.AtlasImages.ToString(CultureInfo.InvariantCulture)
                    + "/" + snap.AtlasPages.ToString(CultureInfo.InvariantCulture)
                    + "  ·  glyphs " + snap.CachedGlyphs.ToString(CultureInfo.InvariantCulture)
                    + ". App excl. GPU ≈ working set − "
                    + (sharedIgpu ? "LOCAL (UMA/shared)" : "NON_LOCAL (system-memory overlap)")
                    + ".";
            }

            LastCopyText =
                "Working set: " + _workingSet.Peek()
                + "\nManaged heap: " + _managed.Peek()
                + "\nUptime: " + _uptime.Peek()
                + "\nFPS: " + _fps.Peek()
                + "\nGPU assets: " + _gpuAssets.Peek()
                + "\nApp memory excl. GPU assets: " + _appExcl.Peek()
                + "\n" + _detail.Peek();
        }

        static bool ClassifySharedIgpu(in GpuVideoMemorySnapshot snap)
        {
            if (GpuProfile.IsWeak) return true;
            if (GpuProfile.Tier == GpuPowerTier.Strong) return false;
            // Unknown: task heuristic — NON_LOCAL bulk of the DXGI usage ⇒ shared/iGPU; LOCAL dominates ⇒ discrete.
            return snap.NonLocalCurrentUsage >= snap.LocalCurrentUsage && snap.NonLocalCurrentUsage > 0;
        }

        static Element ReceiptLine(Signal<string> value, string label) => new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Children =
            [
                new TextEl(label) { Size = 12f, Color = Tok.TextSecondary, Shrink = 0f },
                new TextEl(value) { Size = 13f, Weight = 600, Color = Tok.TextPrimary, Grow = 1f, MinWidth = 0f, Wrap = TextWrap.Wrap },
            ],
        };

        static string FormatBytes(long bytes)
        {
            double mb = bytes / 1048576.0;
            return mb.ToString("0.0", CultureInfo.InvariantCulture) + " MB";
        }

        static string FormatUptime(TimeSpan t)
        {
            if (t.TotalDays >= 1) return ((int)t.TotalDays).ToString(CultureInfo.InvariantCulture) + "d " + t.Hours.ToString(CultureInfo.InvariantCulture) + "h";
            if (t.TotalHours >= 1) return ((int)t.TotalHours).ToString(CultureInfo.InvariantCulture) + "h " + t.Minutes.ToString(CultureInfo.InvariantCulture) + "m";
            if (t.TotalMinutes >= 1) return ((int)t.TotalMinutes).ToString(CultureInfo.InvariantCulture) + "m " + t.Seconds.ToString(CultureInfo.InvariantCulture) + "s";
            return Math.Max(0, (int)t.TotalSeconds).ToString(CultureInfo.InvariantCulture) + "s";
        }
    }
}
