namespace FluentGpu.Hooks;

/// <summary>
/// Ambient access to the host's live re-theme trigger. After mutating the active theme (<c>Theme.Dark = …</c>,
/// <c>Tok.Use</c>/<c>Tok.SetAccent</c>) a component invokes this to re-render every mounted component IN PLACE — so each
/// re-reads the new token set — and cross-fade the resulting fill/border/text color diffs over the given duration
/// (milliseconds; <c>0</c> = snap). No remount: node identity and component state survive, so scroll position, selection,
/// and in-flight gestures are preserved (unlike the old re-key-the-root workaround).
/// <para>
/// Read it with <c>UseContext(ThemeControl.Request)</c>. The default (no host / headless tests) is
/// <see langword="null"/>; callers should null-check (a token swap simply won't animate without a host driving frames).
/// </para>
/// </summary>
public static class ThemeControl
{
    /// <summary>The host-provided live-re-theme trigger: <c>request(ms)</c> schedules an in-place re-render of the whole
    /// tree and a cross-fade of the color diffs over <c>ms</c> (default the host uses is 250ms; pass 0 to snap). Null when
    /// no host published one (headless/test).</summary>
    public static readonly Context<System.Action<float>?> Request = new(null);
}
