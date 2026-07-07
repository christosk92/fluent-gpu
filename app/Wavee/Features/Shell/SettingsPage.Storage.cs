using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using Wavee.Backend.Audio;
using Wavee.SpotifyLive.Audio;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

sealed partial class SettingsPage
{
    sealed record StorageSnapshot(long LibraryDb, long Runtime, long Logs, int LogFiles, long Store,
        long AudioBody, long LicenseDb, long Total);

    enum StorageLoadPhase : byte { NotStarted, Loading, Ready, Failed }

    static readonly string[] s_bodyBudgetLabels = ["512 MB", "1 GB", "2 GB", "4 GB", "8 GB"];
    static readonly long[] s_bodyBudgetBytes = [512L << 20, 1L << 30, 2L << 30, 4L << 30, 8L << 30];

    // Distinct hues for the usage bar + matching row accents (accent tints read as identical blue on dark themes).
    static readonly ColorF StorageColorLibrary = ColorF.FromRgba(0x4A, 0x90, 0xD9);
    static readonly ColorF StorageColorRuntime = ColorF.FromRgba(0x9B, 0x59, 0xB6);
    static readonly ColorF StorageColorLogs = ColorF.FromRgba(0xF5, 0xA6, 0x23);
    static readonly ColorF StorageColorStore = ColorF.FromRgba(0x27, 0xAE, 0x60);
    static readonly ColorF StorageColorAudio = ColorF.FromRgba(0x1A, 0xBC, 0x9C);
    static readonly ColorF StorageColorKeys = ColorF.FromRgba(0x95, 0xA5, 0xA6);

    readonly Signal<StorageLoadPhase> _storageLoad = new(StorageLoadPhase.NotStarted);
    StorageSnapshot? _storage;
    string? _storageError;

    void RefreshStorage(Action<Action> post)
    {
        if (_storageLoad.Peek() == StorageLoadPhase.Loading) return;
        _storageLoad.Value = StorageLoadPhase.Loading;
        _storageError = null;
        _ = Task.Run(() =>
        {
            StorageSnapshot? snap = null;
            Exception? error = null;
            try { snap = ComputeStorage(); }
            catch (Exception ex) { error = ex; }
            post(() =>
            {
                if (snap is { } ready)
                {
                    _storage = ready;
                    _storageError = null;
                    _storageLoad.Value = StorageLoadPhase.Ready;
                }
                else
                {
                    _storage = null;
                    _storageError = error?.Message;
                    _storageLoad.Value = StorageLoadPhase.Failed;
                }
            });
        });
    }

    static StorageSnapshot ComputeStorage()
    {
        string root = SettingsShared.AppDataRoot;
        long library = 0, runtime = 0, logs = 0, store = 0;
        int logFiles = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(root, "library.db*")) library += FileSize(f);
        }
        catch { }
        runtime = DirSize(Path.Combine(root, "playplay"));
        try
        {
            string logDir = Path.Combine(root, "logs");
            if (Directory.Exists(logDir))
                foreach (var f in Directory.EnumerateFiles(logDir)) { logs += FileSize(f); logFiles++; }
        }
        catch { }
        store += FileSize(Path.Combine(root, "store.json"));
        store += DirSize(Path.Combine(root, "WaveeMusic"));
        long audioBody = DirSize(AudioBodyDiskCache.DefaultDirectory());
        long licenseDb = FileSize(LicenseKeyDiskCache.DefaultDbPath());
        return new StorageSnapshot(library, runtime, logs, logFiles, store, audioBody, licenseDb,
            library + runtime + logs + store + audioBody + licenseDb);
    }

    static long FileSize(string path)
    {
        try { var fi = new FileInfo(path); return fi.Exists ? fi.Length : 0; }
        catch { return 0; }
    }

    static long DirSize(string dir)
    {
        long total = 0;
        try
        {
            if (!Directory.Exists(dir)) return 0;
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)) total += FileSize(f);
        }
        catch { }
        return total;
    }

    static string FmtBytes(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => (bytes / (1024.0 * 1024 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " GB",
        >= 1024L * 1024 => (bytes / (1024.0 * 1024)).ToString("0.0", CultureInfo.InvariantCulture) + " MB",
        >= 1024L => (bytes / 1024.0).ToString("0", CultureInfo.InvariantCulture) + " KB",
        _ => bytes.ToString(CultureInfo.InvariantCulture) + " B",
    };

    static Element StorageActions(long? size, string folder, Element? extra = null)
    {
        var actions = new List<Element>();
        if (size is { } b) actions.Add(new TextEl(FmtBytes(b)) { Size = 13f, Color = Tok.TextSecondary, Shrink = 0f });
        actions.Add(HyperlinkButton.Create(Loc.Get(Strings.Settings.Storage.OpenFolder), () => SettingsShared.OpenFolder(folder)));
        if (extra is not null) actions.Add(extra);
        return new BoxEl { Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M, Children = actions.ToArray() };
    }

    static Element StorageRowCard(string label, string? sub, long? size, string folder, ColorF accent,
                                  string? icon = null, Element? extra = null)
    {
        var row = SettingsRow(label, sub, StorageActions(size, folder, extra), icon);
        return new BoxEl
        {
            Direction = 0, AlignSelf = FlexAlign.Stretch,
            Children =
            [
                new BoxEl
                {
                    Width = 3f, AlignSelf = FlexAlign.Stretch, Fill = accent,
                    Corners = new CornerRadius4(2f, 0f, 0f, 2f),
                },
                new BoxEl { Grow = 1f, Basis = 0f, MinWidth = 0f, Children = [row] },
            ],
        };
    }

    static int BodyBudgetIndex(long bytes)
    {
        int best = 3;
        long bestDiff = long.MaxValue;
        for (int i = 0; i < s_bodyBudgetBytes.Length; i++)
        {
            long diff = Math.Abs(s_bodyBudgetBytes[i] - bytes);
            if (diff < bestDiff) { bestDiff = diff; best = i; }
        }
        return best;
    }

    void DeleteOldLogs(Action<Action> post)
    {
        _ = Task.Run(() =>
        {
            int deleted = 0;
            try
            {
                string logDir = Path.Combine(SettingsShared.AppDataRoot, "logs");
                if (Directory.Exists(logDir))
                    foreach (var f in Directory.EnumerateFiles(logDir, "wavee-*.log"))
                    { try { File.Delete(f); deleted++; } catch { } }
            }
            catch { }
            post(() =>
            {
                Toasts.Show(deleted > 0
                    ? Loc.Format("settings.storage.oldLogsDeleted", ("count", deleted))
                    : Loc.Get(Strings.Settings.Storage.NoOldLogsDeleted),
                    ToastSeverity.Success);
                RefreshStorage(post);
            });
        });
    }

    static string ResidentCacheDescription(Wavee.Backend.Persistence.CachedStore? cold)
    {
        if (cold is null) return Loc.Get(Strings.Settings.Storage.ResidentCacheSub);
        return Loc.Format("settings.storage.residentCacheStats",
            ("used", FmtBytes(cold.ResidentMembershipBytes)),
            ("cap", FmtBytes(cold.MaxResidentBytes)),
            ("count", cold.ResidentMembershipCount),
            ("max", cold.MaxResidentPlaylists));
    }

    Element StorageTab(Services? svc, Action<Action> post)
    {
        var storageLoad = _storageLoad.Value;
        var s = _storage;
        string root = SettingsShared.AppDataRoot;
        string playplayDir = Path.Combine(root, "playplay");
        var settings = svc?.Settings;

        var cold = svc?.RealStore as Wavee.Backend.Persistence.CachedStore;
        string residentCacheDescription = ResidentCacheDescription(cold);
        string audioDir = AudioBodyDiskCache.DefaultDirectory();
        string licenseDb = LicenseKeyDiskCache.DefaultDbPath();
        var keyStats = svc?.AudioLicenseCache?.Stats();
        string licenseSub = keyStats is { } ks
            ? (ks.Count == 1
                ? Loc.Get(Strings.Settings.Storage.LicenseKeysCountOne)
                : Loc.Format("settings.storage.licenseKeysCount", ("count", ks.Count)))
            : Loc.Get(Strings.Settings.Storage.LicenseKeysSub);

        Element Toggle(SettingKey<bool> key) => ToggleSwitch.Create(settings?.Get(key) ?? true, () =>
        {
            if (settings is null) return;
            settings.Set(key, !settings.Get(key));
            Bump();
        }, style: SettingsCard.CompactToggleStyle());

        Element top = storageLoad == StorageLoadPhase.Failed
            ? InfoBar.Create(InfoBarSeverity.Error,
                Loc.Get(Strings.Settings.Storage.ReadFailed),
                _storageError ?? Loc.Get(Strings.Common.ErrorTitle),
                isClosable: false,
                actionButton: Button.Standard(Loc.Get(Strings.Common.Retry), () => RefreshStorage(post)))
            : storageLoad == StorageLoadPhase.Loading || s is null
                ? new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
                    Padding = new Edges4(0, WaveeSpace.L, 0, WaveeSpace.L),
                    Children =
                    [
                        ProgressRing.Indeterminate(size: 20f),
                        new TextEl(Loc.Get(Strings.Settings.Storage.Reading)) { Size = 12f, Color = Tok.TextSecondary },
                    ],
                }
                : StorageUsageBar(s);

        return SettingsTabStack(
            SettingsSectionHeader(Loc.Get(Strings.Settings.Storage.OnThisPc), Icons.Folder),
            top,
            StorageRowCard(Loc.Get(Strings.Settings.Storage.Library), Loc.Get(Strings.Settings.Storage.LibrarySub),
                s?.LibraryDb, root, StorageColorLibrary, Icons.Document),
            StorageRowCard(Loc.Get(Strings.Settings.Storage.Runtime), Loc.Get(Strings.Settings.Storage.RuntimeSub),
                s?.Runtime, playplayDir, StorageColorRuntime, Icons.Folder),
            StorageRowCard(Loc.Get(Strings.Settings.Storage.Logs),
                s is { } sn
                    ? sn.LogFiles == 1
                        ? Loc.Get(Strings.Settings.Storage.LogsSubOne)
                        : Loc.Format("settings.storage.logsSub", ("count", sn.LogFiles))
                    : Loc.Get(Strings.Settings.Storage.LogsSubEmpty),
                s?.Logs, Path.Combine(root, "logs"), StorageColorLogs, Icons.Document,
                Button.Standard(Loc.Get(Strings.Settings.Storage.DeleteOldLogs), () =>
                    ConfirmThen(Loc.Get(Strings.Settings.Storage.DeleteOldLogs),
                        Loc.Get(Strings.Settings.Storage.DeleteOldLogsBody),
                        Loc.Get(Strings.Settings.Storage.DeleteOldLogs),
                        () => DeleteOldLogs(post)))),
            StorageRowCard(Loc.Get(Strings.Settings.Storage.LocalStore), Loc.Get(Strings.Settings.Storage.LocalStoreSub),
                s?.Store, root, StorageColorStore, Icons.Document),
            SettingsSectionHeader(Loc.Get(Strings.Settings.Storage.PlaybackCache), Icons.Download),
            SettingsRow(Loc.Get(Strings.Settings.Storage.CacheAudio), Loc.Get(Strings.Settings.Storage.CacheAudioSub),
                Toggle(WaveeSettings.AudioBodyCacheEnabled), Icons.Download),
            SettingsRow(Loc.Get(Strings.Settings.Storage.CacheKeys), Loc.Get(Strings.Settings.Storage.CacheKeysSub),
                Toggle(WaveeSettings.AudioKeyCacheEnabled), Icons.Document),
            SettingsRow(Loc.Get(Strings.Settings.Storage.BodyBudget), Loc.Get(Strings.Settings.Storage.BodyBudgetSub),
                BudgetControl(svc, settings, s), Icons.Download),
            StorageRowCard(Loc.Get(Strings.Settings.Storage.AudioBodies), Loc.Get(Strings.Settings.Storage.AudioBodiesSub),
                s?.AudioBody, audioDir, StorageColorAudio, Icons.Download,
                Button.Standard(Loc.Get(Strings.Settings.Storage.ClearAudio), () =>
                    ConfirmThen(Loc.Get(Strings.Settings.Storage.ClearAudio),
                        Loc.Get(Strings.Settings.Storage.ClearAudioBody),
                        Loc.Get(Strings.Settings.Storage.ClearAudio),
                        () => ClearAudioBodyCache(svc, post)))),
            StorageRowCard(Loc.Get(Strings.Settings.Storage.LicenseKeys), licenseSub,
                s?.LicenseDb, Path.GetDirectoryName(licenseDb) ?? audioDir, StorageColorKeys, Icons.Document,
                Button.Standard(Loc.Get(Strings.Settings.Storage.ClearKeys), () =>
                    ConfirmThen(Loc.Get(Strings.Settings.Storage.ClearKeys),
                        Loc.Get(Strings.Settings.Storage.ClearKeysBody),
                        Loc.Get(Strings.Settings.Storage.ClearKeys),
                        () => ClearLicenseKeys(svc, post)))),
            SettingsSectionHeader(Loc.Get(Strings.Settings.Storage.Memory), Icons.List),
            SettingsRow(Loc.Get(Strings.Settings.Storage.ResidentCache), residentCacheDescription,
                Button.Standard(Loc.Get(Strings.Settings.Storage.ReleaseNow), () =>
                {
                    svc?.LibraryStore.ShedDetails(keep: 16);
                    Toasts.Show(Loc.Get(Strings.Settings.Storage.DetailsReleased), ToastSeverity.Success);
                    Bump();
                }), Icons.List));
    }

    Element StorageUsageBar(StorageSnapshot s)
    {
        (string label, long bytes, ColorF color)[] segs =
        [
            (Loc.Get(Strings.Settings.Storage.Library), s.LibraryDb, StorageColorLibrary),
            (Loc.Get(Strings.Settings.Storage.Runtime), s.Runtime, StorageColorRuntime),
            (Loc.Get(Strings.Settings.Storage.Logs), s.Logs, StorageColorLogs),
            (Loc.Get(Strings.Settings.Storage.LocalStore), s.Store, StorageColorStore),
            (Loc.Get(Strings.Settings.Storage.AudioBodies), s.AudioBody, StorageColorAudio),
            (Loc.Get(Strings.Settings.Storage.LicenseKeys), s.LicenseDb, StorageColorKeys),
        ];

        var barKids = new List<Element>();
        var legend = new List<Element>();
        long total = Math.Max(1, s.Total);
        foreach (var (label, bytes, color) in segs)
        {
            if (bytes <= 0) continue;
            barKids.Add(new BoxEl { Grow = (float)bytes, Height = 10f, Fill = color, Corners = CornerRadius4.All(2f) });
            int pct = (int)Math.Round(bytes * 100.0 / total);
            legend.Add(new BoxEl
            {
                Direction = 0, AlignItems = FlexAlign.Center, Gap = 8f, Grow = 1f, Basis = 0f, MinWidth = 200f,
                Children =
                [
                    new BoxEl { Width = 10f, Height = 10f, Corners = CornerRadius4.All(2f), Fill = color, Shrink = 0f },
                    new TextEl(label) { Size = 12f, Color = Tok.TextPrimary, Grow = 1f },
                    new TextEl(FmtBytes(bytes)) { Size = 12f, Color = Tok.TextSecondary, Shrink = 0f },
                    new TextEl(pct + "%") { Size = 11f, Color = Tok.TextTertiary, Width = 36f, Shrink = 0f },
                ],
            });
        }

        return new BoxEl
        {
            Direction = 1, Gap = WaveeSpace.M,
            Padding = new Edges4(WaveeSpace.L, WaveeSpace.M, WaveeSpace.L, WaveeSpace.M),
            Corners = CornerRadius4.All(WaveeRadius.Card),
            Fill = Tok.FillCardSecondary, BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.S,
                    Children =
                    [
                        new TextEl(Loc.Get(Strings.Settings.Storage.Total)) { Size = 12f, Weight = 600, Color = Tok.TextSecondary },
                        new TextEl(FmtBytes(s.Total)) { Size = 18f, Weight = 700, Color = Tok.TextPrimary },
                    ],
                },
                barKids.Count > 0
                    ? new BoxEl
                    {
                        Direction = 0, Height = 10f, Gap = 2f, ClipToBounds = true,
                        Corners = CornerRadius4.All(3f), Fill = Tok.StrokeDividerDefault,
                        Children = barKids.ToArray(),
                    }
                    : new BoxEl(),
                new BoxEl { Direction = 0, Gap = WaveeSpace.S, Wrap = true, Children = legend.ToArray() },
            ],
        };
    }

    Element BudgetControl(Services? svc, IAppSettings? settings, StorageSnapshot? s)
    {
        int idx = BodyBudgetIndex(settings?.Get(WaveeSettings.AudioBodyCacheBudgetBytes) ?? (4L << 30));
        long budget = s_bodyBudgetBytes[idx];
        long used = s?.AudioBody ?? 0;
        float frac = budget > 0 ? Math.Clamp((float)used / budget, 0f, 1f) : 0f;
        bool over = used > budget;

        return new BoxEl
        {
            Direction = 1, Gap = 6f,
            Children =
            [
                new BoxEl
                {
                    Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M,
                    Children =
                    [
                        Slider.Ranged(idx, v => SetBodyBudgetIndex(svc, settings, (int)MathF.Round(v)),
                            new Slider.Options
                            {
                                Min = 0f, Max = s_bodyBudgetLabels.Length - 1, Step = 1f, TickFrequency = 1f,
                                IsThumbToolTipEnabled = true,
                                ThumbToolTipValueConverter = v => s_bodyBudgetLabels[Math.Clamp((int)MathF.Round(v), 0, s_bodyBudgetLabels.Length - 1)],
                            },
                            length: 230f, isEnabled: settings is not null),
                        new TextEl(s_bodyBudgetLabels[idx]) { Size = 12f, Color = Tok.TextSecondary, Width = 64f, Shrink = 0f },
                    ],
                },
                ProgressBar.Determinate(frac, width: 300f, state: over ? ProgressBarState.Error : ProgressBarState.Normal),
                new TextEl(Strings.Settings.Storage.UsedOfBudget(FmtBytes(used), s_bodyBudgetLabels[idx]))
                    { Size = 11.5f, Color = Tok.TextSecondary },
            ],
        };
    }

    void SetBodyBudgetIndex(Services? svc, IAppSettings? settings, int index)
    {
        if (settings is null) return;
        int idx = Math.Clamp(index, 0, s_bodyBudgetBytes.Length - 1);
        long bytes = s_bodyBudgetBytes[idx];
        settings.Set(WaveeSettings.AudioBodyCacheBudgetBytes, bytes);
        svc?.AudioBodyCache?.SetBudget(bytes);
        Bump();
    }

    void ClearAudioBodyCache(Services? svc, Action<Action> post)
    {
        _ = Task.Run(() =>
        {
            try { svc?.AudioBodyCache?.ClearAll(); }
            catch
            {
                try
                {
                    if (Directory.Exists(AudioBodyDiskCache.DefaultDirectory()))
                        foreach (var f in Directory.EnumerateFiles(AudioBodyDiskCache.DefaultDirectory())) File.Delete(f);
                }
                catch { }
            }
            post(() =>
            {
                Toasts.Show(Loc.Get(Strings.Settings.Storage.AudioCacheCleared), ToastSeverity.Success);
                _storage = null;
                RefreshStorage(post);
            });
        });
    }

    void ClearLicenseKeys(Services? svc, Action<Action> post)
    {
        _ = Task.Run(() =>
        {
            try { svc?.AudioLicenseCache?.ClearAll(); }
            catch { }
            post(() =>
            {
                Toasts.Show(Loc.Get(Strings.Settings.Storage.LicenseKeysCleared), ToastSeverity.Success);
                _storage = null;
                RefreshStorage(post);
            });
        });
    }
}
