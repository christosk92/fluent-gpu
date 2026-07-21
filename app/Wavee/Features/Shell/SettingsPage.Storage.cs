using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading.Tasks;
using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Localization;
using FluentGpu.Signals;
using FluentGpu.WindowsApi.Dialogs;
using Wavee.Backend.Audio;
using Wavee.SpotifyLive.Audio;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

sealed partial class SettingsPage
{
    sealed record StorageSnapshot(long LibraryDb, long Runtime, long Logs, int LogFiles, long Store,
        long AudioBody, long LicenseDb, long Total);

    enum StorageLoadPhase : byte { NotStarted, Loading, Ready, Failed }

    static readonly string[] s_bodyBudgetLabels = ["1 GB", "2 GB", "4 GB", "8 GB", "16 GB", "32 GB", "64 GB", "128 GB", "256 GB", "512 GB", "1 TB", "Custom"];
    static readonly long[] s_bodyBudgetBytes = [1L << 30, 2L << 30, 4L << 30, 8L << 30, 16L << 30, 32L << 30, 64L << 30, 128L << 30, 256L << 30, 512L << 30, 1L << 40];
    readonly Signal<int> _bodyBudgetMode = new((int)AudioCacheBudgetMode.DriveShare);
    readonly Signal<int> _bodyBudgetPreset = new(5);
    readonly Signal<double> _bodyBudgetGiB = new(32);
    readonly Signal<double> _bodyBudgetPercent = new(0);
    bool _bodyBudgetSeeded;

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
        var cacheSettings = AppDataSettings.ForUnpackaged("Wavee", "Wavee");
        long audioBody = DirSize(AudioBodyDiskCache.ResolveDirectory(cacheSettings.Get(WaveeSettings.AudioBodyCacheBasePath)));
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
        int best = 5;
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
        string membership = Loc.Format("settings.storage.residentCacheStats",
            ("used", FmtBytes(cold.ResidentMembershipBytes)),
            ("cap", FmtBytes(cold.MaxResidentBytes)),
            ("count", cold.ResidentMembershipCount),
            ("max", cold.MaxResidentPlaylists));
        // Entity-store census (attribution for the ~92 MB resident-string floor — the residency plan). Compact + diagnostic,
        // appended on one line; deliberately developer-facing (raw counts), so not routed through the loc tables.
        var c = cold.EntityCounts;
        string entities = $" · entities t={c.Tracks} al={c.Albums} ar={c.Artists} pl={c.Playlists} sh={c.Shows} ep={c.Episodes} (~{FmtBytes(cold.EstimatedEntityBytes)})";
        return membership + entities;
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
        string audioDir = svc?.AudioBodyCache?.CurrentDirectory
            ?? AudioBodyDiskCache.ResolveDirectory(settings?.Get(WaveeSettings.AudioBodyCacheBasePath));
        string licenseDb = LicenseKeyDiskCache.DefaultDbPath();
        var keyStats = svc?.AudioLicenseCache?.Stats();
        if (!_bodyBudgetSeeded && settings is not null)
        {
            _bodyBudgetSeeded = true;
            _bodyBudgetMode.Value = Math.Clamp(settings.Get(WaveeSettings.AudioBodyCacheBudgetMode), 0, 2);
            long fixedBytes = Math.Max(64L << 20, settings.Get(WaveeSettings.AudioBodyCacheBudgetBytes));
            _bodyBudgetGiB.Value = fixedBytes / (double)(1L << 30);
            int preset = Array.IndexOf(s_bodyBudgetBytes, fixedBytes);
            _bodyBudgetPreset.Value = preset >= 0 ? preset : s_bodyBudgetLabels.Length - 1;
            _bodyBudgetPercent.Value = Math.Clamp(settings.Get(WaveeSettings.AudioBodyCacheBudgetPercent), 0, 90);
        }
        string licenseSub = keyStats is { } ks
            ? (ks.Count == 1
                ? Loc.Get(Strings.Settings.Storage.LicenseKeysCountOne)
                : Loc.Format("settings.storage.licenseKeysCount", ("count", ks.Count)))
            : Loc.Get(Strings.Settings.Storage.LicenseKeysSub);

        Element Toggle(SettingKey<bool> key) => ToggleSwitch.Create(new Signal<bool>(settings?.Get(key) ?? true), onChange: _ =>
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
            SettingsRow(Loc.Get(Strings.Settings.Storage.CacheLocation), audioDir,
                CacheLocationActions(svc, settings, post, audioDir), Icons.Folder),
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
        var status = svc?.AudioBodyCache?.Status();
        int mode = Math.Clamp(_bodyBudgetMode.Value, 0, 2);
        long? budget = status?.BudgetBytes;
        long used = s?.AudioBody ?? 0;
        float frac = budget is > 0 ? Math.Clamp((float)(used / (double)budget.Value), 0f, 1f) : 0f;
        bool over = budget is { } b && used > b;

        void SetMode(int next)
        {
            if (settings is null) return;
            next = Math.Clamp(next, 0, 2);
            settings.Set(WaveeSettings.AudioBodyCacheBudgetMode, next);
            svc?.AudioBodyCache?.Trim();
            Bump();
        }

        Element editor = mode switch
        {
            (int)AudioCacheBudgetMode.FixedBytes => FixedBudgetEditor(svc, settings),
            (int)AudioCacheBudgetMode.DriveShare => NumberBox.Create(value: _bodyBudgetPercent,
                minimum: 0, maximum: 90, smallChange: 1, spinButtonPlacementMode: NumberBoxSpinButtonPlacementMode.Compact,
                width: 150f, formatter: v => v <= 0 ? Loc.Get(Strings.Settings.Storage.AutoTenPercent) : v.ToString("0", CultureInfo.InvariantCulture) + "%",
                onChange: v =>
                {
                    if (settings is null) return;
                    int pct = Math.Clamp((int)Math.Round(v), 0, 90);
                    _bodyBudgetPercent.Value = pct;
                    settings.Set(WaveeSettings.AudioBodyCacheBudgetPercent, pct);
                    svc?.AudioBodyCache?.Trim();
                    Bump();
                }, isEnabled: settings is not null),
            _ => new TextEl(Loc.Get(Strings.Settings.Storage.UnlimitedReserve)) { Size = 12f, Color = Tok.TextSecondary },
        };

        string budgetLabel = budget is { } limit ? FmtBytes(limit) : Loc.Get(Strings.Settings.Storage.Unlimited);

        return new BoxEl
        {
            Direction = 1, Gap = 6f,
            Children =
            [
                SelectorBar.Create([
                    Loc.Get(Strings.Settings.Storage.FixedSize),
                    Loc.Get(Strings.Settings.Storage.DriveShare),
                    Loc.Get(Strings.Settings.Storage.Unlimited)], _bodyBudgetMode, onChange: SetMode),
                editor,
                ProgressBar.Determinate(frac, width: 300f, state: over ? ProgressBarState.Error : ProgressBarState.Normal),
                new TextEl(Strings.Settings.Storage.UsedOfBudget(FmtBytes(used), budgetLabel))
                    { Size = 11.5f, Color = Tok.TextSecondary },
                status is { Available: false }
                    ? new TextEl(Loc.Get(Strings.Settings.Storage.LocationUnavailable)) { Size = 11.5f, Color = Tok.SystemFillCritical }
                    : new TextEl(Loc.Format("settings.storage.freeReserve", ("reserve", FmtBytes(status?.ReserveBytes ?? 0))))
                        { Size = 11.5f, Color = Tok.TextTertiary },
            ],
        };
    }

    Element FixedBudgetEditor(Services? svc, IAppSettings? settings)
    {
        void CommitGiB(double gib)
        {
            if (settings is null || double.IsNaN(gib) || double.IsInfinity(gib)) return;
            gib = Math.Clamp(gib, 0.0625, long.MaxValue / (double)(1L << 30));
            long bytes = checked((long)Math.Round(gib * (1L << 30)));
            _bodyBudgetGiB.Value = gib;
            settings.Set(WaveeSettings.AudioBodyCacheBudgetBytes, bytes);
            svc?.AudioBodyCache?.SetBudget(bytes);
            Bump();
        }

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.M, Wrap = true,
            Children =
            [
                ComboBox.Create(s_bodyBudgetLabels, _bodyBudgetPreset, width: 120f, isEnabled: settings is not null,
                    onChange: i =>
                    {
                        if (i < 0 || i >= s_bodyBudgetBytes.Length) return;
                        _bodyBudgetGiB.Value = s_bodyBudgetBytes[i] / (double)(1L << 30);
                        CommitGiB(_bodyBudgetGiB.Value);
                    }),
                NumberBox.Create(value: _bodyBudgetGiB, minimum: 0.0625,
                    maximum: long.MaxValue / (double)(1L << 30), smallChange: 1,
                    spinButtonPlacementMode: NumberBoxSpinButtonPlacementMode.Compact, width: 150f,
                    formatter: v => v.ToString("0.###", CultureInfo.InvariantCulture) + " GB",
                    onChange: v =>
                    {
                        _bodyBudgetPreset.Value = s_bodyBudgetLabels.Length - 1;
                        CommitGiB(v);
                    }, isEnabled: settings is not null),
            ],
        };
    }

    Element CacheLocationActions(Services? svc, IAppSettings? settings, Action<Action> post, string audioDir) =>
        new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.S, Wrap = true,
            Children =
            [
                Button.Standard(Loc.Get(Strings.Settings.Storage.ChooseLocation), () => PickCacheLocation(svc, settings, post)),
                Button.Standard(Loc.Get(Strings.Settings.Storage.UseDefaultLocation), () => OfferCacheRelocation(svc, settings, post, "")),
                HyperlinkButton.Create(Loc.Get(Strings.Settings.Storage.OpenFolder), () => SettingsShared.OpenFolder(audioDir)),
            ],
        };

    void PickCacheLocation(Services? svc, IAppSettings? settings, Action<Action> post)
    {
        if (settings is null || svc?.AudioBodyCache is null) return;
        string? selected = FilePicker.PickFolder(FluentApp.WindowHandle, Loc.Get(Strings.Settings.Storage.ChooseCacheFolder));
        if (string.IsNullOrWhiteSpace(selected)) return;
        OfferCacheRelocation(svc, settings, post, selected);
    }

    void OfferCacheRelocation(Services? svc, IAppSettings? settings, Action<Action> post, string newBase)
    {
        if (_overlay is null || settings is null || svc?.AudioBodyCache is null) return;
        ContentDialog.Show(_overlay, d =>
        {
            d.Title = Loc.Get(Strings.Settings.Storage.MoveCacheTitle);
            d.Message = Loc.Get(Strings.Settings.Storage.MoveCacheBody);
            d.PrimaryText = Loc.Get(Strings.Settings.Storage.MoveExisting);
            d.SecondaryText = Loc.Get(Strings.Settings.Storage.StartEmpty);
            d.CloseText = Loc.Get(Strings.Auth.Cancel);
            d.DefaultButton = ContentDialog.DefaultBtn.Primary;
            d.PrimaryClick = () => BeginCacheRelocation(svc, settings, post, newBase, AudioCacheRelocationMode.Move);
            d.SecondaryClick = () => post(() => OfferStartEmptyChoice(svc, settings, post, newBase));
        });
    }

    void OfferStartEmptyChoice(Services svc, IAppSettings settings, Action<Action> post, string newBase)
    {
        if (_overlay is null) return;
        ContentDialog.Show(_overlay, d =>
        {
            d.Title = Loc.Get(Strings.Settings.Storage.OldCacheTitle);
            d.Message = Loc.Get(Strings.Settings.Storage.OldCacheBody);
            d.PrimaryText = Loc.Get(Strings.Settings.Storage.DeleteOldCache);
            d.SecondaryText = Loc.Get(Strings.Settings.Storage.LeaveOldCache);
            d.CloseText = Loc.Get(Strings.Auth.Cancel);
            d.PrimaryClick = () => BeginCacheRelocation(svc, settings, post, newBase, AudioCacheRelocationMode.StartEmptyDeleteOld);
            d.SecondaryClick = () => BeginCacheRelocation(svc, settings, post, newBase, AudioCacheRelocationMode.StartEmptyKeepOld);
        });
    }

    void BeginCacheRelocation(Services svc, IAppSettings settings, Action<Action> post, string newBase, AudioCacheRelocationMode mode)
    {
        _ = Task.Run(async () =>
        {
            bool ok = await svc.AudioBodyCache!.PrepareRelocationAsync(newBase, mode).ConfigureAwait(false);
            post(() =>
            {
                if (ok)
                {
                    settings.Set(WaveeSettings.AudioBodyCacheBasePath, newBase);
                    Toasts.Show(Loc.Get(Strings.Settings.Storage.CacheLocationChanged), ToastSeverity.Success);
                    _storage = null;
                    RefreshStorage(post);
                    Bump();
                }
                else Toasts.Show(Loc.Get(Strings.Settings.Storage.CacheLocationFailed), ToastSeverity.Critical);
            });
        });
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
                    string dir = svc?.AudioBodyCache?.CurrentDirectory ?? AudioBodyDiskCache.DefaultDirectory();
                    if (Directory.Exists(dir))
                        foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                            if (f.EndsWith(".enc", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".map", StringComparison.OrdinalIgnoreCase)) File.Delete(f);
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
