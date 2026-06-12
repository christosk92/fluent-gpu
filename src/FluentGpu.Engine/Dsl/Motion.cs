namespace FluentGpu.Dsl;

/// <summary>
/// The real Fluent 2 / WinUI motion durations + entrance metrics (durations only — the easing curves live in
/// FluentGpu.Foundation.Easing, selected by the Hooks-layer motion helpers, since Dsl does not reference Animation).
/// Canonical WinUI names (the motion audit's correction): ControlFaster = 83ms (BrushTransition, hover/press),
/// ControlFast = 167ms (ControlFastAnimationDuration — knob travel, settle), ControlNormal = 250ms
/// (ControlNormalAnimationDuration — reposition / pane motion).
/// </summary>
public static class Motion
{
    public const float ControlFaster = 83f;   // WinUI BrushTransition / hover-press cross-fade
    public const float ControlFast = 167f;    // WinUI ControlFastAnimationDuration
    public const float ControlNormal = 250f;  // WinUI ControlNormalAnimationDuration
    public const float Fast = 150f;
    public const float Fade = 200f;           // fade in/out (the WinUI implicit OpacityAnimation)
    public const float PointToPoint = 250f;   // reposition / move (alias of ControlNormal)
    public const float OffsetEntrance = 400f; // the WinUI implicit OffsetAnimation (entrance)
    public const float Slow = 500f;

    public const float EntranceOffsetPx = 24f;   // the WinUI show-transition From="0,24,0"

    /// <summary>Host-set from SPI_GETCLIENTAREAANIMATION; motion helpers no-op the entrance when true.</summary>
    public static bool ReducedMotion;
}
