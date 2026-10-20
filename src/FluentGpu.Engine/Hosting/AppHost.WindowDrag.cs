using FluentGpu.Foundation;
using FluentGpu.Pal;

namespace FluentGpu.Hosting;

// Declarative window drag regions (BoxEl.WindowDrag, docs/plans/app-authoring-ergonomics-implementation.md §1).
//
// The platform keeps ONE region list per window. Two producers feed it: app pushes through InputHooks.SetTitleBarRegions
// (the TitleBar control, an app's own pop-out band) and the engine collector here (every reachable BoxEl.WindowDrag box,
// re-derived at phase 7.8 so scroll, resize, transforms and parked pages are followed with no effect of the app's). The
// host stores the app list separately and pushes the COMPOSITION, first match wins at WM_NCHITTEST:
//
//   [app non-Caption entries: islands + caption buttons] + [engine NoDrag as Client] + [engine Drag as Caption,
//   AppContextMenu] + [app Caption entries: TitleBar's catch-all band]
//
// so caption buttons always win, an engine island beats any drag band, and TitleBar's whole-bar band never shadows an
// engine island. The merged list is pushed only when it differs element-wise from the last push (reused buffers; zero
// allocation once grown). Gate: no declared box and nothing engine-collected last frame ⇒ the collector does no work.
public sealed partial class AppHost
{
    private TitleBarRegion[] _appTitleRegions = [];      // the app's last push (copied — the caller reuses its array)
    private int _appTitleRegionCount;
    private TitleBarRegion[] _engineTitleRegions = [];   // engine NoDrag (Client) entries, then Drag (Caption) entries
    private int _engineNoDragCount, _engineDragCount;
    private TitleBarRegion[] _mergedTitleRegions = [];   // scratch: the composition being built
    private TitleBarRegion[] _pushedTitleRegions = [];   // what the window holds now (swapped with the scratch on push)
    private int _pushedTitleRegionCount;
    private readonly List<WindowDragRect> _windowDragScratch = new();

    /// <summary>Wire the app push seam and the unhandled caption right-click fallback (called once from the hooks block).</summary>
    private void WireWindowDragRegions()
    {
        _inputHooks.SetTitleBarRegions = OnAppTitleBarRegions;
        _dispatcher.OnNonClientContextUnhandled = ShowSystemMenuAtDip;
    }

    /// <summary>An app region push (<c>InputHooks.SetTitleBarRegions</c>): store a copy, recompose with the engine
    /// entries collected last, push if the composition changed.</summary>
    private void OnAppTitleBarRegions(TitleBarRegion[] regions, int count)
    {
        count = Math.Clamp(count, 0, regions.Length);
        if (_appTitleRegions.Length < count) _appTitleRegions = new TitleBarRegion[Math.Max(8, count)];
        regions.AsSpan(0, count).CopyTo(_appTitleRegions);
        _appTitleRegionCount = count;
        ComposeAndPushTitleBarRegions();
    }

    /// <summary>Phase 7.8: re-collect every reachable <c>BoxEl.WindowDrag</c> box (after the scroll pose, before
    /// record) and push the composition when it changed. Skips nodes rendered into popup windows (their rects are not in
    /// this window's client space) and clips every rect to the client area. The scratch also carries, as NoDrag entries,
    /// every interactive node painted ABOVE a Drag band (an open in-window menu, flyout, dialog button, the light-dismiss
    /// scrim, a toast — <c>SceneStore.CollectDragOccluders</c>), so pass 0 emits them as Client ahead of the Caption
    /// bands: a press on a popup over a drag area reaches the popup instead of starting the OS move loop.</summary>
    private void CollectWindowDragRegions()
    {
        if (!_scene.HasWindowDrag && _engineNoDragCount + _engineDragCount == 0) return;   // nothing declared, nothing to retract
        _windowDragScratch.Clear();
        _scene.CollectWindowDragRects(_windowDragScratch);
        if (_engineTitleRegions.Length < _windowDragScratch.Count)
            _engineTitleRegions = new TitleBarRegion[Math.Max(8, _windowDragScratch.Count * 2)];
        Size2 client = ClientSizeDip();
        var clientRect = new RectF(0f, 0f, client.Width, client.Height);
        int n = 0;
        // Two passes over the (small) scratch keep the NoDrag islands ahead of the Drag bands without a sort.
        for (int pass = 0; pass < 2; pass++)
        {
            WindowDragRole want = pass == 0 ? WindowDragRole.NoDrag : WindowDragRole.Drag;
            for (int i = 0; i < _windowDragScratch.Count; i++)
            {
                WindowDragRect e = _windowDragScratch[i];
                if (e.Role != want || IsUnderPopupWindowRoot(e.Node)) continue;
                RectF r = e.RectDip.Intersect(clientRect);
                if (r.IsEmpty) continue;
                _engineTitleRegions[n++] = pass == 0
                    ? new TitleBarRegion(r, TitleBarHit.Client)
                    : new TitleBarRegion(r, TitleBarHit.Caption, TitleBarRegionFlags.AppContextMenu);
            }
            if (pass == 0) _engineNoDragCount = n;
        }
        _engineDragCount = n - _engineNoDragCount;
        ComposeAndPushTitleBarRegions();
    }

    private bool IsUnderPopupWindowRoot(NodeHandle node)
    {
        if (_popupWindows.Count == 0) return false;
        for (NodeHandle a = node; !a.IsNull; a = _scene.Parent(a))
            for (int i = 0; i < _popupWindows.Count; i++)
                if (_popupWindows[i].Root == a) return true;
        return false;
    }

    private void ComposeAndPushTitleBarRegions()
    {
        int total = _appTitleRegionCount + _engineNoDragCount + _engineDragCount;
        if (_mergedTitleRegions.Length < total) _mergedTitleRegions = new TitleBarRegion[Math.Max(8, total * 2)];
        int n = 0;
        for (int i = 0; i < _appTitleRegionCount; i++)
            if (_appTitleRegions[i].Hit != TitleBarHit.Caption) _mergedTitleRegions[n++] = _appTitleRegions[i];
        for (int i = 0; i < _engineNoDragCount + _engineDragCount; i++) _mergedTitleRegions[n++] = _engineTitleRegions[i];
        for (int i = 0; i < _appTitleRegionCount; i++)
            if (_appTitleRegions[i].Hit == TitleBarHit.Caption) _mergedTitleRegions[n++] = _appTitleRegions[i];

        if (n == _pushedTitleRegionCount)
        {
            bool same = true;
            for (int i = 0; i < n; i++)
                if (!_mergedTitleRegions[i].Equals(_pushedTitleRegions[i])) { same = false; break; }
            if (same) return;   // unchanged frame: the window keeps what it has (no push, no copy)
        }
        (_pushedTitleRegions, _mergedTitleRegions) = (_mergedTitleRegions, _pushedTitleRegions);
        _pushedTitleRegionCount = n;
        _window.SetTitleBarRegions(_pushedTitleRegions.AsSpan(0, n));
    }

    /// <summary>An app-routed caption right-click nobody handled: open the system menu at the click (window DIP → the
    /// window's physical screen px through its client origin + effective scale).</summary>
    private void ShowSystemMenuAtDip(Point2 dip)
    {
        float s = _window.Scale <= 0f ? 1f : _window.Scale;
        Point2 origin = _window.ClientOriginPx;
        _window.ShowSystemMenu(new Point2(origin.X + dip.X * s, origin.Y + dip.Y * s));
    }
}
