# Microsoft Learn: Windows 11 design guidance

Read Nov 2026. "NOT FOUND" means the docs don't say it: treat those as conventions (from WinUI Gallery or templates), not rules.

## Layout
- 4 epx grid: sizes, margins and positions in multiples of 4 (text exempt). Breakpoints: small ≤640, medium 641–1007,
  large ≥1008. — learn.microsoft.com/windows/apps/design/layout/screen-sizes-and-breakpoints-for-responsive-design
- Spacing ramp: 8 between buttons, button and flyout, control and its header; 12 between a control and its label and
  between content areas/cards; 16 from a surface edge to its text; Title/Subtitle/Body stacked 12 apart; Expander child
  controls right-aligned 16 from the chevron, children indented 48. — …/design/basics/content-basics
- Gutters 12 below 640, 24 above. — …/develop/ui/alignment-margin-padding. NavigationView content margin 12 (Minimal) /
  24. — …/develop/ui/controls/navigationview
- Settings page: single column, scrolling, max width ~1000–1100. Line length 50–60 characters.
- NOT FOUND: a "36px page margin" (Gallery convention).

## Materials and layering — …/design/signature-experiences/layering, …/design/style/mica, …/design/style/acrylic
- Base layer (menus, commands, navigation) on Mica; content layer (`LayerFillColorDefaultBrush`) as one surface or cards.
- Mica: long-lived windows, extend into the title bar, set transparent backgrounds where it should show, never apply a
  backdrop twice or to elements. Mica Alt for tabbed title bars (base → `LayerOnMicaBaseAlt` → layer).
- Acrylic only on transient light-dismiss surfaces (flyouts, menus) and overlay panes; never big backgrounds, never
  edge-to-edge panes, no accent text on acrylic.
- Elevation/stroke: window 128, dialog 128, flyout 32, tooltip 16, card 8, control 2 (pressed 1), layer 1; all 1px
  strokes. Shadows purposeful, not decorative. Darker = less important in both themes.

## Typography — …/design/signature-experiences/typography
Segoe UI Variable; Caption 12/16, Body 14/20, Body Strong 14/20 semibold, Body Large 18/24, Subtitle 20/28, Title 28/36,
Title Large 40/52, Display 68/92. Regular for text, Semibold for titles; no Bold/Italic in the ramp; minimum 12 regular /
14 semibold; sentence case everywhere; left-aligned; ellipsis on overflow.

## Color — …/design/signature-experiences/color, …/develop/ui/controls/infobar, …/design/accessibility/high-contrast-themes
Accent sparingly (important elements, interactive state); respect the user's accent and theme. Never red-vs-green alone;
status always with text. InfoBar severities: Error (problem), Warning (may cause a problem), Success (long task done),
Informational. Don't hard-code colours; high contrast uses system colour pairs and 2px borders on flyouts and dialogs.

## Geometry — …/design/signature-experiences/geometry
8 for windows, flyouts, dialogs, menus, teaching tips; 4 for in-page controls, list backplates, bars, tooltips; 0 where
straight edges meet and for snapped or maximised windows.

## Icons — …/design/style/segoe-fluent-icons-font, …/design/basics/titlebar-design
Segoe Fluent Icons at 16, 20, 24, 32, 40, 48, 64; not inline with text. Title bar icon 16. Multi-line list items 32.
Regular icons for actions and wayfinding, filled for selected states (fluent2.microsoft.design/iconography).

## Motion — …/design/signature-experiences/motion, …/design/motion/timing-and-easing, …/design/motion/page-transitions
83 (fade), 167, 250, 333ms; entrance and point-to-point (0,0,0,1) / (0.55,0.55,0,1); exits (1,0,1,1). Page refresh
(slide up + fade) for top-level, drill-in for deeper, horizontal slide for siblings. Prefer built-in transitions.
NOT FOUND as text: "hover is a colour change, not a scale" (it is what every control template does).

## Structure — …/design/basics/titlebar-design, …/develop/ui/controls/navigationview, …/design/basics/commanding-basics, …/design/app-settings/guidelines-for-app-settings
Title bar 32 (48 with search or PersonPicture), Mica, icon 16 at 16 from the edge, Caption title, draggable empty
space. NavigationView left for 5–10 items, top for ≤5; Settings pinned at the bottom; Auto adapts at 641/1008.
Commands near the objects they act on; CommandBar for the rest; confirm only irreversible actions. Settings: SettingsCard
and SettingsExpander (nest one level), ToggleSwitch for on/off, RadioButtons ≤5 options, apply immediately, theme
"Light / Dark / Use system setting", About at the bottom.

## Controls — …/develop/ui/controls/buttons, …/infobar, …/progress-controls, …/develop/data/drag-and-drop, …/lists
Buttons: 1–2 visible, verb labels, min 120 wide, single button left-aligned on a page and right-aligned in dialogs.
InfoBar: inline, persistent, takes layout space, for app state (not for confirmations). ProgressBar non-blocking (one
per collection), ProgressRing blocking (≥20). Drag and drop: show the operation, targets need a background, say when a
type isn't supported. ListView for text-heavy items, GridView for image-heavy.

## Writing — …/design/style/writing-style
Warm, crisp; lead with what matters; "you"; no period on labels and buttons; capitalise only the first word and proper
nouns; errors don't blame and offer a fix. NOT FOUND: a ban on exclamation marks.
