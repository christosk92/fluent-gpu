namespace FluentGpu.Dsl;

/// <summary>
/// The real Fluent 2 / WinUI motion durations + entrance metrics (durations only — the easing curves live in
/// FluentGpu.Animation.Easing, selected by the Hooks-layer motion helpers, since Dsl does not reference Animation).
/// </summary>
public static class Motion
{
    public const float ControlFast = 83f;    // control brush transition (PointerOver/Pressed)
    public const float Fast = 150f;
    public const float Fade = 200f;          // fade in/out (the WinUI implicit OpacityAnimation)
    public const float PointToPoint = 250f;  // reposition / move
    public const float OffsetEntrance = 400f;// the WinUI implicit OffsetAnimation (entrance)
    public const float Slow = 500f;

    public const float EntranceOffsetPx = 24f;   // the WinUI show-transition From="0,24,0"

    /// <summary>Host-set from SPI_GETCLIENTAREAANIMATION; motion helpers no-op the entrance when true.</summary>
    public static bool ReducedMotion;
}
