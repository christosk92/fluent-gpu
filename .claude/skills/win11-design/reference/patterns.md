# How real Windows 11 apps compose windows and pages

Read from the repos under `C:\WAVEE\`: WinUI-Gallery (the reference), devhome, Files, Wino-Mail, ambie (UWP, the most
brand-forward), and the Community Toolkit SettingsControls (`C:\WAVEE\Windows\components\SettingsControls`).

## Window shell

- **WinUI Gallery** (`WinUI-Gallery\WinUIGallery\MainWindow.xaml`): `MicaBackdrop` (:12-14); row 0 the `TitleBar`
  control (tall, 48: `MainWindow.xaml.cs:105`) with a 16px icon, plain title, back + pane toggle, search in `Content`
  (MaxWidth 580, :36-41); row 1 a left `NavigationView` with its own back/toggle hidden (:137-138). The NavigationView's
  default content layer stays on (LayerFill, CardStroke 1,1,0,0, radius 8,0,0,0). Title bar and pane sit on Mica.
- **Dev Home** removes the layer (pages directly on Mica, `devhome\src\Views\ShellPage.xaml:42-48`), pane 350,
  expanded threshold 1280. **Files** has no NavigationView: sidebar on `LayerOnMicaBaseAltFillColorDefault`, the file
  area a floating 8-radius panel with CardStroke and ThemeShadow (`ModernShellPage.xaml:33-43`). **Wino** puts content in
  a 7-radius bordered plate (`App.xaml:44-49`).

## Page anatomy

- Gallery Settings (`Pages\SettingsPage.xaml`): header `TitleTextBlockStyle`, margin 36,24,36,0, MaxWidth 1064,
  outside the scroller (:28-34); ScrollView padding 36,0,36,0 (:35-39); content StackPanel MaxWidth 1064, centred (:41).
- Gallery item pages: header 36,24,36,0; content padding 36,0,36,36; below 640px both become 16 (`ItemPage.xaml:37-88`).
- Dev Home: MaxPageContentWidth 1236, ContentPageMargin 40,0 (`Styles\LayoutSizes.xaml:6`, `Thickness.xaml:30`); page
  header Subtitle 20. Files settings: Subtitle title, padding 24,12,24,24. Wino settings: MaxWidth 900, padding 20.

## Settings pages

- Gallery `SettingsCardSpacing = 4` (:11); section header BodyStrong margin **1,30,0,6** (:14-21) (Dev Home 1,28,0,4).
- Every card: 16–20px glyph HeaderIcon, Header, optional one-line Description, control as Content (ComboBox,
  ToggleSwitch, buttons MinWidth 120 spacing 8). Link cards `IsClickEnabled` + ActionIcon E8A7 (external) / E8C8 (copy).
- About: version as secondary text; last element bottom margin 36 (:110-119). `SettingsExpander` items indent 58,8,44,8.
- Files and Dev Home: `StackPanel Spacing="4"` of SettingsCard/Expander; Dev Home's settings root is clickable cards
  that navigate to sub-pages.

## Lists, tiles, dashboards

- Gallery home tile (`Styles\ItemTemplates.xaml:7-61`): 300×96, padding 8, `ControlFillColorDefault`, CardStroke 1,
  radius 8, 32px image, BodyStrong title, Caption secondary subtitle; grid item margin 0,0,12,12.
- Gallery empty state (`HomePage.xaml:147-163`): 36px image, BodyStrong "No favorites yet", secondary Body sentence.
- Dev Home dashboard: Subtitle title + **default** "Add widget" button (not accent), fixed 300-wide widgets, radius 7,
  CardBackgroundDefault, centred ProgressRing while loading, text + hyperlink when empty.
- Files drives: UniformGridLayout min 240×72, spacing 8; each tile a default Button with a 32px image, name, ProgressBar,
  caption. Shell warnings: closable InfoBar above the content, radius 8.
- Wino mail list: InfoBadge counts on nav items, Segmented folder filter, 36×36 transparent icon buttons, InfoBar at the
  top of the list, empty reading pane = big glyph + text at 50% opacity.

## Heroes (home pages of content apps only)

- Gallery header (`Controls\HomePage\HomePageHeader.xaml`): 400px image fading out via an opacity mask into the page,
  TitleLarge 40 + 18px subtitle at 36,48, then a row of 232×172 acrylic link tiles (radius 8, padding 24, spacing 12).
- Dev Home banner: 214 tall, illustration background, Subtitle + description (max 450) + one default button + close X.

## Commands

- One accent per flow: wizard footer = full-width bar, 1px top border, padding 0,24, right-aligned Cancel (hyperlink),
  Back (default), Next (accent), MinWidth 120 (`SetupFlowNavigation.xaml:11-24`). Dialogs set
  `PrimaryButtonStyle=AccentButtonStyle`. Page tools are subtle buttons / DropDownButtons with a 16px glyph.

## Drop target

Wino compose (`Views\Mail\ComposePage.xaml:550-572`): shown only while dragging; an in-app acrylic overlay, radius 9,
dashed 2px stroke (3,4) at 50% opacity, centred 20px semibold hint at 50%. In FluentGpu `DropZone.Window` gives the
equivalent drag-time ring and glow over the content.

## What these apps avoid

Big padded wrapper cards around sections; coloured circles behind icons; status as dashboard tiles; several accent
buttons; stretching content past ~1000–1236px; heavy hover chrome (Dev Home even removes the GridView hover overlay).
