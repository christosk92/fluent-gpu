# TaskbarManager — progress, overlay, thumbnail toolbar

`FluentGpu.WindowsApi.Shell.TaskbarManager` drives the Windows 7+ taskbar button over `ITaskbarList3`
(TerraFX vtable, AOT-clean). Best-effort chrome: `CoCreateInstance`/`HrInit` failure latches every method to a
no-op rather than throwing. **UI thread only** — pass `FluentApp.WindowHandle` (published once the window
exists, cleared when the run ends). Do not pass a window owned by another process.

## Surface

| Method | Shell call | Notes |
|---|---|---|
| `SetProgress(hwnd, completed, total)` | `SetProgressValue` | Determinate fill. Pair with `SetProgressState`. |
| `SetProgressState(hwnd, TaskbarProgressState)` | `SetProgressState` | `None` / `Indeterminate` / `Normal` / `Error` / `Paused`. |
| `ClearProgress(hwnd)` | `SetProgressState(NOPROGRESS)` | |
| `SetOverlayIcon(hwnd, iconPath, description)` | `SetOverlayIcon` | `.ico` via `LoadImageW`; `null` path clears. Shell copies the `HICON`; we `DestroyIcon` immediately. Bad path → `InvalidOperationException`. |
| `SetThumbButtons(hwnd, params ThumbButton[])` | `ThumbBarAddButtons` then `ThumbBarUpdateButtons` | See add-once contract below. Max 7. |
| `UpdateThumbButton(hwnd, ThumbButton)` | `ThumbBarUpdateButtons` | No-op if that id was not in the original add set. |
| `NotifyTaskbarButtonCreated(hwnd)` | — | Drops the add-once latch + cached `HICON`s so the next `SetThumbButtons` **adds** again. |

`ThumbButton(int Id, string? IconPath, string Tooltip, bool Enabled = true, bool DismissOnClick = false)` —
`Id` is what `FluentApp.ThumbButtonClicked` delivers. `IconPath` is an `.ico` (or `null` for no glyph);
`DismissOnClick` maps to `THBF_DISMISSONCLICK`.

## Add-once / update-after

The shell will **add** a thumbnail toolbar only once per HWND. `SetThumbButtons`:

1. First successful call per HWND → `ThumbBarAddButtons` (count and left-to-right order freeze).
2. Later calls → `ThumbBarUpdateButtons`. Ids omitted from the new set are hidden (`THBF_HIDDEN`); **new
   ids after the first add are ignored** (the shell cannot grow the set).
3. Empty `buttons` on a not-yet-added HWND is a no-op; on an already-added HWND hides every button.

Call **after the window is shown** (`FluentApp.WindowHandle` is valid from `FluentApp.Run` after
`CreateWindow`; show happens inside the same `Run`). An add made before the taskbar button exists fails
silently (latched as "not yet added") — retry after show, or from `TaskbarButtonCreated`.

If `ThumbBarUpdateButtons` fails (explorer restarted and discarded the toolbar), `SetThumbButtons` retries
as `ThumbBarAddButtons`.

## `TaskbarButtonCreated` caveat

Explorer broadcasts the registered `"TaskbarButtonCreated"` message when the window's taskbar button
appears, and **again if explorer restarts** (which wipes the toolbar). The Win32 PAL forwards it:

`RegisterWindowMessageW("TaskbarButtonCreated")` → `WndProc` → `Win32App.RaiseTaskbarButtonCreated` →
`IPlatformApp.TaskbarButtonCreated` → `AppHost` stash/drain at `Paint` → `FluentApp.TaskbarButtonCreated`.

The PAL does **not** re-add buttons. On that event:

```csharp
FluentApp.TaskbarButtonCreated += () =>
{
    nint hwnd = FluentApp.WindowHandle;
    TaskbarManager.NotifyTaskbarButtonCreated(hwnd);   // reset add-once + HICONs
    TaskbarManager.SetThumbButtons(hwnd, /* same buttons */);
};
```

Without a subscriber, an explorer restart leaves the toolbar empty until the next explicit
`SetThumbButtons`.

## `ThumbButtonClicked` event chain

Clicks are `WM_COMMAND` with `HIWORD(wParam) == THBN_CLICKED (0x1800)` and `LOWORD(wParam) == button id`.
The window-proc branch early-outs on any other `WM_COMMAND` (menus/accelerators) with **zero allocation**.

```
WndProc WM_COMMAND/THBN_CLICKED
  → Win32App.RaiseThumbButtonClicked(int id)          // UI thread
  → IPlatformApp.ThumbButtonClicked                   // engine seam, Action<int>, TerraFX-free
  → AppHost stashes id + WakeFrame
  → AppHost.ThumbButtonClicked at Paint top           // handlers may write signals
  → FluentApp.ThumbButtonClicked                      // app-layer static relay
```

Same stash/drain as `ActivationRedirected`. Headless never fires. Subscribe on the UI thread:

```csharp
FluentApp.ThumbButtonClicked += id => { /* play / pause / skip */ };
TaskbarManager.SetThumbButtons(FluentApp.WindowHandle,
    new ThumbButton(1, playIco, "Play"),
    new ThumbButton(2, nextIco, "Next"));
```

`HICON`s loaded for thumb buttons are kept until replaced (`DestroyIcon` on replace) or
`NotifyTaskbarButtonCreated` (destroy all). Overlay icons are still freed immediately after
`SetOverlayIcon` — the shell copies those during the call.
