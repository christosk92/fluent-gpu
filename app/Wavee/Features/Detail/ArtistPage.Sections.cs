using System;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// Shared section scaffolding (the accent-bar headers + section wrappers) and small formatting helpers used across the
// artist-page partials (.Hero / .TopTracks / .Discography / .Shelves / .Biography / .Skeleton).
sealed partial class ArtistPage : Component
{
    // ── section scaffolding ──────────────────────────────────────────────────────────────────────────────
    // The fixed-accent artist-page header now delegates to the shared, color-parameterized Surfaces.AccentHeader
    // (same visual with Tok.AccentDefault) so the home accent bands and these stay one definition.
    internal BoxEl AccentHeader(string title) => Surfaces.AccentHeader(title, _accent);

    internal BoxEl AccentHeader(string title, int count) => new BoxEl
    {
        Direction = 0, Gap = 10f, AlignItems = FlexAlign.Center,
        Children =
        [
            new BoxEl
            {
                Width = 3f, MinHeight = 22f, AlignSelf = FlexAlign.Stretch,
                Corners = CornerRadius4.All(1.5f), Fill = _accent,
            },
            WaveeType.RailHeader(title) with { MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
            new TextEl(count.ToString()) { Size = 15f, Weight = 600, Color = Tok.TextTertiary },
        ],
    };

    Element Section(string title, Element body) => new BoxEl
    {
        Direction = 1, Gap = Spacing.M, Children = [AccentHeader(title), body],
    };

    Element SectionN(string title, int count, Element body) => new BoxEl
    {
        Direction = 1, Gap = Spacing.M,
        Children = [ AccentHeader(title, count), body ],
    };

    // ── shared formatting helpers ────────────────────────────────────────────────────────────────────────
    static string Count(long n) => n.ToString("N0");
    static string Dur(long ms) { var t = TimeSpan.FromMilliseconds(ms); return t.Hours > 0 ? $"{t.Hours}:{t.Minutes:D2}:{t.Seconds:D2}" : $"{t.Minutes}:{t.Seconds:D2}"; }
    static ColorF Scrim(float a) => ColorF.FromRgba(0, 0, 0) with { A = a };               // black-with-alpha hero scrim stop
    static readonly ColorF WhiteText = ColorF.FromRgba(255, 255, 255);
}
