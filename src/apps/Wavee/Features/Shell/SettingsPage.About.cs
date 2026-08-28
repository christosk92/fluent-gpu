using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Rhi.D3D12;
using FluentGpu.Signals;
using Wavee.Backend.Audio;
using Wavee.Core;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

sealed partial class SettingsPage
{
    const string FeedbackUrl = "https://github.com/christosk92/fluent-gpu/issues";
    const string WebsiteUrl = "https://github.com/christosk92/fluent-gpu";
    const string PrivacyUrl = "https://github.com/christosk92/fluent-gpu/blob/main/PRIVACY.md";

    /// <summary>The generated notices file, staged next to Wavee.exe by ops/build/generate-third-party-notices.ps1
    /// (called from both publish-wavee-aot.ps1 and pack-wavee-msix.ps1). A plain `dotnet run` has no such file, which
    /// is exactly what <c>Strings.Settings.About.NoticesMissing</c> says.</summary>
    const string NoticesFileName = "THIRD-PARTY-NOTICES.txt";

    static string NoticesPath => Path.Combine(AppContext.BaseDirectory, NoticesFileName);

    /// <summary>Wavee's OWN license. Everything else — every package, every vendored component — is enumerated by the
    /// generated notices file rather than by a hand-maintained list here: a list in code drifts the moment a
    /// PackageReference changes, and the drift is invisible until someone audits it.</summary>
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

    Element AboutTab(Services? svc, InputHooks hooks)
    {
        string version = AppVersion.Current;
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
            AboutLinksCard(svc, hooks, DiagInfo, os),
            SettingsSectionHeader(Loc.Get(Strings.Settings.About.Licenses), Icons.Document),
        };
        kids.AddRange(LicenseExpanders());
        return SettingsTabStack(kids.ToArray());
    }

    static Element AboutHero(string version)
    {
        var kids = new List<Element>
        {
            Icon(Icons.MusicNote, 48f, Tok.AccentDefault),
            new TextEl("Wavee") { Size = 28f, Weight = 700, Color = Tok.TextPrimary },
            new TextEl(Strings.Settings.About.Version(version)) { Size = 13f, Weight = 600, Color = Tok.TextSecondary },
        };
        // An unstamped build is captioned so a screenshot of it is never mistaken for a shipped version.
        if (AppVersion.IsDev)
            kids.Add(new TextEl(Loc.Get(Strings.Settings.About.DevBuild))
                { Size = 12f, Weight = 600, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap });
        kids.Add(new TextEl("© 2026 Christos Karapasias") { Size = 12f, Color = Tok.TextTertiary });

        return new BoxEl
        {
            Direction = 1, Gap = Spacing.S, AlignItems = FlexAlign.Center,
            Padding = new Edges4(Spacing.XL, Spacing.L, Spacing.XL, Spacing.L),
            Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children = kids.ToArray(),
        };
    }

    static Element AboutLinksCard(Services? svc, InputHooks hooks, Func<string> diagInfo, string os) => SettingsCard.Create(new SettingsCard.Options
    {
        Alignment = SettingsCard.ContentAlignment.Left,
        Content = new BoxEl
        {
            Direction = 1, Gap = 4f, Margin = new Edges4(-12f, 0f, 0f, 0f),
            Children =
            [
                HyperlinkButton.Create(Loc.Get(Strings.Settings.About.CheckForUpdates), () => CheckForUpdates(svc)),
                HyperlinkButton.Create(Loc.Get(Strings.Settings.About.SendFeedback), FeedbackUrl),
                HyperlinkButton.Create(Loc.Get(Strings.Settings.About.Website), WebsiteUrl),
                HyperlinkButton.Create(Loc.Get(Strings.Settings.About.PrivacyPolicy), PrivacyUrl),
                HyperlinkButton.Create(Loc.Get(Strings.Settings.About.ThirdPartyNotices), OpenThirdPartyNotices),
                HyperlinkButton.Create(Loc.Get(Strings.Settings.About.CopyDiagnostics), () =>
                {
                    hooks.Clipboard?.SetText(diagInfo());
                    Toast.Show(Loc.Get(Strings.Settings.About.DiagnosticsCopied), new ToastOptions { Severity = InfoBarSeverity.Success });
                }),
                HyperlinkButton.Create(Loc.Get(Strings.Settings.About.OpenDataFolder),
                    () => SettingsShared.OpenFolder(SettingsShared.AppDataRoot)),
                new TextEl(Loc.Get(Strings.Settings.About.Unofficial)) { Size = 12f, Color = Tok.TextTertiary, Wrap = TextWrap.Wrap },
                new TextEl(os) { Size = 12f, Color = Tok.TextSecondary, Wrap = TextWrap.Wrap },
                new TextEl(SettingsShared.AppDataRoot) { Size = 12f, Color = Tok.TextSecondary, FontFamily = "Cascadia Code", Wrap = TextWrap.Wrap },
            ],
        },
    });

    /// <summary>Runs one feed check and reports its OUTCOME (never its progress: the state machine lives in the
    /// service). The await happens off the UI thread, so the report hops back through the host poster —
    /// <c>HostDispatch.Current</c> is null only headlessly, where running inline is correct.</summary>
    static void CheckForUpdates(Services? svc)
    {
        if (svc?.AppUpdate is not { } upd)
        {
            Toast.Show(Loc.Get(Strings.Settings.About.UpdateCheckFailed), new ToastOptions { Severity = InfoBarSeverity.Error });
            return;
        }

        Toast.Show(Loc.Get(Strings.Settings.About.Checking), new ToastOptions { Severity = InfoBarSeverity.Informational });
        _ = Task.Run(async () =>
        {
            try { await upd.CheckAsync(CancellationToken.None).ConfigureAwait(false); }
            catch { /* the seam swallows its own failures; a throwing impl still lands on the Failed branch below */ }

            void Report()
            {
                switch (upd.Current)
                {
                    case AppUpdateState.Available:
                        Toast.Show(Strings.Settings.About.UpdateAvailable(upd.Version ?? ""), new ToastOptions
                        {
                            Severity = InfoBarSeverity.Success,
                            ActionLabel = Loc.Get(Strings.Notifications.Update.Download),
                            OnAction = () => _ = upd.DownloadAsync(CancellationToken.None),
                        });
                        break;
                    case AppUpdateState.Failed:
                        Toast.Show(Loc.Get(Strings.Settings.About.UpdateCheckFailed), new ToastOptions { Severity = InfoBarSeverity.Error });
                        break;
                    default:
                        Toast.Show(Loc.Get(Strings.Settings.About.UpToDate), new ToastOptions { Severity = InfoBarSeverity.Success });
                        break;
                }
            }

            var post = FluentGpu.Hooks.HostDispatch.Current;
            if (post is null) Report(); else post(Report);
        });
    }

    /// <summary>Open the shipped notices file with the shell. Absent in a dev run (it is generated at publish/pack
    /// time), which the toast says plainly rather than opening nothing.</summary>
    static void OpenThirdPartyNotices()
    {
        string path = NoticesPath;
        if (!File.Exists(path))
        {
            Toast.Show(Loc.Get(Strings.Settings.About.NoticesMissing), new ToastOptions { Severity = InfoBarSeverity.Informational });
            return;
        }
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch
        {
            Toast.Show(Loc.Get(Strings.Settings.About.NoticesMissing), new ToastOptions { Severity = InfoBarSeverity.Warning });
        }
    }

    // Read once per process: the file is stamped at publish time and cannot change under a running build, and the
    // About tab re-renders on every theme/tab tick — a File.ReadAllText per render would be disk I/O on the UI thread.
    static string? s_notices;

    static string ReadThirdPartyNotices()
    {
        if (s_notices is not null) return s_notices;
        string text;
        try
        {
            string path = NoticesPath;
            text = File.Exists(path) ? File.ReadAllText(path) : Loc.Get(Strings.Settings.About.NoticesMissing);
        }
        catch { text = Loc.Get(Strings.Settings.About.NoticesMissing); }   // unreadable reads the same as absent
        s_notices = text;
        return text;
    }

    static Element[] LicenseExpanders()
    {
        var style = LicenseExpanderStyle;
        var items = new List<Element>();
        foreach (var lic in s_licenses)
        {
            items.Add(SettingsExpander.Create(new SettingsExpander.Options
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
            }));
        }
        // ONE expander for everything third-party, read from the generated file rather than restated in code.
        items.Add(SettingsExpander.Create(new SettingsExpander.Options
        {
            Header = Loc.Get(Strings.Settings.About.ThirdPartyNotices),
            Description = NoticesFileName,
            InitiallyExpanded = false,
            Style = style,
            Items =
            [
                SettingsExpander.Item("", null,
                    new TextEl(ReadThirdPartyNotices()) { Size = 12f, Color = Tok.TextTertiary, FontFamily = "Cascadia Code", Wrap = TextWrap.Wrap },
                    alignment: SettingsCard.ContentAlignment.Left,
                    style: style),
            ],
        }));
        return items.ToArray();
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
