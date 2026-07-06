using System;
using System.Collections.Generic;
using Google.Protobuf;
using Wavee.Backend;
using Wavee.Core;
using Wavee.Protocol.Media;
using Wavee.Protocol.Player;
// The UI defines an internal `enum PlayerState` in the parent Wavee namespace (PlayerBar.cs), which shadows the proto type
// here — alias the proto PlayerState explicitly so this stays unambiguous.
using ProtoPlayerState = Wavee.Protocol.Player.PlayerState;

namespace Wavee.SpotifyLive;

// Builds the connect-state PutStateRequest (DeviceInfo + Capabilities + PrivateDeviceInfo + PlayerState) → protobuf bytes.
// The DeviceInfo/Capabilities/supported-types are desktop-parity / anti-fraud values ported VERBATIM from the reference
// (decision #11): "spotify"/"PC laptop", license=premium (Recently-Played eligibility), NeedsFullPlayerState=true, 64 volume
// steps, the 14 supported_types, AudioQuality.VeryHigh. Changing them silently breaks Recently Played / can get us throttled.
// Proto-building lives in SpotifyLive (the wire boundary); the proto-free ConnectService orchestrates the PUT.
public sealed class ConnectStateBuilder
{
    const string KeymasterClientId = "65b708073fc0480ea92a077233ca87bd";
    public const int MaxVolume = 65535;
    public const int DefaultVolumeSteps = 64;

    static readonly string[] DesktopSupportedTypes =
    {
        "audio/ad", "audio/episode", "audio/episode+track", "audio/interruption", "audio/local", "audio/media",
        "audio/podcast-chapter", "audio/track", "audio/user-highlight", "video/ad", "video/episode",
        "video/podcast-chapter", "video/track", "video/user-highlight",
    };

    readonly string _deviceId;
    readonly string _deviceName;
    readonly string _clientId;
    readonly Func<bool> _isPrivateSession;
    int _volume;

    public ConnectStateBuilder(string deviceId, string deviceName, string? clientId = null, int volume = MaxVolume / 2,
        Func<bool>? isPrivateSession = null)
    {
        _deviceId = deviceId;
        _deviceName = deviceName;
        _clientId = clientId ?? KeymasterClientId;
        _volume = Math.Clamp(volume, 0, MaxVolume);
        _isPrivateSession = isPrivateSession ?? (() => false);
    }

    public string DeviceId => _deviceId;
    public void SetVolume(int spotifyVolume) => _volume = Math.Clamp(spotifyVolume, 0, MaxVolume);

    public DeviceInfo BuildDeviceInfo()
    {
        var info = new DeviceInfo
        {
            CanPlay = true,
            Volume = (uint)_volume,
            Name = _deviceName,
            DeviceId = _deviceId,
            DeviceType = DeviceType.Computer,
            DeviceSoftwareVersion = SpotifyClientIdentity.DeviceSoftwareVersion,
            ClientId = _clientId,
            SpircVersion = SpotifyClientIdentity.SpircVersion,
            Capabilities = BuildCapabilities(),
            Brand = "spotify",
            Model = "PC laptop",
            License = "premium",          // Recently-Played / play-count eligibility — load-bearing
            IsPrivateSession = _isPrivateSession(),
        };
        info.MetadataMap["debug_level"] = "1";
        info.MetadataMap["tier1_port"] = "0";
        return info;
    }

    static Capabilities BuildCapabilities(int volumeSteps = DefaultVolumeSteps)
    {
        var c = new Capabilities
        {
            CanBePlayer = true,
            GaiaEqConnectId = true,
            SupportsLogout = true,
            IsObservable = true,
            CommandAcks = true,
            SupportsRename = false,
            SupportsPlaylistV2 = true,
            IsControllable = true,
            SupportsExternalEpisodes = true,
            SupportsSetBackendMetadata = true,
            SupportsTransferCommand = true,
            SupportsCommandRequest = true,
            VolumeSteps = volumeSteps,
            SupportsGzipPushes = true,
            NeedsFullPlayerState = true,   // pull full cluster snapshots, not deltas — the projection reconciles from full
            SupportsSetOptionsCommand = true,
            SupportsHifi = new CapabilitySupportDetails { FullySupported = true, UserEligible = true, DeviceSupported = true },
            SupportsDj = true,
            SupportedAudioQuality = AudioQuality.VeryHigh,   // 320 kbps OGG (claiming HIFI would lie about FLAC)
        };
        foreach (var t in DesktopSupportedTypes) c.SupportedTypes.Add(t);
        return c;
    }

    /// <summary>Serialize a PutStateRequest from OUR local playback snapshot (null = empty player_state, for the initial
    /// NewConnection announce). Matches the DeviceStatePublisher's builder delegate. <paramref name="nowMs"/> overridable
    /// for deterministic tests.</summary>
    public byte[] BuildPutState(PutStateReasonKind reason, LocalPlaybackSnapshot? snap, uint messageId, bool isActive, long? nowMs = null)
    {
        long ts = nowMs ?? DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        if (snap is { } sv) SetVolume((int)Math.Round(Math.Clamp(sv.Volume01, 0, 1) * MaxVolume));   // DeviceInfo.Volume from our live volume
        var ps = snap is { } s ? BuildPlayerState(s, ts) : new ProtoPlayerState { Timestamp = ts };
        var req = new PutStateRequest
        {
            MemberType = MemberType.ConnectState,
            Device = new Device
            {
                DeviceInfo = BuildDeviceInfo(),
                PlayerState = ps,
                PrivateDeviceInfo = new PrivateDeviceInfo { Platform = SpotifyClientIdentity.GetPrivateDevicePlatform() },
            },
            IsActive = isActive,
            PutStateReason = reason switch
            {
                PutStateReasonKind.NewConnection => PutStateReason.NewConnection,
                PutStateReasonKind.VolumeChanged => PutStateReason.VolumeChanged,
                PutStateReasonKind.BecameInactive => PutStateReason.BecameInactive,
                _ => PutStateReason.PlayerStateChanged,
            },
            MessageId = messageId,
            ClientSideTimestamp = (ulong)ts,
        };
        if (snap is { } s2)
        {
            if (s2.StartedPlayingAtMs > 0) req.StartedPlayingAt = (ulong)s2.StartedPlayingAtMs;
        }
        return req.ToByteArray();
    }

    static ProtoPlayerState BuildPlayerState(LocalPlaybackSnapshot s, long ts)
    {
        string contextUri = s.ContextUri ?? "";
        string feature = FeatureOf(contextUri, s.ContextMetadata);
        var ps = new ProtoPlayerState
        {
            Timestamp = ts,
            ContextUri = contextUri,
            ContextUrl = string.IsNullOrEmpty(contextUri) ? "" : "context://" + contextUri,
            PlayOrigin = new PlayOrigin
            {
                FeatureIdentifier = feature,
                FeatureVersion = SpotifyClientIdentity.XpuiSnapshotVersion,
                ReferrerIdentifier = feature,
            },
            PositionAsOfTimestamp = s.PositionMs,
            Duration = s.DurationMs,
            // Spotify desktop keeps is_playing=true while paused (transport engaged, audio frozen).
            IsPlaying = s.IsPlaying || s.IsPaused,
            IsPaused = s.IsPaused,
            PlaybackSpeed = s.IsPaused ? 0.0 : 1.0,
            PlaybackId = s.PlaybackId,
            SessionId = s.SessionId,
            QueueRevision = s.QueueRevision ?? "",
            Track = ToProvided(s.Track, contextUri, s.InteractionId, s.PageInstanceId),
            Index = new ContextIndex { Track = (uint)Math.Max(0, s.ContextIndex) },
            Options = new ContextPlayerOptions
            {
                ShufflingContext = s.Shuffle,
                RepeatingContext = s.Repeat == RepeatMode.Context,
                RepeatingTrack = s.Repeat == RepeatMode.Track,
            },
            Restrictions = new Restrictions(),
            PlaybackQuality = new PlaybackQuality
            {
                BitrateLevel = BitrateLevel.High,
                Strategy = BitrateStrategy.CachedFile,
                TargetBitrateLevel = BitrateLevel.High,
                TargetBitrateAvailable = true,
                HifiStatus = HiFiStatus.Off,
            },
        };
        foreach (var (k, v) in s.ContextMetadata)
            if (!string.IsNullOrEmpty(k)) ps.ContextMetadata[k] = v ?? "";
        ps.ContextMetadata["player.arch"] = "2";
        if (s.IsPaused)
        {
            ps.Restrictions.DisallowPausingReasons.Add("already_paused");
            if (s.ContextIndex <= 0 && s.PrevTracks.Count == 0)
                ps.Restrictions.DisallowSkippingPrevReasons.Add("no_prev_track");
        }
        else if (s.IsPlaying)
            ps.Restrictions.DisallowResumingReasons.Add("not_paused");
        foreach (var t in s.PrevTracks) ps.PrevTracks.Add(ToProvided(t, contextUri, s.InteractionId, s.PageInstanceId));
        foreach (var t in s.NextTracks) ps.NextTracks.Add(ToProvided(t, contextUri, s.InteractionId, s.PageInstanceId));
        return ps;
    }

    static ProvidedTrack ToProvided(in SnapshotTrack t, string contextUri, string interactionId, string pageInstanceId)
    {
        var pt = new ProvidedTrack
        {
            Uri = t.Uri,
            Uid = t.Uid ?? "",
            Provider = string.IsNullOrEmpty(t.Provider) ? "context" : t.Provider,
        };
        var meta = pt.Metadata;
        if (t.Metadata is { Count: > 0 })
            foreach (var (k, v) in t.Metadata)
                if (!string.IsNullOrEmpty(k)) meta[k] = v ?? "";

        AddIfMissing(meta, "title", t.Title);
        AddIfMissing(meta, "artist_name", t.ArtistName);
        AddIfMissing(meta, "album_title", t.AlbumTitle);
        AddIfMissing(meta, "album_uri", t.AlbumUri);
        AddIfMissing(meta, "artist_uri", t.ArtistUri);

        bool isVideo = IsVideoTrack(t);
        bool isAutoplay = pt.Provider == "autoplay";
        bool isQueue = pt.Provider == "queue";
        if (!string.IsNullOrEmpty(contextUri) && pt.Provider == "context")
        {
            AddIfMissing(meta, "context_uri", contextUri);
            if (!isVideo) AddIfMissing(meta, "entity_uri", contextUri);
        }
        if (!string.IsNullOrEmpty(t.ImageUrl))
        {
            var image = SpotifyImage(t.ImageUrl);
            AddIfMissing(meta, "image_url", image);
            AddIfMissing(meta, "image_small_url", image);
            AddIfMissing(meta, "image_large_url", image);
            AddIfMissing(meta, "image_xlarge_url", image);
        }
        if (isQueue) meta["is_queued"] = "true";
        if (isAutoplay) meta["autoplay.is_autoplay"] = "true";
        if (!isAutoplay && !isQueue)
        {
            AddIfMissing(meta, "actions.skipping_prev_past_track", "resume");
            AddIfMissing(meta, "actions.skipping_next_past_track", "resume");
        }
        AddIfMissing(meta, "track_player", "audio");
        AddIfMissing(meta, "interaction_id", interactionId);
        AddIfMissing(meta, "page_instance_id", pageInstanceId);
        if (!isVideo && !isQueue && !isAutoplay)
        {
            if (t.ViewIndex >= 0) AddIfMissing(meta, "view_index", t.ViewIndex.ToString(System.Globalization.CultureInfo.InvariantCulture));
            AddIfMissing(meta, "iteration", "0");
        }
        return pt;
    }

    static void AddIfMissing(IDictionary<string, string> metadata, string key, string? value)
    {
        if (!string.IsNullOrEmpty(value) && !metadata.ContainsKey(key)) metadata[key] = value;
    }

    static bool IsVideoTrack(in SnapshotTrack t)
    {
        if (t.HasVideo) return true;
        var metadata = t.Metadata;
        if (metadata is null) return false;
        if (metadata.TryGetValue("track_player", out var player) && player == "video") return true;
        if (metadata.TryGetValue("media.type", out var media) && (media == "video" || media == "mixed")) return true;
        return metadata.ContainsKey("media.manifest_id") || metadata.ContainsKey("save_track.uri");
    }

    static string FeatureOf(string contextUri, IReadOnlyDictionary<string, string> metadata)
    {
        if (metadata.TryGetValue("format_list_type", out var listType) && listType == "liked-songs") return "your_library";
        if (metadata.ContainsKey("liked_songs_collection_uri")) return "your_library";
        if (contextUri.Contains(":collection", StringComparison.Ordinal)) return "your_library";
        if (contextUri.Contains(":album:", StringComparison.Ordinal)) return "album";
        if (contextUri.Contains(":artist", StringComparison.Ordinal)) return "artist";
        if (contextUri.Contains(":playlist:", StringComparison.Ordinal)) return "playlist";
        if (contextUri.Contains(":episode:", StringComparison.Ordinal)) return "home";
        return "harmony";
    }

    static string SpotifyImage(string url)
    {
        const string prefix = "https://i.scdn.co/image/";
        return url.StartsWith(prefix, StringComparison.Ordinal) ? "spotify:image:" + url[prefix.Length..] : url;
    }
}
