using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The countdown card for an announced-but-unreleased album: an eyebrow, the time remaining, and a progress
/// arc that fills as the wait elapses.
///
/// Shown on the album detail rail (from <c>DetailModel.IsPreRelease</c>/<c>PreReleaseEnd</c>) and on the artist page
/// (from <c>ArtistExtras.PreRelease</c>). Both carry the same thing — an instant to count down to — so this takes the
/// instant and nothing else, and neither surface owns a second copy of the formatting.
///
/// The clock is <c>UseInterval</c>, not a <c>System.Threading.Timer</c>: detail pages are parked by
/// <c>Flow.KeepAlive</c> rather than unmounted, and UseInterval auto-pauses while parked or minimized. A raw timer
/// would keep waking the app to re-render a card nobody is looking at.</summary>
sealed class PreReleaseCountdown : Component
{
    /// <summary>When the release unlocks. Frozen at mount — the caller keys this component on it.</summary>
    public required DateTimeOffset ReleaseAt { get; init; }
    /// <summary>Accent for the arc, so the card belongs to the page it sits on. A thunk rather than a value because the
    /// artist page derives its accent from art that lands AFTER the page mounts — reading it inside Render subscribes,
    /// so the ring re-tints when the palette arrives instead of staying frozen at the mount-time default.</summary>
    public required Func<ColorF> Accent { get; init; }
    /// <summary>When the countdown started, so the arc can show elapsed progress rather than a bare number. Null (the
    /// common case — the wire never states it) draws the ring as a plain track.</summary>
    public DateTimeOffset? AnnouncedAt { get; init; }

    const float RingSize = 34f;

    // A second-resolution tick is only worth its wakes in the last minute; past that a minute-resolution clock shows
    // exactly the same string 59 times out of 60. The interval is chosen from the remaining distance for that reason.
    const float FastTickMs = 1000f;
    const float SlowTickMs = 30_000f;

    readonly Signal<long> _nowTicks = new(0);

    public override Element Render()
    {
        // Seeded on first render rather than at construction: a component built during a parked-page rebuild could
        // otherwise carry a stale "now" until its first tick landed.
        if (_nowTicks.Peek() == 0) _nowTicks.Value = DateTimeOffset.UtcNow.UtcTicks;

        var now = new DateTimeOffset(_nowTicks.Value, TimeSpan.Zero);   // subscribe → the copy re-renders on each tick
        TimeSpan remaining = ReleaseAt - now;
        bool released = remaining <= TimeSpan.Zero;

        UseInterval(() => _nowTicks.Value = DateTimeOffset.UtcNow.UtcTicks,
            remaining.TotalMinutes <= 1d ? FastTickMs : SlowTickMs,
            enabled: !released);

        string headline = released ? Loc.Get(Strings.Detail.PreReleaseOut) : Remaining(remaining);
        var accent = Accent();   // read inside Render → subscribes, so a late palette re-tints the ring

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = Spacing.M, MinWidth = 0f,
            Padding = new Edges4(Spacing.M, Spacing.M, Spacing.M, Spacing.M),
            Corners = CornerRadius4.All(Radii.Card),
            Fill = Tok.FillCardDefault,
            BorderWidth = 1f, BorderColor = Tok.StrokeCardDefault,
            Children =
            [
                Ring(released ? 1f : Progress(now), accent),
                new BoxEl
                {
                    Direction = 1, Gap = 2f, MinWidth = 0f, Grow = 1f,
                    Children =
                    [
                        Caption(Loc.Get(Strings.Detail.PreReleaseEyebrow)) with
                        {
                            Color = accent, Weight = 700, CharSpacing = 40f, MaxLines = 1,
                        },
                        new TextEl(headline)
                        {
                            Size = 15f, Weight = 700, Color = Tok.TextPrimary, MaxLines = 1,
                            Wrap = TextWrap.NoWrap, Trim = TextTrim.CharacterEllipsis,
                        },
                    ],
                },
            ],
        };
    }

    /// <summary>Fraction of the announced wait already elapsed. Without an announcement instant there is no meaningful
    /// denominator, so the ring stays an empty track rather than inventing one.</summary>
    float Progress(DateTimeOffset now)
    {
        if (AnnouncedAt is not { } start) return 0f;
        double total = (ReleaseAt - start).TotalSeconds;
        if (total <= 0d) return 1f;
        return Math.Clamp((float)((now - start).TotalSeconds / total), 0f, 1f);
    }

    /// <summary>The single largest unit still meaningful — "3 days", then "5 hours", then "12 minutes", then seconds.
    /// One unit, not "3d 4h 12m 09s": the card answers "how long until I can play this", and a running seconds field
    /// on a three-week wait is noise that also forces a wake every second to redraw.
    ///
    /// Internal so the static surfaces that only need the STRING (the hero eyebrow pill, the shy pill) share this one
    /// phrasing instead of each inventing their own.</summary>
    internal static string Remaining(TimeSpan left)
    {
        if (left.TotalDays >= 1d) return Strings.Detail.PreReleaseDays((int)left.TotalDays);
        if (left.TotalHours >= 1d) return Strings.Detail.PreReleaseHours((int)left.TotalHours);
        if (left.TotalMinutes >= 1d) return Strings.Detail.PreReleaseMinutes((int)left.TotalMinutes);
        return Strings.Detail.PreReleaseSeconds(Math.Max(0, (int)left.TotalSeconds));
    }

    /// <summary>Track + sweep, the ProgressRing shape used elsewhere in the app (MediaCard's countdown ring). Static
    /// arcs rather than an animated trim: this sweep advances over days, so a keyframe run would be wrong.</summary>
    static Element Ring(float progress, ColorF accent) => new BoxEl
    {
        ZStack = true, Width = RingSize, Height = RingSize, Shrink = 0f, HitTestPassThrough = true,
        Children =
        [
            new BoxEl
            {
                Width = RingSize, Height = RingSize,
                Arc = new ArcSpec(Tok.FillControlTertiary, 3f, 0f, 360f, RoundCaps: false),
            },
            new BoxEl
            {
                Width = RingSize, Height = RingSize,
                Arc = new ArcSpec(accent, 3f, -90f, 360f * Math.Clamp(progress, 0f, 1f), RoundCaps: true),
            },
        ],
    };
}
