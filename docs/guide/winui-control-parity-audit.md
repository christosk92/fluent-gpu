# WinUI control parity audit

This is the source-backed audit for `src/FluentGpu.Controls` against the local WinUI tree at
`C:\WAVEE\microsoft-ui-xaml`.

The important correction: a WinUI control is not defined only by its XAML template. For every control below, parity
must be checked against three source layers:

1. Styling and visual states: `controls/dev/CommonStyles/*_themeresources*.xaml` or `controls/dev/<Control>/*.xaml`.
2. Behavior: `controls/dev/<Control>/*.cpp`, `*.h`, and `*.idl`.
3. Generated source: `controls/dev/Generated/*.properties.cpp` and `*.properties.h`, especially dependency
   properties, property-changed callbacks, events, and `TemplateSettings`.

Generated files matter because many XAML storyboards do not hard-code their start/end geometry. They bind to
generated `TemplateSettings` values that are computed in C++ at runtime. `CommandBarFlyoutCommandBarTemplateSettings`
is the clearest example: its generated properties include `OpenAnimationStartPosition`, `OpenAnimationEndPosition`,
`ContentClipRect`, `OverflowContentClipRect`, `WidthExpansionDelta`, and expand-up/down animation positions.

## Current engine surface

FluentGpu now has one real interaction animation path:

- `BoxEl` supports `HoverFill`, `PressedFill`, `HoverBorderColor`, `PressedBorderColor`, `HoverOpacity`,
  `PressedOpacity`, `HoverScale`, `PressScale`, per-node hover/press duration, and easing.
- `PolylineStrokeEl` supports transform/opacity plus hover/press scale, and the animation system supports stroke trim
  keyframes.
- `TextEl` has static `Color` and reactive `ColorBind`, but no built-in hover/pressed/disabled/focused text-state
  ramps.
- `BoxEl` has handlers for click, key, char input, pointer down, drag, focusability, repeat, hit testing, and a basic
  role field.
- The renderer supports solid rounded boxes, gradient fills, gradient borders, acrylic, shadows, scrollbars, focus
  rings, and analytic polyline strokes.

That means the correct policy is:

- Use the engine interaction animator for ordinary hover/press visual state.
- Use authored keyframes for real WinUI timelines: stroke trim, open/close, reveal, layout projection, clip, and
  animated icon segments.
- Do not add separate per-control animation systems. If a WinUI behavior cannot be represented, add the missing engine
  primitive once.

## Cross-cutting gaps

These are engine-level or platform-level gaps seen repeatedly in the control audit.

| Gap | Impact | Required engine direction |
|---|---|---|
| Stateful text/glyph brush ramps | Many WinUI controls dim or recolor text/icons on hover, press, disabled, checked, selected, or focus. FluentGpu usually sets resting text color only. | Add `TextEl` interaction ramps: hover/pressed/disabled/focused color and opacity, using inherited interaction progress from the nearest interactive ancestor. |
| Disabled input gate | Several controls set disabled colors but still rely on every factory to remove handlers manually. | Add `IsEnabled` or disabled semantics to interactive scene nodes so hit-test, focus, keyboard, pointer, repeat, and automation all gate consistently. |
| Focus and keyboard model | WinUI C++ has roving focus, arrow navigation, `SelectionFollowsFocus`, Enter/Space activation, escape close, tab stops, and focus-restoration behavior. | Add a small focus/navigation service: directional movement, roving-tabindex helpers, key-to-command helpers, and focus-restoration for popups. |
| `TemplateSettings` equivalent | CommandBar, CommandBarFlyout, NavigationView, TreeView, TabView, Progress*, TeachingTip, PersonPicture, InfoBar, Expander, and PipsPager rely on computed settings. | Add a typed per-control computed-settings pattern, not a stringly VSM clone. Settings should be generated or declared once and bound into animations/layout. |
| Popup/flyout placement | WinUI open/close behavior includes light dismiss, placement, size-to-anchor, above/below flipping, joined corners, focus transfer, and nested flyouts. | Promote `FlyoutPositioner`/`OverlayHost` into a reusable popup service with placement result, clipping, focus trap/restoration, dismiss policy, and transition inputs. |
| Stateful gradient and brush transitions | Some WinUI borders/fills use elevation gradient brushes and brush transitions. FluentGpu can draw gradients but cannot state-ramp an entire gradient brush. | Add `HoverGradient`, `PressedGradient`, `HoverBorderBrush`, and `PressedBorderBrush` or a generic state-brush abstraction for gradient side tables. |
| Layout-transition primitives | WinUI often animates width, height, translation, clip rect, and opacity from computed template settings. | Extend authored timelines with clip-rect, width/height projection, and per-edge reveal without forcing full relayout per frame. |
| Rich text/editing | TextBox, PasswordBox, RichTextBlock, AutoSuggestBox, NumberBox, and date/time pickers rely on selection, caret, IME, validation, and formatting behavior. | Expand `EditableText` and text layout primitives before claiming full parity. |
| Advanced brushes/media | ColorPicker needs a true 2D spectrum brush; MediaPlayerElement needs real video composition; acrylic/shadow values are approximate. | Add 2D gradient/spectrum brush, media surface primitive, and WinUI-calibrated acrylic/elevation tokens. |
| Accessibility/automation | WinUI supplies automation peers, names, patterns, and events for most mux controls. FluentGpu currently exposes a small role field. | Add automation properties and patterns where controls have selection, range, expand/collapse, toggle, invoke, value, or scroll behavior. |

## Source map and per-control diff

Each row names the WinUI sources used for comparison. "Generated: none" means no matching generated mux property file
was found under `controls/dev/Generated`; framework controls may still have platform implementation outside this repo.

### Basic buttons and input state controls

| FluentGpu control | WinUI sources | Current FluentGpu | Diff |
|---|---|---|---|
| `AnimatedIcon.cs` | XAML: none. Source: `AnimatedIcon/AnimatedIcon.cpp`, `.h`, `.idl`; `AnimatedIcon/AnimatedVisuals/AnimatedAcceptVisualSource.cpp`, `.h`, `.idl`; also `AnimatedBackVisualSource.*`, `AnimatedChevronDownSmallVisualSource.*`, `AnimatedChevronRightDownSmallVisualSource.*`, `AnimatedChevronUpDownSmallVisualSource.*`, `AnimatedFindVisualSource.*`, `AnimatedGlobalNavigationButtonVisualSource.*`, `AnimatedSettingsVisualSource.*`. Generated: `AnimatedIcon.properties.*`, `AnimatedIconSource.properties.*`, `AnimatedAcceptVisualSource.properties.cpp`. | A static glyph wrapper with hover/press scale. CheckBox separately implements the accept stroke trim. | Missing WinUI animated-icon source contract, state-to-segment lookup, and the generated visual-source property model. Add a reusable animated-icon segment abstraction if more icons need 1:1 motion. |
| `Button.cs` | XAML: `CommonStyles/Button_themeresources.xaml`, `Button_themeresources_perf2026.xaml`; also `CommonStyles/Common_themeresources_any.xaml` for 83/167/250 ms timing. Source: framework-owned, not in `controls/dev/Button`. Generated: `ButtonInteraction.properties.*`. | Close visual surface: 32px height, padding, fill/border hover/press, elevation border, static text foreground. | Text foreground hover/press/disabled is only documented, not animated/stateful. Disabled is manual instead of engine gated. BrushTransition is 83ms in WinUI; use engine defaults only when they match that token. |
| `IconButton.cs` | Same WinUI evidence as `Button.cs`. Generated: `ButtonInteraction.properties.*`. | Icon-only button using Button-like surface tokens. | Same Button gaps plus no text/glyph state ramp. Disabled token is approximate. |
| `RepeatButton.cs` | XAML: `CommonStyles/RepeatButton_themeresources.xaml`, `_perf2026.xaml`. Source: framework-owned. Generated: none. | Button-like surface plus `BoxEl.Repeats`. | Repeat interval/delay should be checked against WinUI repeat behavior. Text/glyph state and disabled input should move to engine gate. |
| `HyperlinkButton.cs` | XAML: `CommonStyles/HyperlinkButton_themeresources.xaml`, `_perf2026.xaml`. Source/generated: none in mux tree. | Text-like clickable element with hover/press fill approximation. | WinUI is primarily foreground/underline/state text behavior. FluentGpu lacks stateful text foreground and hyperlink-specific focus/visited semantics. |
| `AppBarButton.cs` | XAML: `CommonStyles/AppBarButton_themeresources.xaml`, `_perf2026.xaml`. Source/generated: none. | Vertical 16px glyph plus 12px label, subtle hover/press fill. | WinUI press dims foreground via state setters; FluentGpu cannot state-ramp child text. Disabled uses manual color/handler choices because the engine lacks `IsEnabled`. |
| `AppBarToggleButton.cs` | XAML: `CommonStyles/AppBarToggleButton_themeresources.xaml`, `_perf2026.xaml`. Source/generated: none. | AppBar button with local checked state and accent checked surface. | Missing per-state foreground ramps for unchecked/checked hover/press/disabled. Toggle semantics are local-only; no command/radio group integration. |
| `AppBarSeparator.cs` | XAML: `CommonStyles/AppBarSeparator_themeresources.xaml`. Source/generated: none. | Thin divider. | Mostly visual. Check exact height/margin against template when auditing CommandBar composition. |
| `ToggleButton.cs` | XAML: `CommonStyles/ToggleButton_themeresources.xaml`, `_perf2026.xaml`. Source/generated: none. | Two/three-state filled button with hover/press fill. | Missing full `Unchecked/Checked/Indeterminate x Normal/PointerOver/Pressed/Disabled` foreground/border matrix because text/glyph state ramps and disabled gate are missing. |
| `ToggleSplitButton.cs` | XAML: no direct file in local tree; behavior source is under `SplitButton/ToggleSplitButton.cpp`, `.h`; generated `ToggleSplitButton.properties.*`, `ToggleSplitButtonAutomationPeer.properties.cpp`. | SplitButton variant with checked primary half and dropdown half. | Needs SplitButton C++ behavior parity: checked dependency property, toggle events, split invoke/open behavior, disabled/focus handling. Uses approximate on-accent tertiary stroke token. |
| `CheckBox.cs` | XAML: `CommonStyles/CheckBox_themeresources.xaml`, `_perf2026.xaml`; AnimatedIcon source from `AnimatedIcon/AnimatedVisuals/AnimatedAcceptVisualSource.*`; generated `AnimatedAcceptVisualSource.properties.cpp`. Source: no mux `CheckBox` C++ in this tree. | Good current state: WinUI-sized box, on/off/indeterminate state ramps, analytic drawn checkmark using `AnimatedAccept` segment timing, stroke trim, press scale. | Remaining gap is text/glyph foreground interaction: label is static, glyph pressed foreground dim is not eased by `TextEl`. Full disabled semantics need engine gate. |
| `RadioButton.cs` | XAML: `CommonStyles/RadioButton_themeresources.xaml`, `_perf2026.xaml`. Behavior source for the `RadioButtons` group: `RadioButtons/RadioButtons.cpp`, `.h`, `.idl`, `RadioButtonsElementFactory.*`, `ColumnMajorUniformToLargestGridLayout.*`. Generated: `RadioButtons.properties.*`, `RadioButtonsAutomationPeer.properties.cpp`. | Ring/dot visuals include hover/press fill and dot grow/shrink; unchecked pressed glyph exists as a separate part. | Single RadioButton visual is close, but group behavior is not WinUI `RadioButtons`: no generated property model, item factory, keyboard roving, column-major uniform layout, automation, or disabled/focus handling. Verify pressed glyph in live path after engine changes because this was recently fragile. |
| `ToggleSwitch.cs` | XAML: `CommonStyles/ToggleSwitch_themeresources.xaml`, `_perf2026.xaml`. Source/generated: none in mux tree. | Pill track, sliding knob via layout projection, hover/press knob scale, accent on state. | WinUI animates multiple knob/content opacity and size states at 83/250 ms. FluentGpu approximates text/content and state matrix; missing disabled gate, stateful text, off/on content crossfade, and drag behavior. |

### Text, edit, picker, dropdown, and flyout controls

| FluentGpu control | WinUI sources | Current FluentGpu | Diff |
|---|---|---|---|
| `EditableText.cs` | WinUI evidence shared by TextBox/PasswordBox/AutoSuggestBox/NumberBox. Source: framework text editing not fully represented in mux tree. Generated: none. | Internal single-line editing primitive with char/key support. | Needs selection ranges, caret blink/placement, IME composition, clipboard, undo, validation hooks, selection brush, disabled/read-only visual states, and richer keyboard handling before TextBox-family parity. |
| `TextBox.cs` | XAML: `CommonStyles/TextBox_themeresources.xaml`, `_perf2026.xaml`. Source/generated: none. | Header plus editable field surface using `EditableText`. | Visual shell is close, but behavior is partial: no full selection/IME/clipboard/read-only/placeholder/header state matrix. Focus visual exists generically but not all TextBox state setters. |
| `PasswordBox.cs` | XAML: `CommonStyles/PasswordBox_themeresources.xaml`, `_perf2026.xaml`. Source/generated: none. | TextBox-like field with reveal affordance approximation. | Missing real password reveal button states, secure editing behavior, selection/caret parity, and generated/framework PasswordBox behavior. |
| `AutoSuggestBox.cs` | XAML: `CommonStyles/AutoSuggestBox_themeresources.xaml`, `_perf2026.xaml`. Source: `AutoSuggestBox/AutoSuggestBoxHelper.cpp`, `.h`, `.idl`. Generated: `AutoSuggestBoxHelper.properties.*`. | EditableText with filtered popup, query button, up/down/enter behavior, open iff query has matches. | Missing WinUI dispatcher-timer debounce, full suggestion item event timing, popup corner-joining behavior from `AutoSuggestBoxHelper::UpdateCornerRadius`, focus restoration, automation, and richer text-editing behavior. |
| `NumberBox.cs` | XAML: `NumberBox/NumberBox.xaml`, `NumberBox_perf2026.xaml`, `NumberBox_themeresources.xaml`. Source: `NumberBox/NumberBox.cpp`, `.h`, `.idl`, `NumberBoxParser.cpp`, `.h`. Generated: `NumberBox.properties.*`, `NumberBoxAutomationPeer.properties.cpp`. | Numeric field with spin buttons and parsing approximation. | Missing source-backed parser parity, validation modes, coercion, min/max/small-large change behavior, mouse wheel/PageUp/PageDown/arrow stepping, expression parsing details, automation value/range patterns. Existing comment says arrows are blocked by `EditableText`. |
| `ComboBox.cs` | XAML: `CommonStyles/ComboBox_themeresources.xaml`, `_perf2026.xaml`. Source: `ComboBox/ComboBoxHelper.cpp`, `.h`, `.idl`. Generated: `ComboBoxHelper.properties.*`. | Button field plus popup list and selected item. | WinUI has popup open/close opacity/scale/translation storyboards, selected-item presenter states, editable/non-editable modes, keyboard search, focus restore, and helper-corner behavior. FluentGpu mostly approximates the shell. |
| `DropDownButton.cs` | XAML: `DropDownButton/DropDownButton.xaml`, `DropDownButton_perf2026.xaml`, `DropDownButton_themeresources.xaml`. Source: `DropDownButton/DropDownButton.cpp`, `.h`, `.idl`. Generated: `DropDownButton.properties.cpp`, `DropDownButtonAutomationPeer.properties.cpp`. | Button with chevron and flyout. | Needs source behavior parity for `Flyout` property, open/close events, keyboard opening, focus, automation invoke/expand-collapse, and exact flyout placement transitions. |
| `SplitButton.cs` | XAML: `SplitButton/SplitButton.xaml`, `SplitButton_themeresources.xaml`. Source: `SplitButton/SplitButton.cpp`, `.h`, `.idl`, `SplitButtonEventArgs.h`; generated `SplitButton.properties.*`, `SplitButtonAutomationPeer.properties.cpp`. | Joined primary/dropdown halves. | Needs separate primary/dropdown focus, hover/press matrices, split invocation events, open state, keyboard routing, automation, and joined-corner state. |
| `CalendarDatePicker.cs` | XAML: `CommonStyles/CalendarDatePicker_themeresources.xaml`, `_perf2026.xaml`. Source/generated: none in mux tree. | Button-like date field with calendar glyph. | Text padding is approximated because `TextEl` has no padding. Missing calendar flyout behavior, date validation/ranges, focus/keyboard, disabled state, and popup placement. |
| `DatePicker.cs` | XAML: `CommonStyles/DatePicker_themeresources.xaml`, `_perf2026.xaml`; also `DateTimePickerFlyout_themeresources*.xaml`. Source/generated: none. | Three fields/date selection approximation. | Missing WinUI picker flyout, looping selectors, touch-fling snapping, date range/calendar handling, localization, focus/keyboard, and full flyout transition. Existing comment notes touch-fling snapping is an engine seam. |
| `TimePicker.cs` | XAML: `CommonStyles/TimePicker_themeresources.xaml`, `_perf2026.xaml`; `DateTimePickerFlyout_themeresources*.xaml`. Source/generated: none. | Three fields for hour/minute/AMPM. | Missing looping flyout selectors, localization, 12/24-hour behavior, keyboard/focus, disabled states, and exact DateTimePicker flyout styling/motion. |
| `CalendarView.cs` | XAML: `CommonStyles/CalendarView_themeresources.xaml`, `_perf2026.xaml`. Source/generated: none. | Visual calendar grid with selected/today treatment. | WinUI has extensive view-change opacity/scale transitions, header button fade, scope changes, blackout/out-of-scope states, keyboard calendar navigation, selection modes, and automation. FluentGpu is a visual subset. |
| `ContentDialog.cs` | XAML: `CommonStyles/ContentDialog_themeresources.xaml`. Source/generated: none in mux tree. | Modal-like card/dialog composition. | Missing modal focus trap, default/cancel button semantics, close deferrals, placement/sizing policy, overlay transition, automation, and exact shadow/acrylic values. |
| `GenericFlyout.cs` | XAML: `CommonStyles/FlyoutPresenter_themeresources.xaml`, `MenuFlyout_themeresources*.xaml`, `DateTimePickerFlyout_themeresources*.xaml`. Source/generated: framework-owned. | Shared flyout surface. | Needs service-level popup placement, light dismiss, focus capture/restore, nested flyouts, placement constraints, above/below flip, joined corners, and open/close animation tokens. |
| `MenuFlyout.cs` | XAML: `CommonStyles/MenuFlyout_themeresources.xaml`, `_perf2026.xaml`. Source/generated: framework-owned. | Menu surface/items approximation. | Missing submenu behavior, keyboard mnemonics/arrow navigation, checked/radio menu item states, separators, automation menu patterns, and exact transition. |
| `CommandBarFlyout.cs` | XAML: `CommandBarFlyout/CommandBarFlyout.xaml`, `_themeresources.xaml`, `_perf2026.xaml`. Source: `CommandBarFlyout/CommandBarFlyout.cpp`, `.h`, `.idl`, `CommandBarFlyoutCommandBar.cpp`, `.h`, `.idl`, `CommandBarFlyoutCommandBarAutomationProperties.*`, `CommandBarFlyoutCommandBarTemplateSettings.*`, `TextCommandBarFlyout.*`. Generated: `CommandBarFlyout.properties.cpp`, `CommandBarFlyoutCommandBar.properties.*`, `CommandBarFlyoutCommandBarAutomationProperties.properties.*`, `CommandBarFlyoutCommandBarTemplateSettings.properties.*`. | Good structural approximation: primary commands row, overflow region, toggle command behavior, More button. | This is not 1:1 yet. WinUI relies on computed template settings for open, close, expand up/down, width expansion, overflow clip rects, and animation positions. Needs engine clip/position timeline support and computed settings. Also missing full keyboard/focus/menu semantics. |
| `CommandBar.cs` | XAML: `CommonStyles/CommandBar_themeresources.xaml`, `_perf2026.xaml`. Source/generated for flyout-like behavior overlaps `CommandBarFlyoutCommandBarTemplateSettings.properties.*`; framework CommandBar source is outside mux tree. | Chromeless closed bar, open acrylic/card approximation, AppBarButton children. | WinUI has compact/minimal/hidden open-up/open-down transitions bound to template settings and command bar overflow measurement. FluentGpu surface is visual-only and acrylic/border tokens are approximate. |
| `TeachingTip.cs` | XAML: `TeachingTip/TeachingTip.xaml`, `_themeresources.xaml`, `_perf2026.xaml`. Source: `TeachingTip/TeachingTip.cpp`, `.h`, `.idl`, `TeachingTipTemplateSettings.*`, `TeachingTipClosedEventArgs.*`, `TeachingTipClosingEventArgs.*`, `TeachingTipOpenedEventArgs.*`. Generated: `TeachingTip.properties.*`, `TeachingTipTemplateSettings.properties.*`, `TeachingTipAutomationPeer.properties.cpp`. | Trigger plus anchored teaching callout surface. | Missing placement algorithm, hero content/action buttons, open/close events and deferrals, light dismiss policy, focus behavior, automation, and template-setting-driven placement visuals. |
| `ToolTip.cs` | XAML: `CommonStyles/ToolTip_themeresources.xaml`. Source/generated: framework-owned. | Hover wrapper with simple bubble. | Missing delay/show duration, pointer/focus trigger rules, placement collision handling, dismissal, automation help text, and exact acrylic/shadow. |

### Navigation, selection, and collection controls

| FluentGpu control | WinUI sources | Current FluentGpu | Diff |
|---|---|---|---|
| `BreadcrumbBar.cs` | XAML: `Breadcrumb/BreadcrumbBar.xaml`, `BreadcrumbBar_perf2026.xaml`, `BreadcrumbBar_themeresources.xaml`. Source: `BreadcrumbBar.cpp`, `.h`, `.idl`, `BreadcrumbBarElementFactory.*`, `BreadcrumbBarItem.*`, `BreadcrumbBarItemClickedEventArgs.*`, `BreadcrumbIterable.*`, `BreadcrumbIterator.*`, `BreadcrumbLayout.*`. Generated: `BreadcrumbBar.properties.*`, `BreadcrumbBarItem.properties.cpp`, `BreadcrumbBarItemAutomationPeer.properties.cpp`. | Horizontal trail with chevrons and transparent buttons. | Missing ItemsRepeater-backed elision layout, ellipsis hidden-items menu, last/current item visual states, pointer/focus foreground states, keyboard focus movement, and item-clicked event semantics. Current code notes engine lacks per-state text color. |
| `NavigationView.cs` | XAML: `NavigationView/NavigationView.xaml`, `NavigationView_themeresources.xaml`, `NavigationBackButton.xaml`, `NavigationBackButton_perf2026.xaml`, `NavigationBackButton_themeresources.xaml`. Source: `NavigationView.cpp`, `.h`, `.idl`, `NavigationViewItem*`, `NavigationViewItemPresenter*`, `NavigationViewTemplateSettings.*`, `TopNavigationViewDataProvider.*`, `SplitDataSourceBase.h`, event args files. Generated: `NavigationView.properties.*`, `NavigationViewItem*.properties.*`, `NavigationViewItemPresenterTemplateSettings.properties.*`, `NavigationViewTemplateSettings.properties.*`. | Navigation shell/menu with selected item styling, hover/press fills, pane transition approximation. | Large behavior gap: display mode transitions, adaptive pane layout, top/left modes, settings item, back button, selection events, selection-follows-focus, item invoke semantics, keyboard navigation, generated template settings, automation, and nested item expansion. |
| `TreeView.cs` | XAML: `TreeView/TreeView.xaml`, `TreeViewItem.xaml`, `TreeView_themeresources.xaml`. Source: `TreeView.cpp`, `.h`, `.idl`, `TreeViewItem.*`, `TreeViewList.*`, `TreeViewNode.*`, `ViewModel.*`, drag/expand/collapse/selection event args. Generated: `TreeView.properties.*`, `TreeViewItem.properties.*`, `TreeViewItemTemplateSettings.properties.*`, `TreeViewNode.properties.*`, automation generated files. | Expand/collapse visual tree with selected-row indicator and hover/press fill. | Missing WinUI ViewModel selection engine, multi-select/partial-select propagation, drag/drop, item container recycling, keyboard navigation, automation, generated item template settings, and expand/collapse animations. |
| `TabView.cs` | XAML: `TabView/TabView.xaml`, `TabView_perf2026.xaml`, `TabView_themeresources.xaml`. Source: `TabView.cpp`, `.h`, `.idl`, `TabViewItem.*`, `TabViewItemTemplateSettings.h`, `TabViewListView.*`. Generated: `TabView.properties.*`, `TabViewItem.properties.*`, `TabViewItemTemplateSettings.properties.*`, `TabViewListView.properties.cpp`. | Local selected tab state, headers, close buttons, add button, divider. | Missing tab collection model, close requests/deferrals, drag reorder, overflow behavior, selected indicator animation/template settings, keyboard focus, text weight ramp, and automation. |
| `Pivot.cs` | XAML: `CommonStyles/Pivot_themeresources.xaml`, `_perf2026.xaml`. Source/generated: none in mux tree. | Pivot-like selection strip. | Missing header keyboard navigation, selected indicator animation, item container model, content transitions, focus states, and automation. |
| `SelectorBar.cs` | XAML: `SelectorBar/SelectorBar.xaml`, `SelectorBar_perf2026.xaml`, `SelectorBar_themeresources.xaml`. Source: `SelectorBar.cpp`, `.h`, `.idl`, `SelectorBarItem.*`, `SelectorBarSelectionChangedEventArgs.*`. Generated: `SelectorBar.properties.*`, `SelectorBarItem.properties.*`, `SelectorBarItemAutomationPeer.properties.cpp`. | Segmented row with selected pill rendered at final width. | WinUI selected pill animates scale/opacity and has item selection events/properties. FluentGpu lacks per-item scale-x state machine and generated selection model. |
| `PipsPager.cs` | XAML: `PipsPager/PipsPager.xaml`, `_themeresources.xaml`, `_perf2026.xaml`. Source: `PipsPager.cpp`, `.h`, `.idl`, `PipsPagerTemplateSettings.*`, `PipsPagerSelectedIndexChangedEventArgs.h`. Generated: `PipsPager.properties.*`, `PipsPagerTemplateSettings.properties.*`, `PipsPagerAutomationPeer.properties.cpp`. | Visual pips/pager approximation. | Missing generated property model, selected-index changed args, template settings, button enable/visibility rules, keyboard navigation, automation range/selection behavior, and exact selected pip animation. |
| `ListView.cs` | XAML: `CommonStyles/ListViewItem_themeresources.xaml`, `_perf2026.xaml`. Source: framework ListView source not in mux tree. Generated: none. | List row styling and selection approximation. | Missing item container generator, selection modes, keyboard/focus, virtualization, drag/drop, item invoked, grouped headers, automation, and full item state matrix. |
| `GridView.cs` | XAML: `CommonStyles/ListViewItem_themeresources.xaml`, `_perf2026.xaml`. Source/generated: framework GridView source not in mux tree. | Grid-style item composition. | Same ListView gaps plus layout/selection/keyboard behavior for grid navigation. |
| `ItemsView.cs` | XAML: `ItemsView/ItemsView.xaml`, `ItemsView_themeresources.xaml`. Source: `ItemsView.cpp`, `.h`, `.idl`, `ItemsViewInteractions.cpp`, `ExtendedSelector.*`, `SingleSelector.*`, `MultipleSelector.*`, `SelectorBase.*`, `NullSelector.*`, selection/item-invoked event args. Generated: `ItemsView.properties.*`, `ItemsViewAutomationPeer.properties.cpp`, `ItemsViewTestHooks.properties.cpp`. | Simplified items view. | Missing the source-backed selection engine, item invocation model, interaction policy, keyboard/focus, virtualization integration, and automation. |
| `Repeater.cs` | WinUI peer: `Repeater/ItemsRepeater.cpp`, `.h`, `.idl`, `ItemsRepeater.common.*`, `ElementManager.*`, `ViewManager.*`, `ViewportManager*`, `VirtualizationInfo.*`, `RecyclePool.*`, `Layout*`, `StackLayout.*`, `UniformGridLayout.*`, `FlowLayout.*`, `LinedFlowLayout.*`, `SelectionModel.*`, transition provider files. Generated: `ItemsRepeater.properties.*`, `ItemsRepeaterScrollHost.properties.cpp`. | Has small virtual list/grid/custom layout helpers and a non-alloc-oriented API. | It is a useful FluentGpu primitive, not WinUI ItemsRepeater parity. Missing recycle pool, element prepared/clearing/index-changed events, viewport manager, layout contexts, transitions, phased realization, selection model, and generated APIs. |
| `AnnotatedScrollBar.cs` | XAML: `AnnotatedScrollBar/AnnotatedScrollBar.xaml`, `_themeresources.xaml`, `_perf2026.xaml`. Source: `AnnotatedScrollBar.cpp`, `.h`, `.idl`, label/panning/scrolling event args. Generated: `AnnotatedScrollBar.properties.*`, `AnnotatedScrollBarLabel.properties.cpp`. | Static visual demo with labels and thumb. | Missing live scroll integration, panning info, detail label request event, scrolling event args, label generation, keyboard/automation, and full perf2026 visuals. |
| `ScrollBar.cs` | XAML: `CommonStyles/ScrollBar_themeresources.xaml`, `_perf2026.xaml`. Source/generated: framework-owned. | Draggable thumb with hover/press scale. | Needs exact expand/collapse behavior, track buttons if applicable, pointer capture/cancel, keyboard/page behavior, disabled gate, scroll viewer integration, and automation. |

### Progress, media, rating, color, and people controls

| FluentGpu control | WinUI sources | Current FluentGpu | Diff |
|---|---|---|---|
| `Slider.cs` | XAML: `CommonStyles/Slider_themeresources.xaml`, `_perf2026.xaml`. Generated: `SliderInteraction.properties.*`. Source: framework-owned. | Good visual approximation: 4px rail, value fill, thumb hover grow/press shrink, bind mode for hot values. | Missing full keyboard/range semantics, tick mark placement parity, disabled state, tooltip/value presenter behavior, vertical orientation, automation range pattern, and stateful text/glyph if labels are added. |
| `ProgressBar.cs` | XAML: `ProgressBar/ProgressBar.xaml`, `ProgressBar_themeresources.xaml`. Source: `ProgressBar.cpp`, `.h`, `.idl`, `ProgressBarTemplateSettings.*`. Generated: `ProgressBar.properties.*`, `ProgressBarTemplateSettings.properties.*`, `ProgressBarAutomationPeer.properties.cpp`. | Determinate/indeterminate visual approximation. | Missing template-settings-driven indeterminate animation parity, pause/error/show-paused/show-error state rules, range automation, and exact sizing/tokens. |
| `ProgressRing.cs` | XAML: `ProgressRing/ProgressRing.xaml`, `ProgressRing_themeresources.xaml`. Source: `ProgressRing.cpp`, `.h`, `.idl`, `ProgressRingTemplateSettings.*`. Generated: `ProgressRing.properties.*`, `ProgressRingTemplateSettings.properties.*`, `ProgressRingAutomationPeer.properties.cpp`. | Analytic arc ring. | Missing exact indeterminate ring storyboard, template settings, active/inactive behavior, size policy, automation, and WinUI easing/timing. |
| `RatingControl.cs` | XAML: `RatingControl/RatingControl.xaml`, `RatingControl_themeresources.xaml`. Source: `RatingControl.cpp`, `.h`, `.idl`, `RatingItemInfo.*`, `RatingItemFontInfo.*`, `RatingItemImageInfo.*`. Generated: `RatingControl.properties.*`, `RatingControlAutomationPeer.properties.cpp`. | Star rating visual/input approximation. | Missing placeholder/value/caption state model, pointer hover preview, keyboard/range behavior, custom image/font item info, disabled/read-only states, and automation. |
| `ColorPicker.cs` | XAML: `ColorPicker/ColorPicker.xaml`, `ColorPicker_themeresources.xaml`, `ColorSpectrum.xaml`. Source: `ColorPicker.cpp`, `.h`, `.idl`, `ColorSpectrum.cpp`, `.h`, `.idl`, `ColorPickerSlider.*`, `SpectrumBrush.*`, `ColorHelpers.*`, `ColorChangedEventArgs.*`. Generated: `ColorPicker.properties.*`, `ColorSpectrum.properties.*`, `ColorPickerSlider.properties.*`, automation generated files. | Approximate 2D spectrum using layered/segmented gradients, hue rail, alpha/preview. | Needs true 2D spectrum brush, exact HSV/RGB conversion behavior from `ColorHelpers`, slider/thumb states, color property model/events, input text fields if exposed, keyboard/automation. |
| `PersonPicture.cs` | XAML: `PersonPicture/PersonPicture.xaml`, `PersonPicture_themeresources.xaml`. Source: `PersonPicture.cpp`, `.h`, `.idl`, `InitialsGenerator.*`, `PersonPictureTemplateSettings.h`. Generated: `PersonPicture.properties.*`, `PersonPictureTemplateSettings.properties.*`, `PersonPictureAutomationPeer.properties.cpp`. | Avatar/initials approximation. | Missing exact initials generation, contact/display name property precedence, image loading/fallback events, badge states, template settings, and automation name behavior. |
| `InfoBadge.cs` | XAML: `InfoBadge/InfoBadge.xaml`, `InfoBadge_themeresources.xaml`. Source: `InfoBadge.cpp`, `.h`, `.idl`, `InfoBadgeTemplateSettings.*`. Generated: `InfoBadge.properties.*`, `InfoBadgeTemplateSettings.properties.*`. | Badge visual approximation. | Missing generated template settings for value/icon display, automation name/value behavior, exact size thresholds, and state resources. |
| `InfoBar.cs` | XAML: `InfoBar/InfoBar.xaml`, `InfoBar_themeresources.xaml`. Source: `InfoBar.cpp`, `.h`, `.idl`, `InfoBarPanel.*`, `InfoBarTemplateSettings.*`, opened/closing/closed event args. Generated: `InfoBar.properties.*`, `InfoBarPanel.properties.*`, `InfoBarTemplateSettings.properties.*`, event args generated files, automation generated file. | Severity surface with title/message/action/close approximation. | Missing open/close event lifecycle and deferrals, close button semantics, panel layout algorithm, icon/source behavior, severity template settings, automation, and exact typography weight because `TextEl` only has `Bold`. |
| `MediaPlayerElement.cs` | WinUI peer exists outside mux source map; no local `controls/dev/MediaPlayerElement` files found. | Static media-player chrome approximation. | Not parity. Needs a media/video surface primitive, transport controls behavior, buffering/error state, focus/keyboard, captions/fullscreen, and real media session integration. |

### Layout and primitive wrappers

| FluentGpu control | WinUI sources | Current FluentGpu | Diff |
|---|---|---|---|
| `BorderControl.cs` | WinUI peer: framework `Border`, no local mux source/generated files. | Thin wrapper over `BoxEl`. | Visual parity depends on `BoxEl` rounded-rect/background/border. Missing per-side border thickness/brush if needed. |
| `Canvas.cs` | WinUI peer: framework `Canvas`, no local mux source/generated files. | Absolute offset container. | Needs attached-property model (`Canvas.Left/Top/ZIndex`) only if public API wants WinUI shape. |
| `RelativePanel.cs` | WinUI peer: framework `RelativePanel`, no local mux source/generated files. | Simplified relative layout helper. | Not WinUI layout parity. Needs constraint solver/attached properties for true RelativePanel. |
| `VariableSizedWrapGrid.cs` | WinUI peer: framework `VariableSizedWrapGrid`, no local mux source/generated files. | Grid/span layout approximation. | Missing orientation, maximum rows/columns behavior, item attached span properties, measure policy, and focus behavior. |
| `Viewbox.cs` | WinUI peer: framework `Viewbox`, no local mux source/generated files. | Explicit scale or known natural-size scale. | Missing measure-override seam. True WinUI Viewbox computes scale from child desired size and available size during layout. |
| `Popup.cs` | WinUI peer: framework `Popup`/primitives, no direct mux source/generated files. | Overlay host primitive/wrapper. | Needs real popup service behavior: placement, light dismiss, focus transfer, z-order, clipping, input capture, and open/close event lifecycle. |
| `SplitView.cs` | XAML: `CommonStyles/SplitView_themeresources.xaml`, `_perf2026.xaml`. Source/generated: framework-owned. | Fixed side pane plus content row. | Missing display modes, overlay/compact behavior, pane open/close transitions, light dismiss, focus, adaptive template settings, and automation. |
| `FlipView.cs` | XAML: `CommonStyles/FlipView_themeresources.xaml`, `_perf2026.xaml`, `FlipViewItem_themeresources.xaml`. Source/generated: framework-owned. | Visual carousel approximation. | Missing item navigation buttons, touch/pointer manipulation, virtualization, keyboard, item container states, transition animations, and automation. |
| `TextBlockDemo.cs` | XAML: `CommonStyles/TextBlock_themeresources.xaml`, `TextBlock_themeresources_v2.5.xaml`. Source/generated: framework-owned. | Type-ramp helper methods. | Display-only. Missing inline runs, line height/trimming nuance, text selection, semantic styles, and exact font weights. |
| `RichTextBlock.cs` | WinUI peer: framework `RichTextBlock`, no local mux source/generated files. | Paragraphs as separate `TextEl` nodes. | Not RichTextBlock parity. Needs inline/run model, paragraphs, hyperlinks, selection, embedded UI, text layout metrics, and automation. |

### Mux controls with large behavior surfaces

| FluentGpu control | WinUI sources | Current FluentGpu | Diff |
|---|---|---|---|
| `Expander.cs` | XAML: `Expander/Expander.xaml`, `Expander_perf2026.xaml`, `Expander_themeresources.xaml`, `_perf2026.xaml`. Source: `Expander.cpp`, `.h`, `.idl`, `ExpanderTemplateSettings.*`. Generated: `Expander.properties.*`, `ExpanderTemplateSettings.properties.*`, `ExpanderAutomationPeer.properties.cpp`. | Expand/collapse panel approximation. | Needs expand direction, template settings, header button state matrix, chevron animation, expand/collapse event behavior, focus/keyboard, automation expand-collapse pattern, and clip/height transition parity. |
| `MenuBar.cs` | XAML: `MenuBar/MenuBar.xaml`, `MenuBarItem.xaml`, `MenuBar_themeresources.xaml`. Source: `MenuBar.cpp`, `.h`, `.idl`, `MenuBarItem.*`, `MenuBarItemFlyout.*`. Generated: `MenuBar.properties.*`, `MenuBarItem.properties.*`, `MenuBarItemFlyout.properties.cpp`, automation generated files. | Horizontal menu with flyout approximation. | Missing menu mode state machine, Alt/F10 activation, arrow-key traversal, open-on-hover between top items, submenu semantics, focus restore, automation menu patterns, and exact item visual states. |
| `SwipeControl.cs` | XAML: `SwipeControl/SwipeControl.xaml`, `SwipeControl_themeresources.xaml`. Source: `SwipeControl.cpp`, `.h`, `.idl`, `SwipeControlInteractionTrackerOwner.*`, `SwipeItem.*`, `SwipeItems.*`, `SwipeItemInvokedEventArgs.*`. Generated: `SwipeControl.properties.*`. | Static revealed trailing actions demo. | Missing gesture physics, interaction tracker, threshold/open modes, execute/close behavior, leading/trailing item collections, item invoked events, and automation. |
| `ItemsView.cs` | See collection section. | See collection section. | See collection section. |
| `TeachingTip.cs` | See flyout section. | See flyout section. | See flyout section. |

### Internal helpers and non-control infrastructure

| File | Source comparison | Diff |
|---|---|---|
| `ControlMotion.cs` | No direct WinUI file. It maps WinUI timing/state concepts into FluentGpu primitives. | Keep this as the shared vocabulary, but do not let it become a second animation engine. Missing capabilities should be added to `BoxEl`, `TextEl`, `AnimEngine`, or popup/focus services. |
| `FlyoutPositioner.cs` | Related to WinUI flyout/popup placement. | Should grow into the placement result provider for ComboBox, AutoSuggestBox, DropDownButton, MenuFlyout, CommandBarFlyout, TeachingTip, ToolTip, Calendar/Date/Time pickers. |
| `OverlayHost.cs` | Related to Popup/Flyout hosting. | Needs focus trapping/restoration, z-order, dismiss policy, screen/viewport collision handling, nested overlay stacking, and transition inputs. |
| `Navigation.cs` | FluentGpu app navigation helper, not a WinUI `NavigationView` source peer. | Keep separate from control parity. |
| `Virtual.cs` | FluentGpu virtualization helper, related to ItemsRepeater/ListView/ItemsView. | Useful primitive, but not WinUI ItemsRepeater parity until recycle pool, viewport manager, and item events exist. |
| `Icons.cs` | Glyph constants, not a WinUI control. | Needs periodic glyph validation against Segoe Fluent Icons if exact glyph names/codes matter. |

## Priority implementation plan after the audit

Do these in engine-first order. This avoids creating two or three animation/interaction systems.

1. Add a real disabled state to interactive nodes.
   - Surface: `BoxEl.IsEnabled` or equivalent.
   - Behavior: disabled nodes do not hit-test, focus, repeat, drag, click, or receive keyboard activation.
   - Rendering: disabled target can be selected by control state ramps without each control manually nulling handlers.

2. Add stateful text/glyph interaction channels.
   - Surface: `TextEl.HoverColor`, `PressedColor`, `DisabledColor`, optional `FocusedColor`, and matching opacity if needed.
   - Behavior: child text can inherit nearest clickable ancestor interaction progress, same as child box parts.
   - This unlocks AppBarButton, HyperlinkButton, BreadcrumbBar, TabView, Button, ToggleButton, CheckBox label/glyph,
     RadioButton label, menu items, and many selected/pressed foreground states.

3. Add computed settings for control templates.
   - Do not clone WinUI VisualStateManager.
   - Add typed settings objects or records for controls that need runtime geometry: CommandBarFlyout, NavigationView,
     Expander, TeachingTip, ProgressBar, ProgressRing, PersonPicture, InfoBar, TabView, TreeView.
   - Bind settings into layout, clip, transform, and opacity keyframes.

4. Expand authored animation channels.
   - Add clip-rect/reveal timeline support.
   - Add width/height projection without per-frame relayout where WinUI animates layout values visually.
   - Add stateful gradient/gradient-border transitions.

5. Promote popup/flyout infrastructure.
   - Shared placement result: anchor rect, placement side, available size, collision flip, corner-join info.
   - Shared behavior: light dismiss, escape close, focus transfer/restore, nested overlay stack, modal/non-modal policy.
   - Consumers: ComboBox, AutoSuggestBox, DropDownButton, SplitButton, MenuFlyout, CommandBarFlyout, TeachingTip,
     ToolTip, CalendarDatePicker, DatePicker, TimePicker.

6. Add focus/keyboard helpers.
   - Roving focus, arrow traversal, Home/End/Page keys, Enter/Space activation, Escape close.
   - Selection follows focus where WinUI source requires it.
   - Consumers: BreadcrumbBar, MenuBar, NavigationView, TreeView, TabView, List/Grid/ItemsView, RadioButtons,
     ComboBox, AutoSuggestBox, PipsPager.

7. Add specialized primitives only where the source proves they are required.
   - 2D spectrum brush for ColorPicker.
   - Media surface for MediaPlayerElement.
   - Measure override/layout solver seams for Viewbox, RelativePanel, and WinUI panel parity.
   - Automation properties/patterns for controls with generated automation peers.

## Acceptance rule for future control patches

Before marking any control 1:1, the PR must list:

- Exact WinUI XAML/template files read.
- Exact C++/H/IDL behavior files read, or `framework source not present in controls/dev`.
- Exact generated property/template-setting files read, or `Generated: none`.
- The mapped state matrix: logical state x interaction state x focus/disabled.
- The mapped motion source: `BrushTransition`, storyboard, `TemplateSettings`, animated icon segment, or no animation.
- A headless vertical-slice check for the changed behavior or animation.
- A live GPU screenshot or slow-motion proof for visual motion that the headless harness cannot prove.

If a state cannot be represented without one-off control code, stop and add the shared engine primitive first.
