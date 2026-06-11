export const meta = {
  name: 'winui-parity-sweep',
  description: 'Compare every FluentGpu control against WinUI 3 reference sources; verify findings; synthesize plan sections',
  phases: [
    { title: 'Compare', detail: 'one finder per control family + systemic audits' },
    { title: 'Verify', detail: 'adversarial re-check of every finding' },
    { title: 'Synthesize', detail: 'group confirmed findings into plan sections' },
  ],
}

const OURS = 'C:/WAVEE/fluent-gpu'
const WX = 'C:/WAVEE/microsoft-ui-xaml'

const PRIMER = `
== CONTEXT ==
We reimplemented WinUI 3 controls in a from-scratch GPU-rendered C# UI engine (repo ${OURS}). You compare ONE control family against the WinUI 3 reference source (repo ${WX}). READ-ONLY task — never edit/write files.

== OUR ENGINE PRIMER (read fast, then go) ==
- Controls = immutable Element records (BoxEl/TextEl/ImageEl/PolylineStrokeEl in src/FluentGpu.Dsl/Element.cs) built by static factories (e.g. Button.Create) or Component classes with hooks (Context.UseState/UseKeyframes/UseTransition/UseSpring). Controls live in src/FluentGpu.Controls/.
- Interaction props on BoxEl: OnClick, OnPointerDown, OnPointerPressed, OnPointerExit, OnHoverMove, OnDrag, OnKeyDown, OnFocusChanged, Focusable, IsEnabled. Engine fires OnClick on Enter/Space for focused nodes.
- State visuals: Fill/HoverFill/PressedFill, BorderColor/HoverBorderColor/PressedBorderColor, HoverOpacity/PressedOpacity, HoverScale/PressScale, BrushTransitionMs (83 = WinUI ControlFasterAnimationDuration). StateBrush(Rest,Hover,Pressed,Disabled) in src/FluentGpu.Controls/ControlMotion.cs.
- CURSOR: BoxEl.Cursor (CursorId). Engine DEFAULT gives clickables a Hand cursor unless Cursor=CursorId.Arrow is set explicitly. WinUI ground truth: arrow for nearly everything; hand ONLY for hyperlinks; IBeam over editable text. Flag every wrong cursor.
- Focus ring: engine-drawn 2px outer + 1px inner OUTSIDE bounds; controls set Focusable + FocusVisualMargin (negative = push outward).
- Theme tokens: Tok.* in src/FluentGpu.Dsl/Tokens.cs map 1:1 to WinUI resource names (Tok.FillControlDefault == ControlFillColorDefault, Tok.TextPrimary == TextFillColorPrimary, Tok.AccentDefault == AccentFillColorDefault...). Check the control uses the SAME token the WinUI template/themeresources uses per state.
- Motion: ControlMotion.cs + src/FluentGpu.Animation/Motion.cs (83/167/250ms tokens, springs like MotionSprings.NavPill). Keyframe anims via Component Context.UseKeyframes(AnimChannel.X, ...).
- Flyouts: OverlayHost.Open(anchor, content, FlyoutPlacement, PopupOptions) + FlyoutPositioner auto-flip; acrylic via Tok.AcrylicFlyout; OverlayCornerRadius 8; Elevation.Flyout shadow.
- Icons: src/FluentGpu.Controls/Icons.cs (Segoe Fluent Icons glyph consts), Ui.Icon(glyph,size,color).
- TemplateParts: controls export PartXxx consts, route through Parts.Apply().
- KNOWN INTENTIONAL DEVIATIONS — do NOT report these as gaps: (a) layout-size animations use eased reflow (SizeMode.Reflow) instead of WinUI snap+translate (timings still match WinUI); (b) springs may replace WinUI splines for indicator/pill motion where the control self-documents it. docs/guide/control-fidelity.md has conventions.
- Gallery/demo usage lives under src/FluentGpu.WindowsApp/ — optionally check how the control is instantiated there to spot integration bugs.

== WINUI REPO GUIDE ==
- Lifted controls: ${WX}/controls/dev/<Name>/ → <Name>.cpp/.h (behavior), <Name>.idl (API), <Name>.xaml (template), <Name>_themeresources.xaml (per-theme brushes/metrics).
- System controls: behavior ${WX}/dxaml/xcp/dxaml/lib/<Name>_Partial.cpp(.h); template structure in ${WX}/dxaml/xcp/dxaml/themes/generic.xaml (~24k lines — grep for the control/style name); WinUI3 styling in ${WX}/controls/dev/CommonStyles/<Name>_themeresources.xaml. IGNORE *_perf2026.xaml variants.
- Master color tokens: ${WX}/controls/dev/CommonStyles/Common_themeresources_any.xaml; animation constants there too (ControlNormalAnimationDuration 250ms / Fast 167 / Faster 83; ControlFastOutSlowInKeySpline 0,0,0,1).
- NEVER look in BuildOutput/ or packages/. If a given path is missing, Glob for it under controls/dev and dxaml/xcp/dxaml/lib instead of giving up.

== METHOD ==
Read OUR file(s) COMPLETELY. Read the WinUI behavior + template + themeresources sources for the same control. Compare on ALL of:
1. API surface (vs .idl / public properties): missing props/events/features that an app would feel. Skip DependencyProperty plumbing + automation peers.
2. Template/visual structure + exact metrics: sizes, paddings, margins, corner radii, border thicknesses, font sizes/weights, icon glyphs+sizes.
3. Brushes per visual state (rest/hover/pressed/disabled/selected/focused…): exact resource names and whether we use the matching Tok.* token.
4. Interaction behavior: pointer states, click semantics, keyboard map, focus behavior, light-dismiss, repeat/hold, drag, text editing semantics — whatever applies.
5. Motion: every storyboard/transition the WinUI template defines (durations, KeySplines/easings, animated properties) vs ours.
6. Cursor correctness (see primer).
7. Bugs in OUR code you notice while reading (wrong math, dead code, state leaks, off-by-N) — report even if not a parity item, kind=bug.
8. Where WinUI itself is weak and we can do BETTER (user explicitly invites this): kind=improvement, still cite the WinUI reference you are improving on.

EXACTNESS RULES (critical): every numeric/color/name claim must come from a file you actually read this session, cited as path:line. No memory, no assumption, no approximation. If you cannot pin evidence, set confidence=low. Before reporting something as MISSING in our code, grep our whole file + related files for it (it may live elsewhere, e.g. in the engine, OverlayHost, or ListView base).
Severity: high = visibly wrong / blocks faithful app UI; medium = noticeable delta; low = polish. Keep each finding tight (<= ~60 words per text field). Report ALL real findings — do not cap the count.
`

const FINDINGS = {
  type: 'object',
  required: ['control', 'findings'],
  properties: {
    control: { type: 'string' },
    findings: {
      type: 'array',
      items: {
        type: 'object',
        required: ['id', 'title', 'kind', 'severity', 'our', 'winui', 'fix', 'confidence', 'needsEngineChange'],
        properties: {
          id: { type: 'string', description: 'short unique id like nav-01' },
          title: { type: 'string' },
          kind: { enum: ['missing-api', 'missing-behavior', 'styling-delta', 'motion-delta', 'bug', 'engine-gap', 'improvement'] },
          severity: { enum: ['high', 'medium', 'low'] },
          our: { type: 'string', description: 'our evidence: path:line + short quote/value (or "absent — searched X")' },
          winui: { type: 'string', description: 'WinUI evidence: path:line + exact value/name' },
          fix: { type: 'string', description: 'exact change: target value/behavior' },
          confidence: { enum: ['high', 'medium', 'low'] },
          needsEngineChange: { type: 'boolean' },
        },
      },
    },
    intentionalDeviations: { type: 'array', items: { type: 'string' } },
    notes: { type: 'string' },
  },
}

const VERDICTS = {
  type: 'object',
  required: ['control', 'verdicts'],
  properties: {
    control: { type: 'string' },
    verdicts: {
      type: 'array',
      items: {
        type: 'object',
        required: ['id', 'verdict'],
        properties: {
          id: { type: 'string' },
          verdict: { enum: ['confirmed', 'corrected', 'refuted'] },
          note: { type: 'string', description: 'for corrected: the corrected fact with path:line; for refuted: why' },
        },
      },
    },
  },
}

function findPrompt(it) {
  return PRIMER + '\n== YOUR ASSIGNMENT ==\nControl family: ' + it.key + '\nOUR files (read fully): ' + it.ours + '\nWinUI reference sources: ' + it.winui + (it.focus ? '\nSpecific focus points:\n' + it.focus : '') + '\n\nReturn your findings via StructuredOutput.'
}

const ITEMS = [
  { key: 'button-family', ours: 'src/FluentGpu.Controls/Button.cs, RepeatButton.cs, ToggleButton.cs, HyperlinkButton.cs, IconButton.cs', winui: 'controls/dev/CommonStyles/{Button,RepeatButton,ToggleButton,HyperlinkButton}_themeresources.xaml; dxaml/xcp/dxaml/lib/{ButtonBase_Partial,Button_Partial,RepeatButton_Partial,ToggleButton_Partial,HyperLinkButton_Partial}.cpp', focus: '- ButtonBase semantics: ClickMode, capture/release, space-bar press visual, Enter vs Space timing.\n- RepeatButton Delay/Interval defaults.\n- ToggleButton three-state + checked brush ramp (accent) incl. checked+hover/pressed.\n- HyperlinkButton underline/foreground states + hand cursor (the ONE allowed hand).\n- IconButton has no WinUI counterpart — judge it against Button w/ icon content conventions.' },
  { key: 'split-dropdown', ours: 'src/FluentGpu.Controls/DropDownButton.cs, SplitButton.cs, ToggleSplitButton.cs', winui: 'controls/dev/DropDownButton/*; controls/dev/SplitButton/* (incl ToggleSplitButton.cpp + SplitButton.xaml + _themeresources)', focus: '- SplitButton divider rendering, primary/secondary hit zones, keyboard (F4/Alt+Down opens flyout; Enter/Space invokes primary), checked visual for ToggleSplitButton (only primary checked).\n- DropDownButton chevron glyph E70D size 8? verify exact, spacing, flyout open state visual.' },
  { key: 'checkbox-radio', ours: 'src/FluentGpu.Controls/CheckBox.cs, RadioButton.cs, RadioButtons.cs', winui: 'controls/dev/CommonStyles/{CheckBox,RadioButton}_themeresources.xaml; dxaml/xcp/dxaml/lib/{CheckBox_Partial,RadioButton_Partial}.cpp; controls/dev/RadioButtons/*', focus: '- CheckBox glyph anims (stroke draw), indeterminate visual, 3-state cycling order, exact box 20px metrics + brushes per state incl checked-pointerover.\n- RadioButton outer ring/inner dot sizes per state (ellipse sizes animate: rest/hover/pressed dot diameters), group semantics + arrow-key movement.\n- RadioButtons panel: ColumnMajor layout, keyboard navigation (arrow wrap, Home/End), SelectedIndex API.' },
  { key: 'toggleswitch', ours: 'src/FluentGpu.Controls/ToggleSwitch.cs', winui: 'controls/dev/CommonStyles/ToggleSwitch_themeresources.xaml; dxaml/xcp/dxaml/lib/ToggleSwitch_Partial.cpp', focus: '- Track 40x20? knob sizes per state (rest 12, hover 14, pressed stretch 17x14?) — verify exact from xaml; knob travel animation duration/easing; drag-to-toggle behavior with threshold; On/Off content labels; header.' },
  { key: 'slider', ours: 'src/FluentGpu.Controls/Slider.cs', winui: 'controls/dev/CommonStyles/Slider_themeresources.xaml; dxaml/xcp/dxaml/lib/Slider_Partial.cpp (+ RangeBase_Partial.cpp)', focus: '- Thumb outer 22? inner ellipse rest 12/hover 14/pressed 10 — verify exact; track 4px height; tick marks; SnapsTo/StepFrequency; keyboard (arrows/PgUp/PgDn/Home/End, SmallChange/LargeChange); ToolTip value display while dragging (IsThumbToolTipEnabled); vertical orientation; IntermediateValue while dragging.' },
  { key: 'rating', ours: 'src/FluentGpu.Controls/RatingControl.cs', winui: 'controls/dev/RatingControl/*', focus: '- Pointer-over scrub preview, half-star? (no — verify), Caption, MaxRating, IsClearEnabled (click same value clears? actually drag-off), PlaceholderValue, glyph sizes/spacing (16px, spacing 8?), keyboard arrows, the scale animation on pointerover per-star.' },
  { key: 'textbox', ours: 'src/FluentGpu.Controls/TextBox.cs, EditableText.cs', winui: 'controls/dev/CommonStyles/TextBox_themeresources.xaml + TextControlsCommon_themeresources.xaml; dxaml/xcp/dxaml/lib/TextBox_Partial.cpp (skim; huge)', focus: '- Chrome: underline accent on focus (bottom border 1px rest → 2px accent focused), background ramp rest/hover/focused, header, placeholder color, padding 10,5,6? verify.\n- Editing semantics in EditableText: selection (double-click word, triple-click all), caret blink, clipboard, undo, IME hooks, context menu, DeleteButton (clear X) visibility rules, MaxLength, IsReadOnly.\n- IBeam cursor over the text area.' },
  { key: 'password-numberbox', ours: 'src/FluentGpu.Controls/PasswordBox.cs, NumberBox.cs', winui: 'controls/dev/CommonStyles/PasswordBox_themeresources.xaml; controls/dev/NumberBox/*', focus: '- PasswordBox: reveal button semantics (press-and-hold reveals; verify exact), bullet char U+25CF, PasswordChar API.\n- NumberBox: SpinButton placement modes (Inline/Compact popup), ValidationMode, AcceptsExpression, SmallChange/LargeChange, wheel + arrow keys, formatting/rounding, spin button repeat.' },
  { key: 'autosuggest-combobox', ours: 'src/FluentGpu.Controls/AutoSuggestBox.cs, ComboBox.cs', winui: 'dxaml/xcp/dxaml/lib/{AutoSuggestBox_Partial,ComboBox_Partial}.cpp; controls/dev/CommonStyles/ComboBox_themeresources.xaml; controls/dev/AutoSuggestBox if present; generic.xaml ComboBox template', focus: '- ComboBox: editable mode unified chrome (recent work on our side — re-verify against reference), dropdown max height, item realization, placement (opens OVER the control aligned to selected item? verify ShouldPositionDropdownCentered), keyboard typeahead/search, SelectionChangedTrigger.\n- AutoSuggestBox: QuerySubmitted/TextChanged+reason, suggestion list placement+sizing, QueryIcon, keyboard up/down through suggestions updating text.' },
  { key: 'listview-gridview', ours: 'src/FluentGpu.Controls/ListView.cs, GridView.cs, SelectionModel.cs', winui: 'controls/dev/CommonStyles/{ListViewItem,GridViewItem}_themeresources.xaml; dxaml/xcp/dxaml/lib/ListViewBase_Partial*.cpp (skim interaction parts), ListViewItemPresenter usage in generic.xaml', focus: '- ListViewItemPresenter chrome: selection indicator pill 3x16, multi-select checkmark mode, rounded corners + margins 4,2, reveal/hover brushes, swipe? no.\n- Selection semantics: Single/Multiple/Extended (ctrl/shift), SelectionFollowsFocus, space/ctrl-space toggles.\n- Keyboard: typeahead?, PgUp/PgDn, Home/End, arrow wrapping in GridView grid layout.\n- Drag-reorder visuals.\n- GridView item chrome differences (border-based selection, checkmark corner).' },
  { key: 'itemsview-repeater', ours: 'src/FluentGpu.Controls/ItemsView.cs, ItemContainer.cs, Repeater.cs, Virtual.cs', winui: 'controls/dev/ItemsView/*, controls/dev/ItemContainer/*, controls/dev/Repeater/* (ItemsRepeater)', focus: '- ItemsView: selection modes incl. invoke-only, ItemInvoked, keyboard nav via current-item (not focus?), StartBringItemIntoView.\n- ItemContainer: selection visual (check glyph circle top-right), pointer-over overlay brushes, SelectionCheckbox? verify template.\n- ItemsRepeater: layout virtualization contract, element recycling events.' },
  { key: 'treeview', ours: 'src/FluentGpu.Controls/TreeView.cs', winui: 'controls/dev/TreeView/*', focus: '- Indentation per depth (16px? verify exact), chevron glyph + rotate anim, expand/collapse anims, selection modes (None/Single/Multiple with checkboxes), drag reorder, ItemInvoked vs selection, keyboard left/right expand-collapse semantics.' },
  { key: 'navigationview', ours: 'src/FluentGpu.Controls/NavigationView.cs (also skim src/FluentGpu.Controls/Navigation.cs if related)', winui: 'controls/dev/NavigationView/* (NavigationView.cpp, NavigationViewItem.cpp, NavigationViewItemPresenter.cpp, NavigationViewItemBase.h, NavigationView.xaml, NavigationView_themeresources.xaml)', focus: 'GROUND TRUTH already established — verify each against our code and find MORE:\n- Child indentation: WinUI c_itemIndentation = 31 px/level (NavigationViewItemBase.h:63); ours IndentStep=24f → confirm and flag (user-reported bug).\n- Cursor: WinUI items use arrow (no hand anywhere in folder); our items likely inherit engine Hand default — confirm exactly which elements.\n- Selection pill: 3x16 r2 accent; WinUI move anim ~600ms translate+fade w/ 200ms center scale (NavigationView.cpp:2176-2221) vs our MotionSprings.NavPill — judge equivalence; verify indicator-on-collapsed-parent behavior (FindLowestLevelContainerToDisplaySelectionIndicator, cpp:5553-5606) exists in ours.\n- Pane open/close: 350ms KeySpline 0.1,0.9,0.2,1 open / 120ms close (NavigationView.xaml:94-122) vs ours.\n- Top mode indicator dimensions (verify exact: width/height/placement at bottom) + 48px top pane, content margins 8,0,12,0.\n- Item 36px height, pressed foreground TextFillColorSecondary, chevron AnimatedIcon rotation, badge slot, compact-rail tooltip on truncated items?, PaneCustomContent/PaneFooter/Header slots, back button states, AutoSuggestBox in compact mode behavior, settings item bottom placement, separators, overflow in Top mode.' },
  { key: 'personpicture', ours: 'src/FluentGpu.Controls/PersonPicture.cs + find its usage in src/FluentGpu.WindowsApp gallery', winui: 'controls/dev/PersonPicture/* (PersonPicture.cpp/.idl/.xaml, PersonPicture_themeresources.xaml, InitialsGenerator.cpp)', focus: 'USER REPORTS THIS CONTROL IS "JUST PLAIN WRONG" — gallery screenshot shows three initial circles (JD large, AB medium, CK small) where initials text appears oversized relative to circles / overflows the small circle. Diagnose ROOT CAUSE exactly: read our font-size math (claimed Width*0.42), how TextEl is centered/measured in the circle, what sizes the gallery passes, whether the circle Box actually gets size x size, badge offset math (ours uses OffsetX=plate+4, OffsetY=-4 vs WinUI BadgeGrid margin 0,-4,-4,0 top-right align + stroke 2px).\nGround truth: font = max(1, Width*0.42) via OnSizeChanged (PersonPicture.cpp:563-617); square coercion min(W,H); badge plate = size*0.5, badge font = badgeHeight*0.6; default 96x96; brushes ControlAltFillColorQuarternary fill / CardStrokeColorDefault stroke 1px / TextFillColorPrimary fg / AccentFillColorDefault badge / TextOnAccentFillColorPrimary badge fg; states Photo/Initials/NoPhotoOrInitials(E77B)/Group(E716); FontWeight from control (verify template default — SemiBold?).\nAPI gaps vs idl: ProfilePicture(ImageSource), Contact, BadgeText, BadgeImageSource, PreferSmallImage, TemplateSettings. Also verify our InitialsFromDisplayName vs InitialsGenerator.cpp (Contact path, single-word → 1 char, CJK→empty→person glyph).' },
  { key: 'tabview', ours: 'src/FluentGpu.Controls/TabView.cs', winui: 'controls/dev/TabView/*', focus: '- Tab item: selected plate shape (rounded top corners 8? verify), separators between unselected tabs, close button visibility (hover/selected/CloseButtonOverlayMode), widths (Equal/SizeToContent, min/max 240?), overflow scroll buttons + Ctrl+Tab cycling, Ctrl+F4 close, drag reorder + tear-out APIs, add button, keyboard, the selected tab z-order/no-separator-adjacent rule, tab close on middle-click.' },
  { key: 'pivot-selectorbar', ours: 'src/FluentGpu.Controls/Pivot.cs, SelectorBar.cs', winui: 'Glob dxaml/phone/**/Pivot* for behavior + grep generic.xaml/CommonStyles for Pivot styles; controls/dev/SelectorBar/*', focus: '- Pivot: header item states (selected bold? size 24 light?), slide animation between items, header carousel/wrap?, focus-follows.\n- SelectorBar: item icon+text layout, selection underline pill geometry + spring/anim params, keyboard arrows change selection? (verify: focus moves, selection follows focus?), sizes/padding.' },
  { key: 'breadcrumb-pips', ours: 'src/FluentGpu.Controls/BreadcrumbBar.cs, PipsPager.cs', winui: 'controls/dev/Breadcrumb/*, controls/dev/PipsPager/*', focus: '- BreadcrumbBar: chevron separator glyph E76C size, crumb ellipsis dropdown when overflow (collapsed nodes flyout), last item non-clickable + bold styling, ItemClicked args index.\n- PipsPager: pip sizes (selected 6? unselected 4? verify exact + GlyphSize), max visible pips scrolling (5 default), prev/next buttons visibility modes, orientation, pip hover/pressed scale, animation when selection moves (scroll), wraparound?, keyboard.' },
  { key: 'menus', ours: 'src/FluentGpu.Controls/MenuFlyout.cs, MenuBar.cs', winui: 'dxaml/xcp/dxaml/lib/MenuFlyout*_Partial.cpp + CommonStyles/MenuFlyout_themeresources.xaml + generic.xaml MenuFlyoutPresenter/MenuFlyoutItem templates; controls/dev/MenuBar/*; controls/dev/RadioMenuFlyoutItem/*', focus: '- MenuFlyoutItem: heights 32? padding, icon column 32?, keyboard accelerator text right-aligned (TextSecondary), check glyph for toggle items, RadioMenuFlyoutItem bullet, separator 1px margins, sub-menu open on hover delay (~?ms) + chevron, open/close motion (entrance translate + fade durations), acrylic + shadow, max width?, mnemonic underlines?\n- MenuBar: top-level item padding/heights, open-on-click then hover-tracking across items, Alt key activation?, F10.' },
  { key: 'commandbar-appbar', ours: 'src/FluentGpu.Controls/CommandBar.cs, AppBarButton.cs, AppBarToggleButton.cs, AppBarSeparator.cs', winui: 'dxaml/xcp/dxaml/lib/{CommandBar_Partial,AppBarButton_Partial,AppBarToggleButton_Partial}.cpp + CommonStyles/{CommandBar,AppBarButton,AppBarToggleButton,AppBarSeparator}_themeresources.xaml + generic.xaml templates', focus: '- AppBarButton: 48px? width compact, icon 16 + label below (or right per DefaultLabelPosition), LabelOnRightStyle, overflow item style (menu-like row), keyboard accelerator text.\n- CommandBar: Open/closed states + sizes (compact 48 closed?), More button (E712), overflow menu population + separators, dynamic overflow move order, IsSticky/light-dismiss, opening up vs down.\n- AppBarToggleButton checked plate accent.' },
  { key: 'commandbarflyout', ours: 'src/FluentGpu.Controls/CommandBarFlyout.cs', winui: 'controls/dev/CommandBarFlyout/* (incl TextCommandBarFlyout)', focus: '- Collapsed→expanded transition (primary row + chevron expands secondary list; expand direction up/down), open animation (clip + translate, durations/splines), AlwaysExpanded, proofing menu?, the width animation, secondary items as menu rows, shadow/acrylic.' },
  { key: 'infobar-infobadge', ours: 'src/FluentGpu.Controls/InfoBar.cs, InfoBadge.cs', winui: 'controls/dev/InfoBar/*, controls/dev/InfoBadge/*', focus: '- InfoBar: severity icon glyphs + colors (Informational/Success E930?/Warning/Error E783? — verify exact glyph codepoints + background brushes per severity), close button, IsIconVisible, ActionButton slot, wrapping rules (message inline vs wrapped layout switch), min height 48?\n- InfoBadge: value/icon/dot variants, sizes (dot 4? value height 16?), corner rounding, colors per style (attention/informational/success/caution/critical preset styles), font size 11?, padding, position in anchors.' },
  { key: 'teachingtip-tooltip', ours: 'src/FluentGpu.Controls/TeachingTip.cs, ToolTip.cs', winui: 'controls/dev/TeachingTip/*; CommonStyles/ToolTip_themeresources.xaml + dxaml/xcp/dxaml/lib/ToolTip*_Partial.cpp', focus: '- ToolTip: show delay (~1000ms? verify from code/BetweenShowDelay), reshow delay, hide on pointer move?, offset from pointer/placement (verify 20px?), max width 320?, padding 8,5?, corner 4, entrance animation (fade+slide?), keyboard-focus triggered tooltips.\n- TeachingTip: tail/pointer geometry + placement modes (12 positions), light-dismiss vs persistent, IsLightDismissEnabled, action/close buttons layout, hero content, icon, open/close animation (scale from tail origin, ~?ms), target highlighting?' },
  { key: 'contentdialog', ours: 'src/FluentGpu.Controls/ContentDialog.cs', winui: 'CommonStyles/ContentDialog_themeresources.xaml + generic.xaml ContentDialog template + dxaml/xcp/dxaml/lib/ContentDialog_Partial.cpp (skim)', focus: '- Smoke layer color/opacity, dialog max width 548?/min width 320?, padding 24, title 20 SemiBold, button row layout (equal width, 8 gap, single accent default button), DefaultButton Enter mapping + Escape=Close, open animation (scale 1.05→1 + fade ~?ms, smoke fade 83ms), separate command space background (footer plate SolidBackgroundFillColorBase + top border), FullSizeDesired.' },
  { key: 'progress', ours: 'src/FluentGpu.Controls/ProgressBar.cs, ProgressRing.cs', winui: 'controls/dev/ProgressBar/*, controls/dev/ProgressRing/* (incl Lottie sources / .cpp math)', focus: '- ProgressBar: determinate track 1?3?px height (verify: 3px? track ControlStrongFill? actually base track color), indeterminate two-dot sweep animation exact (Lottie or composition: durations, the 2 sliding segments widths/easing), paused/error state colors (CautionFill/CriticalFill), corner radius 1.5.\n- ProgressRing: stroke width scaling (ring thickness vs diameter), indeterminate arc sweep params (rotation period, arc grow/shrink range — from LottieGen code AnimatedVisuals/ProgressRingIndeterminate.cpp if present), determinate start angle -90, color accent, min size.' },
  { key: 'expander', ours: 'src/FluentGpu.Controls/Expander.cs', winui: 'controls/dev/Expander/*', focus: '- Header 48px min? chevron button 32 + rotate 180 anim duration, content border merge (shared border, bottom corners), expand 333ms / collapse 167ms splines (we intentionally reflow — check timings/curves match), ExpandDirection Up, header press states on whole header toggles?, content padding 16.' },
  { key: 'splitview', ours: 'src/FluentGpu.Controls/SplitView.cs', winui: 'dxaml/xcp/dxaml/lib/SplitView_Partial.cpp + controls/dev/SplitView/SplitView_themeresources.xaml + generic.xaml SplitView template', focus: '- 4 display modes exact behavior (Overlay/Inline/CompactOverlay/CompactInline), pane open/close animation duration+easing per mode, light-dismiss (overlay click + Escape), CompactPaneLength 48 / OpenPaneLength 320 defaults, pane placement right, PaneOpening/Closing events.' },
  { key: 'scrollbars', ours: 'src/FluentGpu.Controls/ScrollBar.cs, AnnotatedScrollBar.cs', winui: 'CommonStyles/ScrollBar_themeresources.xaml + dxaml/xcp/dxaml/lib/ScrollBar_Partial.cpp; controls/dev/AnnotatedScrollBar/*; also CommonStyles/ScrollViewer_themeresources.xaml for indicator-mode transitions', focus: '- ScrollBar collapsed→expanded on hover (panning indicator 2px? → full 6px? verify thumb sizes + paddings), expand/collapse animation (duration 167? delay before collapse ~?ms ContractDelay 2s?), arrow repeat buttons (glyphs E0E0?-style small arrows, repeat behavior), min thumb size 24?, track click page behavior.\n- AnnotatedScrollBar: label slots, detail label flyout on hover, click-to-jump semantics.' },
  { key: 'colorpicker', ours: 'src/FluentGpu.Controls/ColorPicker.cs', winui: 'controls/dev/ColorPicker/*', focus: '- Spectrum (box vs ring), third-dimension slider, alpha slider + channel text fields (RGB/HSV toggle, hex field), preview swatches (current/previous), spectrum cursor ring visuals + keyboard arrows w/ shift step, ColorChanged args, more/less button collapse.' },
  { key: 'datetime', ours: 'src/FluentGpu.Controls/DatePicker.cs, TimePicker.cs, CalendarDatePicker.cs, CalendarView.cs', winui: 'CommonStyles/{DatePicker,TimePicker,CalendarView,CalendarDatePicker,DateTimePickerFlyout}_themeresources.xaml; dxaml/xcp/dxaml/lib/{DatePicker_Partial,TimePicker_Partial,CalendarView_Partial*,CalendarDatePicker_Partial}.cpp (skim behavior)', focus: '- DatePicker/TimePicker: 3-column looping selectors flyout (item height 32?, loop wheel physics, accept/dismiss bar), column order/widths, highlight rect.\n- CalendarView: 7x6 day grid, day item states (today = accent filled circle? verify shape — rounded?), out-of-scope opacity, header nav buttons, month/year/decade zoom levels + zoom animation, density bars?, min/max date, first day of week.\n- CalendarDatePicker: icon E787, placeholder text, flyout opens CalendarView.' },
  { key: 'flipview-swipe', ours: 'src/FluentGpu.Controls/FlipView.cs, SwipeControl.cs', winui: 'CommonStyles/{FlipView,FlipViewItem}_themeresources.xaml + dxaml/xcp/dxaml/lib/FlipView_Partial.cpp; controls/dev/SwipeControl/*', focus: '- FlipView: prev/next buttons appear on hover (placement, 38x38?, glyphs E0E2/E0E3? verify), slide animation between items (duration/curve), wheel + keyboard + touch panning w/ snap points, SelectedIndex wrap?, vertical orientation.\n- SwipeControl: reveal/execute modes, threshold distances, open/close springs, item styling (icon+text), commanding.' },
  { key: 'icons-animatedicon', ours: 'src/FluentGpu.Controls/AnimatedIcon.cs, Icons.cs', winui: 'controls/dev/AnimatedIcon/* (AnimatedIcon.cpp state machine, markers), controls/dev/IconSource/*', focus: '- AnimatedIcon: state transition markers protocol (NormalToPointerOver etc.), fallback to static glyph, FallbackIconSource, our impl vs WinUI semantics.\n- Icons.cs: spot-check 15 glyph codepoints against Segoe Fluent Icons usage in WinUI templates (chevrons E70D/E70E/E76C, More E712, Accept E73E, Cancel E711, Settings E713, Search E721, Back E72B, Contact E77B, People E716, ChromeClose E8BB vs E711 confusion, CheckMark E001 vs E73E usage in CheckBox template — verify which the template uses).' },
  { key: 'flyout-infra', ours: 'src/FluentGpu.Controls/Popup.cs, GenericFlyout.cs, FlyoutPositioner.cs, OverlayHost.cs', winui: 'dxaml/xcp/dxaml/lib/FlyoutBase_Partial.cpp (placement logic), CommonStyles/FlyoutPresenter_themeresources.xaml, generic.xaml FlyoutPresenter; dxaml/xcp/dxaml/lib/Popup_Partial.cpp (light-dismiss)', focus: '- Placement enum coverage (Top/Bottom/Left/Right + Edge-aligned variants + Full) vs our FlyoutPlacement; flip/clamp-to-window rules + margins from anchor (verify 4px? gap) vs FlyoutBase placement code; ShowMode (Standard/Transient/TransientWithDismissOnPointerMoveAway); light-dismiss overlay + Escape + focus restore to anchor; entrance animations per placement direction (translate distance 40?? verify PopupThemeTransition offset, duration); shadow (ThemeShadow 16/32px depth?); FlyoutPresenter padding 16,15,16,17? verify; max height scroll.' },
  { key: 'panels', ours: 'src/FluentGpu.Controls/Canvas.cs, RelativePanel.cs, VariableSizedWrapGrid.cs, Viewbox.cs, BorderControl.cs', winui: 'dxaml/xcp/dxaml/lib/{RelativePanel_Partial,VariableSizedWrapGrid_Partial,Viewbox_Partial}.cpp + dxaml/xcp/core equivalents for Canvas/Border (Glob for them)', focus: 'Lower-depth pass: verify layout algorithm correctness vs reference (RelativePanel constraint solving order, VSWG ItemWidth/Height + spans, Viewbox Stretch/StretchDirection math, Canvas ZIndex, Border child clipping with corner radius). Flag only real algorithmic deltas/bugs, not style.' },
  { key: 'media', ours: 'src/FluentGpu.Controls/MediaPlayerElement.cs', winui: 'CommonStyles/MediaTransportControls_themeresources.xaml + dxaml/xcp/dxaml/lib/MediaTransportControls_Partial.cpp (skim)', focus: 'Medium-depth: transport controls present? (play/pause, seek bar w/ buffer indication, volume flyout, time text, fullscreen), auto-hide timeout (~3s), control panel acrylic plate, double-tap fullscreen?, our API vs MediaPlayerElement (Source, AutoPlay, PosterSource, AreTransportControlsEnabled, Stretch).' },
  { key: 'text-display', ours: 'src/FluentGpu.Controls/RichTextBlock.cs, TextBlockDemo.cs + src/FluentGpu.Dsl/Typography.cs (Glob if absent) + how TextEl defaults work in src/FluentGpu.Dsl', winui: 'CommonStyles/TextBlock_themeresources.xaml; grep generic.xaml for CaptionTextBlockStyle/BodyTextBlockStyle/BodyStrongTextBlockStyle/SubtitleTextBlockStyle/TitleTextBlockStyle/TitleLargeTextBlockStyle/DisplayTextBlockStyle', focus: '- Type ramp exact: Caption 12, Body 14, BodyStrong 14 SemiBold, Subtitle 20 SemiBold, Title 28 SemiBold, TitleLarge 40 SemiBold, Display 68 SemiBold + LineHeights (16/20/20/28/36/52/92) — verify from xaml and compare to whatever ramp we expose; default TextBlock foreground/selection brush; text trimming/ellipsis support in our engine (engine-gap if missing); RichTextBlock inline runs/hyperlink inline (hand cursor + underline).' },
  { key: 'cursor-audit', ours: 'ALL of src/FluentGpu.Controls/*.cs + engine default in src/FluentGpu.Input + src/FluentGpu.Pal.Windows + src/FluentGpu.Render (grep CursorId, SetCursor, IDC_)', winui: 'grep -ri cursor in dxaml/xcp/dxaml/lib (expect: HyperLinkButton_Partial.cpp SetCursor(MouseCursorHand); TextBox IBeam via core) and in controls/dev (expect ~none)', focus: 'SYSTEMIC CURSOR AUDIT (user-reported: nav items wrongly show hand). 1) Pin the exact engine default rule for clickable cursor (file:line). 2) Enumerate EVERY control whose interactive elements end up with Hand under that rule (explicit Cursor set vs inherited default) — one finding per control, severity high if WinUI uses arrow there. 3) Verify WinUI ground truth by grepping their source. 4) Recommend as engine-gap finding: flip default to Arrow, opt-in Hand only for HyperlinkButton + inline Hyperlink; IBeam for text edit fields — list exactly which of our controls then need explicit IBeam/Hand.' },
  { key: 'tokens-audit', ours: 'src/FluentGpu.Dsl/Tokens.cs (FULL — every color in dark AND light TokenSet) + gradient/acrylic specs', winui: 'controls/dev/CommonStyles/Common_themeresources_any.xaml (Default + Light dictionaries) + AcrylicBrush definitions (grep AcrylicInAppFillColorDefault)', focus: 'SYSTEMIC TOKEN AUDIT. Compare EVERY token value (ARGB hex) ours-vs-WinUI for both themes: text fills, control fills, subtle fills, control-alt fills, control-strong, strokes (control/control-strong/card/divider/surface/focus), solid backgrounds, layer fills, accent (default/secondary/tertiary/disabled + text-on-accent set), system colors (success/caution/critical fills + backgrounds), smoke. One finding per mismatched token with both hex values. Also: ControlElevationBorder / AccentControlElevationBorder / CircleElevationBorder gradient stops vs WinUI brush defs; acrylic recipes both themes; tokens WinUI defines that we lack entirely.' },
  { key: 'motion-audit', ours: 'src/FluentGpu.Controls/ControlMotion.cs + src/FluentGpu.Animation/Motion.cs + grep src/FluentGpu.Controls for DurationMs|Easing|Spring|KeySpline usages', winui: 'Common_themeresources_any.xaml animation constants + grep controls/dev/*/\\*_themeresources.xaml and generic.xaml for KeySpline/Duration patterns', focus: 'SYSTEMIC MOTION AUDIT. 1) Verify our duration tokens (83/167/250) + standard curves vs WinUI constants. 2) Spot-check 12+ control animations end-to-end (toggle switch knob, checkbox glyph, scrollbar expand, flyout entrance, dialog entrance, list selection pill, pips move, expander chevron, infobar open, tabview tab close, combo dropdown, progress indeterminate) — each: our duration+curve (file:line) vs WinUI storyboard (file:line). 3) Where we use springs instead of splines judge param equivalence (response/damping vs duration/KeySpline) — mark intentional ones as improvement/intentional, wrong ones as motion-delta.' },
  { key: 'focus-audit', ours: 'engine focus-ring drawing (grep FocusVisual|FocusOuter|FocusRing in src/FluentGpu.Render, src/FluentGpu.Input, src/FluentGpu.Dsl) + every FocusVisualMargin in src/FluentGpu.Controls', winui: 'Common_themeresources_any.xaml FocusStrokeColor* + grep FocusVisualMargin in controls/dev CommonStyles + control xamls + generic.xaml; also SystemControlFocusVisual* in generic.xaml', focus: 'SYSTEMIC FOCUS AUDIT. 1) Ring spec: WinUI outer 2px FocusStrokeColorOuter + inner 1px FocusStrokeColorInner (verify exact hex both themes) drawn outside bounds w/ corner radius following control — vs our engine drawing (file:line). 2) Visibility rule: keyboard/gamepad only (verify dxaml FocusVisualKind / our pointer-focus rule). 3) Per-control FocusVisualMargin table ours vs WinUI templates — one finding per mismatch. 4) Controls missing Focusable that WinUI makes tab-stops, and tab order / arrow-key inner navigation gaps you can spot cheaply.' },
  { key: 'missing-controls', ours: 'list src/FluentGpu.Controls/*.cs filenames (Glob)', winui: 'list controls/dev/* folders + major dxaml/xcp/dxaml/lib controls', focus: 'INVENTORY of WinUI controls we lack ENTIRELY. For each absent control: name, what it does (1 line), relevance to a Spotify-like desktop music app (WaveeMusic: 10k+ virtualized lists, album art, video, lyrics, Mica): rate needed-now / nice-to-have / skip. Candidates to check: ScrollView+ScrollPresenter (new scrolling), TitleBar, PullToRefresh/RefreshContainer, WrapPanel, TwoPaneView, PagerControl, ParallaxView, AnimatedVisualPlayer, RadioMenuFlyoutItem (if not in our MenuFlyout), ImageIcon, ListBox, SemanticZoom, Hub, RichEditBox, ItemsRepeater extras, MonochromaticOverlayPresenter, RadialGradientBrush, SplitButton variants, Frame/Page navigation, SemanticZoom, ToggleMenuFlyoutItem, AppBarElementContainer. Severity: high=needed-now, medium=nice, low=skip. kind=missing-api, needsEngineChange as appropriate.' },
]

function verifyPrompt(it, found) {
  return `You are an adversarial verifier. Another agent compared our control "${it.key}" (repo ${OURS}, files: ${it.ours}) against WinUI 3 reference (repo ${WX}; never look in BuildOutput/ or packages/). Its findings are below as JSON.

For EACH finding, re-open the cited files at the cited locations (read surrounding context) and verify:
a) The "our" claim is true — especially for missing-*/bug kinds, search our whole file AND related files (engine: src/FluentGpu.Dsl, src/FluentGpu.Input, src/FluentGpu.Render, src/FluentGpu.Controls/OverlayHost.cs|ControlMotion.cs|Icons.cs) before agreeing something is absent. Many behaviors live in the engine, not the control.
b) The WinUI value/name/number is quoted correctly from the cited file.
c) The fix states the right exact target value.
Verdicts: confirmed (both sides check out) / corrected (real issue but a detail is wrong — put the corrected fact + path:line in note) / refuted (not a real issue — say why in note). Default to refuted when you cannot reproduce the evidence. Read-only task.

FINDINGS JSON:
${JSON.stringify(found)}

Return verdicts via StructuredOutput.`
}

phase('Compare')
log('Comparing ' + ITEMS.length + ' control families/audits against WinUI reference...')

const results = await pipeline(
  ITEMS,
  (x, it) => agent(findPrompt(it), { label: 'find:' + it.key, phase: 'Compare', schema: FINDINGS }),
  (found, it) => {
    if (!found || !found.findings || found.findings.length === 0) return found
    return agent(verifyPrompt(it, found), { label: 'verify:' + it.key, phase: 'Verify', schema: VERDICTS })
      .then(v => {
        if (!v || !v.verdicts) return found
        const map = {}
        for (const vd of v.verdicts) map[vd.id] = vd
        const kept = []
        let refuted = 0
        for (const f of found.findings) {
          const vd = map[f.id]
          if (vd && vd.verdict === 'refuted') { refuted++; continue }
          if (vd && vd.verdict === 'corrected' && vd.note) kept.push({ ...f, fix: f.fix + ' [CORRECTED: ' + vd.note + ']' })
          else kept.push(f)
        }
        return { ...found, control: it.key, findings: kept, refutedCount: refuted }
      })
  }
)

const byKey = {}
let total = 0, refutedTotal = 0
for (let i = 0; i < ITEMS.length; i++) {
  const r = results[i]
  if (r) { byKey[ITEMS[i].key] = r; total += r.findings.length; refutedTotal += (r.refutedCount || 0) }
  else byKey[ITEMS[i].key] = { control: ITEMS[i].key, findings: [], notes: 'AGENT FAILED — coverage gap' }
}
log('Confirmed ' + total + ' findings (' + refutedTotal + ' refuted) across ' + ITEMS.length + ' areas')

phase('Synthesize')

const GROUPS = [
  { name: 'buttons-and-toggles', keys: ['button-family', 'split-dropdown', 'checkbox-radio', 'toggleswitch'] },
  { name: 'inputs', keys: ['textbox', 'password-numberbox', 'autosuggest-combobox', 'slider', 'rating', 'colorpicker', 'datetime'] },
  { name: 'collections', keys: ['listview-gridview', 'itemsview-repeater', 'treeview', 'flipview-swipe', 'scrollbars'] },
  { name: 'navigation-chrome', keys: ['navigationview', 'tabview', 'pivot-selectorbar', 'breadcrumb-pips', 'menus', 'commandbar-appbar', 'commandbarflyout'] },
  { name: 'surfaces-status-misc', keys: ['infobar-infobadge', 'teachingtip-tooltip', 'contentdialog', 'progress', 'expander', 'splitview', 'flyout-infra', 'media', 'personpicture', 'icons-animatedicon', 'panels', 'text-display'] },
  { name: 'engine-systemic', keys: ['cursor-audit', 'tokens-audit', 'motion-audit', 'focus-audit', 'missing-controls'] },
]

const engineFindings = []
for (const k of Object.keys(byKey)) {
  for (const f of (byKey[k].findings || [])) if (f.needsEngineChange) engineFindings.push({ from: k, ...f })
}

const sections = await parallel(GROUPS.map(g => () => {
  const slice = {}
  for (const k of g.keys) slice[k] = byKey[k]
  const extra = g.name === 'engine-systemic'
    ? '\nADDITIONALLY, here are all needsEngineChange findings collected from EVERY other control group — fold them into a deduplicated "Engine work items" list (one item per distinct engine capability, listing which controls need it):\n' + JSON.stringify(engineFindings)
    : ''
  return agent(`You are writing ONE section of an implementation plan for bringing our GPU-rendered control library (C:/WAVEE/fluent-gpu) to WinUI 3 parity. Below are VERIFIED findings (JSON) for the control group "${g.name}". Do not re-explore; just organize. You may quickly open a file only to resolve an ambiguity.

Write a markdown section: one subsection per control, findings ordered high→low severity, each as a compact actionable bullet:
- [sev|kind] Title — ours: <value> (path:line) vs WinUI: <exact value> (path:line). Fix: <exact change>.
PRESERVE every exact number, hex color, resource/token name, duration, file path. Do not drop ANY high/medium finding; you may merge near-duplicate lows. Mark items needing engine changes with (ENGINE). Mark quick wins (<~30min) with (QUICK). Put kind=improvement items in a separate "Do it better" sub-list per control. End the section with a 2-line effort estimate (S/M/L per control). Output ONLY the markdown section, starting with "## ${g.name}".

FINDINGS:
${JSON.stringify(slice)}${extra}`, { label: 'synth:' + g.name, phase: 'Synthesize' })
}))

const perControl = ITEMS.map(it => {
  const r = byKey[it.key]
  const f = r.findings || []
  return { control: it.key, high: f.filter(x => x.severity === 'high').length, med: f.filter(x => x.severity === 'medium').length, low: f.filter(x => x.severity === 'low').length, refuted: r.refutedCount || 0, failed: !!(r.notes && r.notes.indexOf('AGENT FAILED') >= 0) }
})

return {
  totals: { confirmed: total, refuted: refutedTotal },
  perControl,
  sections: sections.filter(Boolean).join('\n\n'),
}