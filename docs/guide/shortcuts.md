# Keyboard shortcuts

Wavee keyboard chords. The command palette (Ctrl+K) is the fastest way to jump to a page or run a command.

## Global

| Shortcut | Action |
|---|---|
| **Ctrl+K** | Open the command palette |
| **Ctrl+F** | Focus the search bar |
| **Ctrl+T** | Open a new Home tab |
| **Alt+Left** | Back |
| **Alt+Right** | Forward |
| **Space** | Play / pause (not while typing in a text field) |
| **F11** | Toggle video full screen (only while a video is playing; otherwise a no-op) |
| **Escape** | Close the palette, a flyout, or the sidebar drawer |

Mouse back/forward buttons (XButton1/2) **are** wired. The OS delivers them as `WM_APPCOMMAND` rather than as a click,
so they arrive through the PAL seam: `WaveeShell` subscribes `FluentApp.AppNavigationCommand` and routes it to the same
`Back()` / `Forward()` that Alt+Left / Alt+Right use. The same path covers keyboards with dedicated Back/Forward keys.

## Planned in the next batch

Not wired yet — listed so the chords are not claimed twice.

| Shortcut | Action |
|---|---|
| **Ctrl+Right** / **Ctrl+Left** | Next / previous track |
| **Shift+Right** / **Shift+Left** | Seek ±5 s |
| **Ctrl+Up** / **Ctrl+Down** | Volume up / down |
| **Ctrl+Shift+Down** | Mute |
| **Ctrl+S** | Toggle shuffle |
| **Ctrl+R** | Toggle repeat |
| **Alt+Shift+B** | Like / unlike the current track |
| **Ctrl+/** | Open this shortcuts dialog |

## Command palette (Ctrl+K)

Type to filter commands. Prefix with **`>`** to search commands only (same as VS Code).

Without `>`, a **Search for …** row appears that opens the Search page with your query. Catalog results themselves live on that page.

**Up** / **Down** move the highlight. **Enter** runs the selected command. **Escape** or a click outside closes the palette.

### Built-in commands

- **Go to** Home, Search, Your Library, Recents, Settings
- **Playback** Play/pause, Next, Previous, Shuffle, Repeat
- **Settings** Theme (light/dark), Crossfade on/off
- **Now playing** actions from the first-party extension table (like, go to album/artist, copy link, song radio, pin this page, …) when they apply

## In a text field

Space inserts a space. Play/pause does not fire while the caret is in an editor.
