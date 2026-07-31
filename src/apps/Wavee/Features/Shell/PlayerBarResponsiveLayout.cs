namespace Wavee;

/// <summary>The identity-first player-dock pressure tiers, ordered from the 300-DIP floor to the full desktop bar.</summary>
public enum PlayerBarTier : byte
{
    Minimal,
    Compact,
    Medium,
    Comfortable,
    Wide,
    Full,
}

/// <summary>
/// Pure width-to-tier resolver for the player dock. Widening commits immediately; narrowing holds the current
/// presentation through a 24-DIP dip so pointer resize cannot chatter structural controls at a threshold.
/// </summary>
internal static class PlayerBarResponsiveLayout
{
    public const float CompactW = 440f;
    public const float MediumW = 760f;
    public const float ComfortableW = 900f;
    public const float WideW = 1100f;
    public const float FullW = 1240f;
    public const float NarrowHysteresis = 24f;

    public static PlayerBarTier Nominal(float width) =>
        width >= FullW ? PlayerBarTier.Full :
        width >= WideW ? PlayerBarTier.Wide :
        width >= ComfortableW ? PlayerBarTier.Comfortable :
        width >= MediumW ? PlayerBarTier.Medium :
        width >= CompactW ? PlayerBarTier.Compact :
        PlayerBarTier.Minimal;

    public static PlayerBarTier Resolve(float width, PlayerBarTier current, bool initialized)
    {
        if (width <= 0f) return current;
        var nominal = Nominal(width);
        if (!initialized || nominal >= current) return nominal;
        var dipped = Nominal(width + NarrowHysteresis);
        return dipped < current ? dipped : current;
    }
}

/// <summary>The complete structural/metric policy for one player-bar tier.</summary>
internal readonly record struct PlayerBarLayout(
    PlayerBarTier Tier,
    bool ShowExpand,
    bool ShowDevices,
    bool ShowQueue,
    bool ShowVolumeSlider,
    bool ShowShuffleRepeat,
    bool ShowLikeSlot,
    bool ShowVolumeButton,
    bool ShowLyrics,
    bool ShowRemoteDeviceLine,
    bool ShowTimesElapsed,
    bool ShowTimesRemaining,
    bool ShowPrevNext,
    bool ShowSubtitle,
    float ButtonBox,
    float ButtonGlyph,
    float PrimaryBox,
    float PrimaryGlyph,
    float LeftW,
    float ArtSize,
    float RowGap,
    float RowPad,
    float ClusterGap,
    float LeftGap,
    float SeekGap,
    float RightGap,
    float TopEdgeWidth)
{
    public static PlayerBarLayout Initial(float width)
        => ForTier(PlayerBarResponsiveLayout.Nominal(width));

    public static PlayerBarLayout Resolve(float width, in PlayerBarLayout current, bool initialized)
        => ForTier(PlayerBarResponsiveLayout.Resolve(width, current.Tier, initialized));

    public static PlayerBarLayout ForTier(PlayerBarTier tier)
    {
        bool full = tier == PlayerBarTier.Full;
        bool wide = tier >= PlayerBarTier.Wide;
        bool comfortable = tier >= PlayerBarTier.Comfortable;
        bool medium = tier >= PlayerBarTier.Medium;
        bool compact = tier >= PlayerBarTier.Compact;

        return new PlayerBarLayout(
            Tier: tier,
            ShowExpand: full,
            ShowDevices: true,          // the device picker is the only route when local playback is unavailable
            ShowQueue: wide,
            ShowVolumeSlider: wide,
            ShowShuffleRepeat: comfortable,
            ShowLikeSlot: true,         // identity-first: the heart survives down to the 300-DIP floor
            ShowVolumeButton: medium,
            ShowLyrics: medium,
            ShowRemoteDeviceLine: comfortable,
            ShowTimesElapsed: medium,
            ShowTimesRemaining: compact,
            ShowPrevNext: medium,
            ShowSubtitle: true,         // title + artist are one indivisible identity block
            ButtonBox: comfortable ? 32f : medium ? 30f : compact ? 28f : 26f,
            ButtonGlyph: comfortable ? 16f : medium || compact ? 15f : 14f,
            PrimaryBox: comfortable ? 36f : medium ? 36f : compact ? 34f : 30f,
            PrimaryGlyph: comfortable || medium ? 20f : compact ? 18f : 16f,
            LeftW: tier switch
            {
                PlayerBarTier.Full => 260f,
                PlayerBarTier.Wide => 240f,
                PlayerBarTier.Comfortable => 230f,
                PlayerBarTier.Medium => 220f,
                PlayerBarTier.Compact => 180f,
                _ => 140f,
            },
            ArtSize: medium ? WaveeSize.ArtPlayerBar : 40f,
            RowGap: wide ? 8f : medium ? 6f : compact ? 4f : 3f,
            RowPad: wide ? 12f : medium ? 8f : compact ? 8f : 6f,
            ClusterGap: medium ? 4f : compact ? 3f : 2f,
            LeftGap: medium ? 8f : compact ? 6f : 4f,
            SeekGap: medium ? 6f : compact ? 5f : 4f,
            RightGap: medium ? 2f : compact ? 1f : 0f,
            TopEdgeWidth: 2400f);
    }
}
