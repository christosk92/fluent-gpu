using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Pal;

namespace Wavee;

/// <summary>
/// The ambient service bag every action Execute/enablement lambda reads — ONE reference-stable instance, provided at
/// the app root (WaveeShell, next to NavCtx) via <see cref="Slot"/>. Fields are plain mutable and refreshed by the
/// shell each render (the instance never changes, so the provide never churns context consumers); <see cref="Overlay"/>
/// is bound by <see cref="ActionServicesOverlayBinder"/> INSIDE the OverlayHost subtree, because the real
/// <see cref="IOverlayService"/> only exists below the host (the context default is a null service). Context, not ctor
/// args — the component-props-freeze contract.
/// </summary>
public sealed class ActionServices
{
    public static readonly Context<ActionServices?> Slot = new(null);

    public PlaybackBridge? Playback;
    public LibraryBridge? Library;
    /// <summary>The composition root (<c>.Player</c> = the IPlaybackPlayer verbs).</summary>
    public Services? Svc;
    /// <summary>User playlists for the Add-to-playlist submenu.</summary>
    public LibraryStore? Store;
    /// <summary>Navigation (<c>HistoryStore.NavCtx</c> value): <c>go("album:"+uri, name)</c> etc.</summary>
    public Action<string, string?>? Go;
    /// <summary>The real overlay service — for imperative opens at INVOKE time (picker dialog, confirm/rename
    /// dialogs). Menu ATTACHMENT resolves its own <c>UseContext(Overlay.Service)</c> per surface instead (an attach
    /// captures the service by value at render time, before this binder may have run).</summary>
    public IOverlayService? Overlay;
    public IClipboard? Clipboard;
    /// <summary>UI-thread marshal (UsePost) for async completions that must touch signals.</summary>
    public Action<Action>? Post;
}

/// <summary>The action context an <see cref="AppAction"/> receives: the WHAT (<see cref="Target"/>) + the HOW
/// (<see cref="S"/>). Built at open/render time, passed by-value, never retained.</summary>
public readonly struct ActionContext(ActionTarget target, ActionServices s)
{
    public readonly ActionTarget Target = target;
    public readonly ActionServices S = s;
}

/// <summary>An attached context menu for a shared element FACTORY (MediaCard, TrackRow): the overlay service the
/// calling component resolved plus the lazy model factory — applied onto the factory's root box via
/// <c>ContextMenu.Attach</c>. Null = no menu (the factory renders unchanged).</summary>
public readonly record struct MenuAttach(IOverlayService Overlay, Func<ContextMenuModel?> Factory);

internal static class MenuAttachExtensions
{
    /// <summary>Apply an optional attached menu onto a factory's root box (chains, never clobbers, its handlers).</summary>
    public static BoxEl WithMenu(this BoxEl el, MenuAttach? menu)
        => menu is { } m ? el.WithContextMenu(m.Overlay, m.Factory) : el;
}

/// <summary>Mounted once inside the OverlayHost subtree (a zero-size, non-interactive leaf): copies the REAL overlay
/// service into the stable <see cref="ActionServices"/> bag so invoke-time dialogs (confirm / rename / picker) can
/// open. The bag instance is a mount seed (reference-stable for the app lifetime) — props-freeze-safe.</summary>
sealed class ActionServicesOverlayBinder : Component
{
    readonly ActionServices _acts;
    public ActionServicesOverlayBinder(ActionServices acts) => _acts = acts;

    public override Element Render()
    {
        _acts.Overlay = UseContext(Overlay.Service);
        return new BoxEl { HitTestVisible = false };
    }
}
