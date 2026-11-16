# WinUI 3 values behind the rules

From `C:\WAVEE\microsoft-ui-xaml` (`controls\dev\…`, written `dev/`). **CA** = `dev/CommonStyles/Common_themeresources_any.xaml`
(Dark dictionary lines 4–207, Light 208–414). FluentGpu token on the right: read it in `Render()`, never cache it.

## Surfaces

| WinUI resource | Dark | Light | Source | FluentGpu |
|---|---|---|---|---|
| SolidBackgroundFillColorBase | #202020 | #F3F3F3 | CA:68/272 | `Tok.FillSolidBase` |
| SolidBackgroundFillColorBaseAlt | #0A0A0A | #DADADA | CA:75/279 | `Tok.FillSolidBaseAlt` |
| LayerFillColorDefault (content layer) | #4C3A3A3A | #80FFFFFF | CA:60/264 | `Tok.FillLayerDefault` |
| LayerFillColorAlt | #0DFFFFFF | #FFFFFF | CA:61/265 | `Tok.FillLayerAlt` |
| LayerOnMicaBaseAltFillColorDefault | #733A3A3A | #B3FFFFFF | CA:64/268 | `Tok.LayerOnMicaBaseAlt` |
| CardBackgroundFillColorDefault | #0DFFFFFF | #B3FFFFFF | CA:56/260 | `Tok.FillCardDefault` |
| CardBackgroundFillColorSecondary | #08FFFFFF | #80F6F6F6 | CA:57/261 | `Tok.FillCardSecondary` |
| CardStrokeColorDefault | #19000000 | #0F000000 | CA:46/250 | `Tok.StrokeCardDefault` |
| SurfaceStrokeColorDefault | #66757575 | #66757575 | CA:50/254 | `Tok.StrokeSurfaceDefault` |
| DividerStrokeColorDefault | #15FFFFFF | #0F000000 | CA:53/257 | `Tok.StrokeDividerDefault` |
| SubtleFillColor Transparent / Secondary / Tertiary | 0 / #0FFFFFFF / #0AFFFFFF | 0 / #09000000 / #06000000 | CA:25-27/229-231 | `Tok.FillSubtle*` |
| ControlFillColor Default / Secondary / Tertiary / Disabled | #0FFFFFFF / #15FFFFFF / #08FFFFFF / #0BFFFFFF | #B3FFFFFF / #80F9F9F9 / #4DF9F9F9 / #4DF9F9F9 | CA:15-19/219-223 | `Tok.FillControl*` |
| ControlStrokeColor Default / Secondary | #12FFFFFF / #18FFFFFF | #0F000000 / #29000000 | CA:39-40/243-244 | `Tok.StrokeControl*` |
| ControlElevationBorderBrush | gradient 0→3px: StrokeSecondary @.33 → StrokeDefault @1 (dark: band on top; light: flipped, band at bottom) | | CA:186-191 / 382-390 | `Tok.ControlElevationBorder` (`BoxEl.BorderBrush`) |
| TextFillColor Primary / Secondary / Tertiary / Disabled | #FFFFFF / #C5FFFFFF / #87FFFFFF / #5DFFFFFF | #E4000000 / #9E000000 / #72000000 / #5C000000 | CA:5-8/209-212 | `Tok.Text*`, `.Secondary()` … |
| TextOnAccentFillColor Primary / Secondary | #000000 / #80000000 | #FFFFFF / #B3FFFFFF | CA:12-13/216-217 | `Tok.TextOnAccent*` |
| AccentFillColor Default / Secondary / Tertiary | SystemAccentColorLight2, α 1 / .9 / .8 | SystemAccentColorDark1, α 1 / .9 / .8 | CA:125-127/329-331 | `Tok.AccentDefault/Secondary/Tertiary` |
| SystemFillColor Success / Caution / Critical | #6CCB5F / #FCE100 / #FF99A4 | #0F7B0F / #9D5D00 / #C42B1C | CA:76-78/280-282 | `Tok.SystemFill*` |
| …Background (InfoBar) | #393D1B / #433519 / #442726 | #DFF6DD / #FFF4CE / #FDE7E9 | CA:82-84/286-288 | `Tok.SystemFill*Background` |

## Buttons (`dev/CommonStyles/Button_themeresources.xaml`)

| State | Button: bg / border / fg | AccentButton: bg / border / fg |
|---|---|---|
| Normal | ControlFillDefault / ControlElevationBorder / TextPrimary (:30,38,34) | AccentFillDefault / AccentControlElevationBorder / TextOnAccentPrimary (:5,13,9) |
| PointerOver | ControlFillSecondary / ControlElevationBorder / TextPrimary | AccentFillSecondary / AccentControlElevationBorder / TextOnAccentPrimary |
| Pressed | ControlFillTertiary / ControlStrokeDefault (flat) / **TextSecondary** | AccentFillTertiary / transparent / **TextOnAccentSecondary** |
| Disabled | ControlFillDisabled / ControlStrokeDefault / TextDisabled | AccentFillDisabled / transparent / TextOnAccentDisabled |

Padding 11,5,11,6 (:152), border 1, radius 4, 14px, min height ≈32. `BrushTransition 0:0:0.083` on background (:174, 246).
No scale or translate on press. SubtleButton: Transparent → SubtleSecondary → SubtleTertiary, pressed fg TextSecondary.
FluentGpu: `Button.Create(..., ButtonAppearance.Accent|Standard|Subtle)`, `BrushTransitionMs = 83`, same ramps.

## Shell metrics

| Item | Value | Source |
|---|---|---|
| TitleBar height compact / expanded | 32 / 48 | dev/TitleBar/TitleBar_themeresources.xaml:77-78 |
| Title bar icon / title | 16×16, margin 0,0,16,0 / Caption 12, margin 0,0,8,0 | TBT:88-94 |
| Back / pane button | 40 wide, glyph 16 | TBT:79-80 |
| Inactive window | title bar opacity .5, text TextTertiary | TBT:86, 11 |
| NavigationView pane open / compact | 320 / 48 | NavigationView.idl:198/195 |
| Adaptive thresholds | Compact ≥641, Expanded ≥1008 | NavigationView.idl:176-179 |
| Expanded pane background | transparent (Mica) | NVT:6/72 |
| Content layer | LayerFillColorDefault, border CardStroke 1,1,0,0, radius 8,0,0,0 (0 in Top/Minimal) | NVT:8, 49, 234, 263-265 |
| Nav item | min height 36, margin 4,2, icon box 40, icon 16 | NVT:217, 228, 209, 219 |
| Nav item fills | rest transparent, hover SubtleSecondary, pressed SubtleTertiary; selected SubtleSecondary + pill | NVT:9-11, 17-19 |
| Selection pill | 3×16, radius 2, AccentFillDefault | NVT:220-222, 48 |
| NavigationView header | 28 semibold, margin 56,44,0,0 | NVT:239, 1087-1093 |

## Containers and status

| Item | Value | Source |
|---|---|---|
| ControlCornerRadius / OverlayCornerRadius | 4 / 8 | dev/CommonStyles/CornerRadius_themeresources.xaml:5-6 |
| Expander | min height 48, header padding 16,0,0,0, content padding 16, CardBackgroundDefault + CardStroke, radius 4 | dev/Expander/Expander_themeresources.xaml:5-87 |
| SettingsCard (Community Toolkit) | CardBackgroundDefault, CardStroke 1, radius 4, padding 16, min height 68, description 12, header icon ≤20 margin 2,0,20,0, content min width 120, wraps <476 | Windows\components\SettingsControls\…\SettingsCard.xaml:8-124 |
| ListViewItem | min height 40, padding 16,0,12,0, backplate inset 4,2, radius 4; fills Subtle ramp; pill 3×16 that shrinks 6 on press | dev/CommonStyles/ListViewItem_themeresources.xaml:13-241; dxaml ListViewBaseItemChrome.cpp:52-79 |
| ItemContainer selected | 3px AccentFillDefault border, not a fill | dev/ItemContainer/ItemContainer.xaml:117-122 |
| InfoBar | severity background (SystemFill*Background), 16px glyph on a SystemFill circle, CardStroke 1, min height 48, radius 4, title 14 semibold, message 14 | dev/InfoBar/InfoBar_themeresources.xaml:5-85 |
| ProgressBar | AccentFill, track ControlStrongStroke, min height 3, track 1 | dev/ProgressBar/ProgressBar_themeresources.xaml:6-32 |
| ProgressRing | default 32, min 16, stroke 4 | ProgressRing.xaml:9-13 |
| ContentDialog | padding 24, title margin 0,0,0,12, button spacing 8 | ContentDialog_themeresources.xaml:16-18 |
| Flyout | padding 16,15,16,17 | FlyoutPresenter_themeresources.xaml:20 |

## Typography (`dev/CommonStyles/TextBlock_themeresources.xaml`)

Caption 12 normal · Body 14 normal · BodyStrong 14 semibold · BodyLarge 18 · Subtitle 20 semibold · Title 28 semibold ·
TitleLarge 40 semibold · Display 68 semibold (TB:3-51). Line heights 16/20/24/28/36/52/92 come from the Fluent type
ramp (Learn), not this file. Font: Segoe UI Variable; icons: Segoe Fluent Icons (`generic.xaml:14, 20`).

## Motion

`ControlFasterAnimationDuration` 83ms, `ControlFast` 167ms, `ControlNormal` 250ms, `ControlFastOutSlowInKeySpline`
0,0,0,1 (CA:602-606). Nav pane open 120ms, spline 0.1,0.9 0.2,1.0 (NavigationView.xaml:113).
