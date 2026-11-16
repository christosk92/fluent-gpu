---
name: win11-design
description: Use when designing or reviewing the look and layout of a FluentGpu app so it reads as a real Windows 11 (WinUI 3) app and not a generic web dashboard — window shell (Mica, title bar, NavigationView content layer), page anatomy (header, padding, max width, section headers), surfaces (layer vs card vs SettingsCard), status (InfoBar, InfoBadge, progress), buttons and accent use, hover/press behaviour, typography, spacing, radii, icons, motion, empty states and drag-and-drop targets. Includes copyable FluentGpu recipes, the WinUI source values behind them, pitfalls, and a review checklist.
---

# Building Windows 11 apps on FluentGpu

The engine already draws WinUI-faithful controls. What makes an app look like "AI slop" is **composition**: wrapping
everything in big padded cards, coloured circles behind icons, glow stages, pills for status, two accent buttons, no
navigation shell, centred hero text. Windows 11 apps are quiet: Mica, one content layer, thin 4px-gapped setting rows,
Body Strong section headers sitting on the page, status in InfoBars, one accent action.

Sources: `reference/values.md` (exact values from `C:\WAVEE\microsoft-ui-xaml`, file:line), `reference/patterns.md`
(how WinUI Gallery, Dev Home, Files, Wino Mail and ambie compose pages, file:line), `reference/guidance.md` (Microsoft
Learn design guidance, URL per rule, and what is NOT documented). APIs: `python -I C:/WAVEE/fgpu/tools/fg.py api X`.

## 1. The model: three planes

| Plane | What sits there | FluentGpu |
|---|---|---|
| **Base (Mica)** | title bar, navigation pane, commands | `AppOptions { Mica = true, CustomFrame = true }`; `TitleBar.Create`; `NavigationView` pane. No `Fill` on these: Mica shows through |
| **Content layer** | the page | `NavigationView` draws it for you: `Tok.FillLayerDefault`, 1px `Tok.StrokeCardDefault` on top+left, `(Radii.Overlay,0,0,0)` corner. Without a NavigationView, one `BoxEl` with exactly that |
| **Cards on the layer** | rows of settings/items, tiles | `SettingsCard` / `SettingsExpander` (radius 4, `FillCardDefault`, 1px `StrokeCardDefault`, min height 68, padding 16) |

Darker = less important, lighter = more important, in both themes. Never put an opaque fill on the page; never stack a
card inside a card; never apply a backdrop (Mica/Acrylic) to an element. Acrylic is for flyouts, menus and an overlay
pane only.

## 2. Rules that separate a Windows 11 app from a dashboard

1. **Shell first.** 48px `TitleBar` (icon 16, title in Caption 12, pane toggle in the bar) over a left
   `NavigationView` whenever there are 2+ destinations (printers, accounts, folders, pages). Settings / "Add …" go in
   the pane **footer**. Top nav only for ≤5 equal sections.
2. **Page = header + column.** `Title` (28/36 semibold) at the top of the content, page padding `Spacing.PagePadWide`
   (36, 24, 36, 36), one column capped at ~1000–1064 and centred, scrolling. Narrow windows (<640): padding 16.
3. **Sections are text, not boxes.** Section header = `BodyStrong` on the page with margin (1, 30, 0, 6). Under it a
   column of `SettingsCard`s with **4px** gaps. No wrapper card around a section.
4. **Rows, not tiles.** Anything with a label + a control or value is a `SettingsCard`: 20px monochrome glyph on the
   left, header + one-line secondary description, control right-aligned. Clickable rows: `IsClickEnabled` +
   `ActionIcon` (chevron E974 to navigate, E8A7 to open externally). Groups: `SettingsExpander`.
5. **One accent button per view**, on the primary action, left-aligned on a page (right-aligned only in dialogs and
   wizard footers). Everything else is `Button.Standard` / `Button.Subtle` / `HyperlinkButton`. Never an accent icon tile.
6. **Accent is reserved** for the primary button, selection pills (3×16, radius 2), progress, focus and checked states.
   Selected rows get a neutral `FillSubtleSecondary`, never an accent fill. Use the user's accent (`Tok.Accent*`), not a
   brand palette.
7. **Status is inline.** App or device state → `InfoBar` at the top of the content (Error / Warning / Success /
   Informational, not closable while the condition holds, never flashing). Counts or attention on a nav item →
   `InfoBadge`. Non-blocking work → `ProgressBar` (one per collection, not per row); blocking → `ProgressRing` (≥20).
   A coloured dot or text next to a status word is fine; coloured pills and banners you draw yourself are not.
8. **Icons are plain glyphs.** Segoe Fluent Icons at 16 (rows, buttons), 20 (card header icons), 24/32/48 (empty
   states, device headers). Monochrome `TextPrimary`/`TextSecondary`; colour only when it carries data (ink colour,
   status). No filled circles or squircles behind icons.
9. **Typography is the ramp, nothing else.** `Caption` 12/16, `Body` 14/20, `BodyStrong`, `BodyLarge` 18/24,
   `Subtitle` 20/28, `Title` 28/36, `TitleLarge` 40/52 (hero only), `Display` 68/92 (almost never). Semibold for titles,
   never Bold (`Ui.Heading` is 28 **Bold**: don't use it for page titles). Sentence case everywhere. Left-aligned;
   centre only empty states and text under icons.
10. **Spacing is the 4px grid.** 4 between setting cards, 8 between buttons and between a control and its header, 12
    between content areas/cards in a grid, 16 inside surfaces, 24 page gutters / between header and content, 36 page
    side padding. `Spacing.XS/S/M/L/XXL/PageWide`. No 20/28/40 paddings around content.
11. **Corners:** 4 (`Radii.ControlAll`) for controls, setting cards, list rows, InfoBars; 8 (`Radii.OverlayAll`) for
    windows, flyouts, dialogs, the content layer, tiles. Nothing pill-shaped except indicators and badges.
12. **Shadows are elevation, not decoration.** Cards and rows on the layer have none (a 1px stroke instead). Flyouts,
    dialogs and tooltips get theirs from the controls. No glows, no coloured shadows.
13. **Hover and press change colour, never size.** WinUI swaps fills in 83ms (`BrushTransitionMs = 83`): standard
    `FillControlDefault → Secondary → Tertiary`, accent `AccentDefault → AccentSecondary (α .9) → AccentTertiary (α .8)`
    with the label going to `TextOnAccentSecondary` on press. Pressed is *quieter*. The accent hover is subtle **by
    design**; do not "fix" it with custom colours or scale. Your own clickable rows: `.Interactive(Interaction.ListRow)`
    (subtle fill ramp), never `HoverScale`.
14. **Motion is short and built in:** 83/167/250ms, decelerate `(0,0,0,1)` in. Page changes use the NavigationView's
    own transition. No bounces, no long fades, no looping decoration.
15. **Words:** sentence case, no period on labels and buttons, periods on full sentences, "you", short verbs
    ("Print files", "Remove", "Search again"). Errors say what happened and what to do.

## 3. Which surface or control?

| You want to show | Use | Not |
|---|---|---|
| A setting, a value, an option with a control | `SettingsCard` (control in `Content`, right) | a `Ui.Card` with a label and a control |
| Several related settings / read-only properties | `SettingsExpander` with `SettingsExpander.Item(...)` rows | a big card full of key: value lines |
| A list of things the user acts on (jobs, files, devices) | `SettingsCard` per item (icon, header, description, trailing actions) or `ItemsView` rows | custom tiles with coloured icon squares |
| Something navigable (a printer, an account) | a `NavItem` in the pane (`IconContent` for a picture) | a selectable card list inside the page |
| Device / object identity | page header: picture 64–96 + `Title` + secondary status line + actions | a "hero stage" with gradient glow |
| A problem or state change | `InfoBar` under the header | a red pill, a toast, a coloured card |
| Empty list | one `SettingsCard` saying so, or centred 32–48 glyph + `BodyStrong` + secondary `Body` | big illustration, accent circle |
| Drop target | `DropZone.Window` over the content (feedback only while dragging) + a "Choose files" button | a permanent dashed box with a big icon |
| Visual hero (home page of a content app) | full-bleed image fading into the page, `TitleLarge` at 36,48 | on utility/tool pages: never |
| Many items as pictures | `ItemsView` grid, tiles 4–8 radius, `FillControlDefault` + stroke, 12 gaps | cards with shadows |

## 4. Recipes (FluentGpu)

### Shell: title bar + navigation pane + content layer
```csharp
// AppOptions { Mica = true, CustomFrame = true }; root wrapped in OverlayHost.Create(...)
var paneToggle = UseSignal(0);
return new BoxEl { Direction = 1, Grow = 1f, Children = [
    TitleBar.Create(new TitleBarOptions {
        Title = "PrintDesk", IconGlyph = "\uE749",
        ShowPaneToggle = true, OnPaneToggle = () => paneToggle.Update(n => n + 1) }),
    new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinHeight = 0f, Children = [
        NavigationView.Create(new NavigationViewOptions {
            Items = [new NavItem("printers", "", "Printers", IsHeader: true), .. itemsPerObject],
            Footer = [new NavItem("add", "\uE710", "Add a printer")],   // Settings / Add / About live here
            Initial = selectedKey,
            OnSelect = key => ...,
            Content = key => Embed.Comp(new PageProps(key), () => new ObjectPage()),
            ShowPaneToggle = false,            // the title bar owns it (WinUI Gallery)
            PaneToggleRequest = paneToggle,
        }) with { Key = keyOfTheItemList },    // items freeze at mount: re-key when the list (or a label/badge) changes
    ]},
]};
```
The NavigationView supplies the content layer (fill, top/left stroke, 8px top-left corner, square in Minimal). Do not
draw another plate behind your page.

### Standard page
```csharp
static Element Page(params Element[] content) => ScrollView(new BoxEl {
    Direction = 0, Justify = FlexJustify.Center, Padding = Spacing.PagePadWide,     // 36, 24, 36, 36
    Children = [ new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, MaxWidth = 1064f, Children = content } ],
}) with { Grow = 1f };

static Element Section(string text, Element? trailing = null) => new BoxEl {   // BodyStrong, 1,30,0,6
    Direction = 0, AlignItems = FlexAlign.End, Margin = new Edges4(1f, 30f, 0f, 6f),
    Children = trailing is null ? [BodyStrong(text)] : [BodyStrong(text), Spacer(), trailing] };

static Element Cards(IEnumerable<Element> cards) => new BoxEl { Direction = 1, Gap = Spacing.XS, Children = [.. cards] };
```
`MaxWidth` on a `Grow` child of a centred row keeps the column centred; `AlignSelf = Center` would collapse it.

### Object (device / account) page, after Windows Settings › Printers & scanners › (printer)
```csharp
Page(
  // header: picture, Title, status line, actions (one accent)
  new BoxEl { Direction = 0, Gap = Spacing.XL, AlignItems = FlexAlign.Center, Children = [
      Image(path, 96f, 96f, placeholder: ColorF.FromRgba(0,0,0,0)) with { Fit = ImageFit.Contain },
      new BoxEl { Direction = 1, Grow = 1f, Basis = 0f, MinWidth = 0f, Gap = Spacing.XXS, Children = [
          Title(name) with { Wrap = TextWrap.Wrap },
          new BoxEl { Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center,
              Children = [statusDot8, Body("Ready  ·  192.168.1.20").Secondary()] },
          new BoxEl { Direction = 0, Gap = Spacing.S, Margin = new Edges4(0f, Spacing.M, 0f, 0f), Children = [
              Button.Create("Print files", Print, ButtonAppearance.Accent, glyph: "\uE8B7"),
              Button.Create("Printer website", Open, glyph: "\uE8A7") ] } ] } ] },
  // problems: InfoBars, 24 below the header, 8 apart, not closable while true
  new BoxEl { Direction = 1, Gap = Spacing.S, Margin = new Edges4(0f, Spacing.XXL, 0f, 0f), Children = [
      InfoBar.Create(InfoBarSeverity.Error, "Paper jam", "Printing waits until this is fixed.", isClosable: false) ] },
  Section("Print settings"),
  Cards([
      SettingsCard.Create(new SettingsCard.Options { Header = "Copies", HeaderIcon = "\uE8C8",
          Content = NumberBox.Create(copies, null, new NumberBox.NumberBoxOptions { Initial = copies.Peek(), Minimum = 1,
              SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline }) }),
      SettingsCard.Create(new SettingsCard.Options { Header = "Color", HeaderIcon = "\uE790",
          Content = ComboBox.Create(["Color", "Black and white"], colorIndex, width: 180f) }) ]),
  Section("Print queue", HyperlinkButton.Create("Clear finished", Clear)),
  Cards(jobs.Select(JobRow)),
  Section("About this printer"),
  Cards([ SettingsExpander.Create(new SettingsExpander.Options { Header = "Printer properties", HeaderIcon = "\uE946",
              Items = [SettingsExpander.Item("Address", null, Body(host).Secondary()), ...] }),
          SettingsCard.Create(new SettingsCard.Options { Header = "Printer website", HeaderIcon = "\uE774",
              IsClickEnabled = true, OnClick = Open, ActionIcon = "\uE8A7" }),
          SettingsCard.Create(new SettingsCard.Options { Header = "Remove this printer", HeaderIcon = "\uE74D",
              Content = Button.Standard("Remove", Remove) }) ]));
```

### Item row with live state (queue job, download, device)
`SettingsCard` with `HeaderIconElement = Icon(glyph, 20f, colorOnlyIfStatus)`, `Description = state sentence`,
`Content` = a `ProgressRing.Create(null, 20f)` while working + a cancel `IconButton` wrapped in `ToolTip.Wrap(…, "Cancel")`.
Empty: a single SettingsCard "Nothing is printing" + one-sentence description. Never a 140px empty box.

### Settings page
Standard page; `Title("Settings")`; sections (Appearance, …, About) of SettingsCards; theme as a ComboBox
"Light / Dark / Use system setting"; changes apply immediately (no Save); About is a SettingsExpander at the bottom
(app name, version as secondary text), last element bottom margin 36.

### Add / connect page
`Title`, a secondary `Body` sentence explaining (margin top 8), then SettingsCards: "Search your network" (button,
`ProgressRing` 20 while searching), "Add by address" (TextBox + Standard button). Errors as a closable `InfoBar`.

## 5. Pitfalls in FluentGpu (verified)

- **A component in `NavItem.InfoBadge` blanks the whole navigation pane** (labels, icons and footer vanish; only the
  selection pill draws). Pass a plain element (`InfoBadge.Dot(InfoBadgeSeverity.Critical)`) or null, and fold the badge
  state into the NavigationView's key. A component in `NavItem.IconContent` works (use it for live pictures).
- **NavigationView items freeze at mount.** Re-key it on whatever the items show (keys, labels, badge state).
- **`NumberBox` text seeds from `NumberBoxOptions.Initial`** even when you pass a value signal: set `Initial = sig.Peek()`
  or the field shows empty.
- **SettingsCard content shorter than 32px** (a caption + a 4px meter) leaves the header off-centre: give the content
  `Height = 32f`. Content wider than ~150 needs `Alignment = Vertical` or it paints over the header.
- **`Ui.Heading` is 28 Bold** and the gallery's `GalleryScaffold.Page` uses 28 padding all round: neither is the
  Windows 11 page rhythm. Use `Title` and `Spacing.PagePadWide`.
- **`Ui.Card` is an 8-radius, 16-padding container** for content blocks, not a settings row; it has no shadow.
- **TitleBar renders 48 tall** (`ExpandedHeight`); pass `Height = TitleBar.CompactHeight` only for a 32 bar with
  nothing but a title.
- **Hover can't be checked in a `FluentAppHarness` screenshot by queueing pointer input**: the host follows the real
  cursor, so injected moves show no hover. Verify hover/press by eye in a live window (and note the snipping overlay
  steals hover before a screenshot is taken).
- `DropZone` content is mount-once; put live text in bound `TextEl`s or components inside it.

## 6. Review checklist (run on every screen before calling it done)

- [ ] Mica shows through title bar and pane; exactly one content layer; no opaque page fill; no card inside a card.
- [ ] Page starts with a `Title` (or device header) at 36/24 padding; column capped ~1064; sections are BodyStrong text.
- [ ] Rows are SettingsCards 4 apart; no big padded wrapper cards; nothing floats in a 140px empty box.
- [ ] At most one accent button visible; accent elsewhere only for selection/progress/focus.
- [ ] Status shown with InfoBar / InfoBadge / ProgressBar / ProgressRing, not custom pills or banners.
- [ ] Icons: plain Segoe Fluent glyphs at 16/20/24/32/48, no coloured circles or tiles behind them.
- [ ] Only ramp styles; no Bold; sentence case; no text under 12.
- [ ] Every gap/padding on the 4px grid and from `Spacing.*`; radii 4 or 8 only.
- [ ] Hover/press change fill only (83ms); no scale on press; custom clickables use `.Interactive(Interaction.ListRow)`.
- [ ] Rendered in light AND dark, at the window's min width and a wide width; screenshots looked at.
