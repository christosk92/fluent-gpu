using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>A single top-level entry in a <see cref="MenuBar"/>: a clickable title (e.g. "File") whose
/// <paramref name="Items"/> populate the <see cref="MenuFlyout"/> that opens beneath it. <see cref="AccessKey"/> is
/// the Alt-mnemonic letter (WinUI MenuBarItem AccessKey — MenuBarItem.cpp:249 OnMenuBarItemAccessKeyInvoked →
/// ShowMenuFlyout); '\0' derives it from the first letter of <paramref name="Title"/>.</summary>
public sealed record MenuBarItem(string Title, IReadOnlyList<MenuFlyoutItem> Items)
{
    public char AccessKey { get; init; }
}

/// <summary>A WinUI MenuBar: a horizontal bar of top-level menu titles (File, Edit, View, …). Clicking a title opens its
/// <see cref="MenuFlyout"/> below the title; re-clicking the open title closes it. 1:1 behavior with
/// <c>controls/dev/MenuBar</c>:
/// <list type="bullet">
/// <item>Alt access keys — each title carries an Alt+letter mnemonic (<see cref="MenuBarItem.AccessKey"/>, default the
/// first title letter) that opens its menu (MenuBarItem.xaml:12 ExitDisplayModeOnAccessKeyInvoked=False;
/// MenuBarItem.cpp:249-253).</item>
/// <item>Hover-switch — while ANY menu in the bar is open, pointer-enter on another title opens that one
/// (MenuBarItem.cpp:130-138 OnMenuBarItemPointerEntered → IsFlyoutOpen → ShowMenuFlyout).</item>
/// <item>Keyboard — a focused title opens on Down/Enter/Space (cpp:158-165); Left/Right move focus between titles
/// (cpp:166-191); while a menu is OPEN, Left/Right open the ADJACENT menu (cpp:205-228 OnPresenterKeyDown →
/// OpenFlyoutFrom), cycling. Escape closes (the overlay's light-dismiss preview).</item>
/// <item>Style — bar MinHeight 40 (MenuBarHeight, MenuBar_themeresources.xaml:44), item button padding 10,4,10,4
/// (MenuBarItemButtonPadding :45), item margin 4 (MenuBarItemMargin :46), ControlCornerRadius 4, fills rest/hover/
/// pressed/selected = SubtleFill Transparent/Secondary/Tertiary/Tertiary (:7-10), FocusVisualMargin −3
/// (MenuBarItem.xaml:14).</item>
/// </list></summary>
public sealed class MenuBar : Component
{
    public IReadOnlyList<MenuBarItem> Menus = [];
    public int OpenIndexOnMount = -1;   // deterministic visual-shot hook: open one top-level menu after first mount

    public static Element Create(IReadOnlyList<MenuBarItem> menus)
        => Embed.Comp(() => new MenuBar { Menus = menus });

    /// <summary>Open-menu coordination shared by the bar's buttons: <see cref="OpenIndex"/> is the index of the open
    /// menu (-1 = none); <see cref="KeyboardOpen"/> marks the next open as keyboard-driven (cursor + focus land on the
    /// first row). Buttons declaratively follow the signal (open mine / close not-mine).</summary>
    internal sealed class BarState
    {
        public readonly Signal<int> OpenIndex = new(-1);
        public bool KeyboardOpen;
        public int Count;
        public NodeHandle[] Nodes = [];   // realized title nodes, for Left/Right focus moves between titles
        public NodeHandle Root;           // the bar strip — the overlay pass-through target while a menu is open
    }

    public override Element Render()
    {
        var state = UseRef<BarState?>(null);
        state.Value ??= new BarState();
        var bar = state.Value;
        bar.Count = Menus.Count;
        if (bar.Nodes.Length != Menus.Count) bar.Nodes = new NodeHandle[Menus.Count];

        var buttons = new Element[Menus.Count];
        for (int i = 0; i < Menus.Count; i++)
        {
            var m = Menus[i];
            int idx = i;
            buttons[i] = Embed.Comp(() => new MenuBarButton
            {
                Index = idx,
                Title = m.Title,
                Items = m.Items,
                AccessKey = ResolveAccessKey(m),
                Bar = bar,
                OpenOnMount = idx == OpenIndexOnMount,
            }) with { Key = "menubar:" + idx };
        }

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            MinHeight = 40f,   // MenuBarHeight (MenuBar_themeresources.xaml:44)
            // MenuBar.Background = MenuBarBackground = SubtleFillColorTransparent (explicit transparent surface).
            Fill = Tok.FillSubtleTransparent,
            OnRealized = h => bar.Root = h,   // overlay pass-through target (OverlayInputPassThroughElement)
            Children = buttons,
        };
    }

    static char ResolveAccessKey(MenuBarItem m)
    {
        if (m.AccessKey != '\0') return char.ToUpperInvariant(m.AccessKey);
        foreach (char c in m.Title)
            if (char.IsAsciiLetterOrDigit(c)) return char.ToUpperInvariant(c);
        return '\0';
    }

    /// <summary>One top-level title in the bar. Owns the anchor node + overlay handle and declaratively follows the
    /// bar's <see cref="BarState.OpenIndex"/> signal — so click-toggle, hover-switch, access keys, and Left/Right
    /// adjacent-menu navigation all converge on one open/close path.</summary>
    internal sealed class MenuBarButton : Component
    {
        public int Index;
        public string Title = "";
        public IReadOnlyList<MenuFlyoutItem> Items = [];
        public char AccessKey;
        public required BarState Bar;
        public bool OpenOnMount;

        public override Element Render()
        {
            var svc = UseContext(Overlay.Service);
            var hooks = UseContext(InputHooks.Current);
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);
            var autoOpened = UseRef(false);

            int openIdx = Bar.OpenIndex.Value;   // subscribe → re-render (Selected fill) + drive open/close in the effect
            bool isMine = openIdx == Index;

            // While a menu is open, Left/Right inside the presenter OPEN the adjacent menu (cyclic) —
            // MenuBarItem.cpp:205-228 OnPresenterKeyDown → OpenFlyoutFrom. Adjacent opens are keyboard-driven.
            void Navigate(int dir)
            {
                int n = Bar.Count;
                if (n <= 1) return;
                Bar.KeyboardOpen = true;
                Bar.OpenIndex.Value = ((Index + dir) % n + n) % n;
            }

            void Open(bool keyboard)
            {
                if (handle.Value is { IsOpen: true }) return;
                bool focusFirst = keyboard || Bar.KeyboardOpen;
                Bar.KeyboardOpen = false;
                handle.Value = svc.Open(
                    () => anchor.Value,
                    () => Embed.Comp(() => new MenuFlyoutPresenter
                    {
                        Items = Items,
                        Close = () => handle.Value?.Close(),
                        MinWidth = MenuFlyout.ThemeMinWidth,
                        OnNavigate = Navigate,
                        FocusFirstOnMount = focusFirst,
                    }),
                    // Below the title, left edges aligned; WinUI menus are WINDOWED popups
                    // (FlyoutBase_Partial.cpp:3181-3205 SetIsWindowedPopup) → may escape the window.
                    FlyoutPlacement.BottomLeft,
                    new PopupOptions(Chrome: PopupChrome.Flyout)
                    {
                        ConstrainToRootBounds = false,
                        // WinUI sets the BAR as OverlayInputPassThroughElement (MenuBarItem.cpp:64-70): hover/press
                        // over the strip bypass the light-dismiss scrim — hover-switch + press-another-title work.
                        PassThrough = () => Bar.Root,
                    });
                handle.Value.ClosedAction = () =>
                {
                    handle.Value = null;
                    // Light-dismiss/Escape/invoke closed MY menu → the bar is closed (unless another title took over).
                    if (Bar.OpenIndex.Peek() == Index) Bar.OpenIndex.Value = -1;
                };
            }

            void CloseMine() { if (handle.Value is { IsOpen: true } h) h.Close(); }

            // Declarative open/close: follow the bar's OpenIndex (post-commit, so the overlay reconciles next pass).
            UseEffect(() =>
            {
                if (isMine && handle.Value is not { IsOpen: true }) Open(keyboard: false);
                else if (!isMine && handle.Value is { IsOpen: true }) CloseMine();
            }, openIdx);

            void Toggle()
            {
                Bar.KeyboardOpen = false;
                Bar.OpenIndex.Value = Bar.OpenIndex.Peek() == Index ? -1 : Index;
            }

            // Keyboard on the focused TITLE: Down/Enter/Space open (MenuBarItem.cpp:158-165 — Enter/Space ride the
            // engine's focused-clickable activation via OnClick); Left/Right move FOCUS between titles when closed
            // (cpp:166-191), and open the ADJACENT menu when this title's menu is open (the presenter-scoped
            // OpenFlyoutFrom path, cpp:205-228 — click-open keeps focus on the title here).
            void OnKey(KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Down)
                {
                    Bar.KeyboardOpen = true;
                    Bar.OpenIndex.Value = Index;
                    e.Handled = true;
                    return;
                }
                if (e.KeyCode is not (Keys.Left or Keys.Right)) return;
                int dir = e.KeyCode == Keys.Right ? +1 : -1;
                if (Bar.OpenIndex.Peek() == Index) { Navigate(dir); e.Handled = true; return; }
                int n = Bar.Count;
                if (n <= 1) return;
                int adj = ((Index + dir) % n + n) % n;
                var target = Bar.Nodes[adj];
                if (!target.IsNull) { hooks.MoveFocusVisual?.Invoke(target); e.Handled = true; }
            }

            UseLayoutEffect(() =>
            {
                if (!OpenOnMount || autoOpened.Value || anchor.Value.IsNull) return;
                autoOpened.Value = true;
                Bar.OpenIndex.Value = Index;
            }, OpenOnMount);

            // MenuBarItem 'Selected' state (flyout open) = SubtleFillColorTertiary, same as Pressed; at rest it is
            // MenuBarItemBackground = SubtleFillColorTransparent (explicit, not just defaulted-transparent).
            return new BoxEl
            {
                Direction = 0,
                AlignItems = FlexAlign.Center,
                Padding = new Edges4(10, 4, 10, 4),     // MenuBarItemButtonPadding (MenuBar_themeresources.xaml:45)
                Margin = Edges4.All(4),                 // MenuBarItemMargin (:46)
                Corners = Radii.ControlAll,             // ControlCornerRadius = 4
                Fill = isMine ? Tok.FillSubtleTertiary : Tok.FillSubtleTransparent,   // Selected = SubtleFillColorTertiary (:10)
                HoverFill = Tok.FillSubtleSecondary,    // MenuBarItemBackgroundPointerOver (:8)
                PressedFill = Tok.FillSubtleTertiary,   // MenuBarItemBackgroundPressed (:9)
                Role = AutomationRole.MenuItem,
                Focusable = true,                       // IsTabStop=True (MenuBarItem.xaml:11)
                FocusVisualMargin = Edges4.All(-3f),    // FocusVisualMargin=-3 (MenuBarItem.xaml:14)
                AccessKey = AccessKey,                  // Alt+letter opens (AccessKeyInvoked → ShowMenuFlyout, cpp:249)
                OnRealized = h => { anchor.Value = h; if ((uint)Index < (uint)Bar.Nodes.Length) Bar.Nodes[Index] = h; },
                OnClick = Toggle,
                OnKeyDown = OnKey,
                // Hover-switch: while any menu in the bar is open, entering another title opens it (cpp:130-138).
                OnHoverMove = _ => { if (Bar.OpenIndex.Peek() >= 0 && Bar.OpenIndex.Peek() != Index) { Bar.KeyboardOpen = false; Bar.OpenIndex.Value = Index; } },
                Children = [new TextEl(Title) { Size = 14f, Color = Tok.TextPrimary }],
            };
        }
    }
}
