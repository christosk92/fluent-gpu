using FluentGpu.Dsl;

namespace Wavee;

// Semantic type aliases so call sites read INTENT, not raw sizes. Every alias maps to the engine's WinUI type ramp
// (Ui.Caption/Body/BodyStrong/Subtitle/Title/Display in Dsl/Typography.cs). Never author a raw `TextEl { Size = … }`.
public static class WaveeType
{
    /// <summary>Track / album / playlist titles in lists. → Ui.BodyStrong (14/20 Semibold).</summary>
    public static TextEl TrackTitle(string s) => Ui.BodyStrong(s);

    /// <summary>Artist · duration · metadata. → Ui.Caption secondary (12/16).</summary>
    public static TextEl TrackMeta(string s) => Ui.Caption(s).Secondary();

    /// <summary>"Because you played…" section / rail headers. → Ui.Subtitle (20/28 Semibold).</summary>
    public static TextEl RailHeader(string s) => Ui.Subtitle(s);

    /// <summary>Page hero (playlist / album name). → Ui.Title (28/36 Semibold).</summary>
    public static TextEl PageHero(string s) => Ui.Title(s);

    /// <summary>Now-playing track title. → Ui.Subtitle.</summary>
    public static TextEl NowPlayingTitle(string s) => Ui.Subtitle(s);
}
