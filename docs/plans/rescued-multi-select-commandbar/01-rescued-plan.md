# Selection command bar fix + multi-select toggle + uniform animated checkboxes

> Rescued 2026-07-09 from the Claude Code session that hit its usage limit
> (cwd `C:\wavee\fluent-gpu`). Full research + user Q&A decisions were completed; implementation was about to start when the limit hit.
> Original plan files: `~/.claude-personal/plans/another-thing-this-floating-mossy-cocoa.md`

## Context

Three connected asks:

1. **Bug**: the floating selection command bar (`SelectionBar`, `app/Wavee/Features/Detail/DetailTracks.cs`) is layout-bugged — fixed-intrinsic-width flex row with no `MaxWidth`/`Shrink`, centered by the overlay, so on narrow panes it overflows and clips at both window edges. No responsive behavior exists.
2. **Feature**: add a **multi-select toggle** to the track-list toolbar that turns on row checkboxes.
3. **Feature**: **auto-show animated checkboxes** in track rows on every page that shows tracks, with identical behavior everywhere.

User decisions (confirmed via Q&A):

- Checkboxes appear when the toggle is ON **or** selection count ≥ 1 (WinUI/OneDrive behavior); slide out when both are off.
- Scope = all standard track lists: detail pages (playlist/album/liked/local), library embedded pane, search "Songs", artist "Popular", artist album drawer. **Queue / Next Up are OUT of scope** (bespoke drag-reorder lists).
- Bar overflow fix = **responsive collapse** (labels → icon-only → drop thumbs).
- Bar now appears at **1+ selected** (was 2+).

## Implementation steps

1. **Engine** — `SelectorVisualsBound.BoundCheckLane` + optional `showCheckbox` on `AccentPill`
2. **Icon + loc** — `Icons.MultiSelect`, `detail.select`, `detail.clearSelection`
3. **State** — `DetailHandlers.MultiSelect` / `SetMultiSelect`; ephemeral per-`DetailShell` signal
4. **Shared bar** — `app/Wavee/Components/SelectionCommandBar.cs` with responsive collapse
5. **Detail TrackList** — toggle, `UseComputed` visibility, checkbox lane, header inset
6. **Search Songs** — Extended selection + bar
7. **Artist Popular** — Extended selection + bar
8. **Album drawer** — Extended selection + bar
9. **Library pane** — inherits TrackList (no toolbar toggle; count≥1 trigger only)

See the full step-by-step spec in the agent plan file for edge cases and verification checklist.
