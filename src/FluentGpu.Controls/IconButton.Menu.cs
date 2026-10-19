using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

public static partial class IconButton
{
    /// <summary>
    /// An icon-only button that opens a <see cref="MenuFlyout"/> — the "⋯" / settings / account menu of a toolbar or a
    /// small tool window. It is <see cref="DropDownButton"/>'s open/close mechanics on the <c>IconButton.Create</c>
    /// chrome (no label, no chevron — WinUI's AppBarButton-with-Flyout shape):
    /// <list type="bullet">
    /// <item><paramref name="items"/> runs at OPEN time, every time, so the rows read live state (a checked toggle, an
    /// enabled flag). A result with no enabled, non-separator row opens nothing.</item>
    /// <item>Opens with <see cref="PopupOptions.Menu"/>: focus trap, light dismiss, and windowed-when-escaping — a menu
    /// taller than a small window leases a popup window instead of clipping.</item>
    /// <item>Re-click closes. The light-dismiss scrim closes the menu on the pointer DOWN and the release never becomes a
    /// click on the button (the press owner is the scrim), so a re-click cannot close-and-reopen. A click that lands
    /// during the 83 ms close fade opens a fresh menu; only the newest handle ever clears the handle cell.</item>
    /// <item>Keyboard: Down / F4 open, and Enter / Space activate it while it holds focus; a keyboard open focuses the
    /// first row and puts the keyboard cursor on it (WinUI keyboard-invoked menu). A pointer open moves focus into the
    /// menu without the cursor highlight, exactly as DropDownButton does. The button never takes pointer focus
    /// (AppBarButton AllowFocusOnInteraction=False), so "focused at activation" is the keyboard signal.</item>
    /// <item><paramref name="toolTip"/> wraps the button in <see cref="ToolTip.Wrap(Element, string, float, float)"/>
    /// (omitted when null or empty).</item>
    /// </list>
    /// Role = Button (the overlay raises ExpandCollapse Collapsed↔Expanded, as for DropDownButton).
    /// Needs an <see cref="OverlayHost"/> ancestor.
    /// </summary>
    public static Element Menu(string glyph, Func<IReadOnlyList<MenuFlyoutItem>> items,
        FlyoutPlacement placement = FlyoutPlacement.BottomEdgeAlignedRight,
        string? toolTip = null, ControlSize size = ControlSize.Medium, bool isEnabled = true)
    {
        Element button = Embed.Comp(new MenuProps(glyph, items, placement, size, isEnabled), static () => new IconMenuButtonCore());
        return toolTip is { Length: > 0 } tip ? ToolTip.Wrap(button, tip) : button;
    }

    /// <summary>Props RE-PUSHED to <see cref="IconMenuButtonCore"/> (a parent re-render delivers a new items factory /
    /// enabled flag — nothing freezes at mount).</summary>
    internal sealed record MenuProps(string Glyph, Func<IReadOnlyList<MenuFlyoutItem>> Items, FlyoutPlacement Placement,
                                     ControlSize Size, bool IsEnabled);

    /// <summary>True when <paramref name="items"/> has at least one enabled, non-separator row — otherwise a menu would
    /// open with nothing to choose (the <see cref="ContextMenu.HasAnyEnabled"/> rule).</summary>
    internal static bool HasAnyEnabled(IReadOnlyList<MenuFlyoutItem>? items)
    {
        if (items is null) return false;
        for (int i = 0; i < items.Count; i++)
            if (!items[i].IsSeparator && items[i].Enabled) return true;
        return false;
    }
}

/// <summary>The stateful core behind <see cref="IconButton.Menu"/>: the anchor capture, the handle cell, the toggle and
/// the open chords — DropDownButton's logic (DropDownButton.cs) over the IconButton chrome.</summary>
internal sealed class IconMenuButtonCore : Component
{
    public override Element Render()
    {
        var p = UseProps<IconButton.MenuProps>();
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var focused = UseRef(false);
        var svc = UseContext(Overlay.Service);
        var itemsFactory = p.Items;
        var placement = p.Placement;

        void Open(bool keyboard)
        {
            var list = itemsFactory();
            if (!IconButton.HasAnyEnabled(list)) return;   // nothing to choose ⇒ nothing opens
            OverlayHandle? mine = null;
            var opened = svc.Open(
                () => anchor.Value,
                () => MenuFlyout.Build(list, () => mine?.Close(), focusFirst: keyboard),
                placement,
                PopupOptions.Menu);
            mine = opened;
            handle.Value = opened;
            // A reopen during this menu's close fade owns the cell now: only the current handle clears it.
            opened.ClosedAction = () => { if (ReferenceEquals(handle.Value, opened)) handle.Value = null; };
        }

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } h) { h.Close(); return; }
            // Enter/Space reach OnClick only on the focused button; a pointer click never focuses it.
            Open(keyboard: focused.Value);
        }

        Action<NodeHandle> anchorCapture = h => anchor.Value = h;
        Action<bool> onFocus = f => focused.Value = f;
        // Down / F4 open (WinUI DropDownButton + ComboBox-family open chords), focusing the first row.
        Action<KeyEventArgs> onKey = a =>
        {
            if ((a.KeyCode == Keys.Down || a.KeyCode == Keys.F4) && handle.Value is not { IsOpen: true })
            {
                Open(keyboard: true);
                a.Handled = true;
            }
        };

        var root = IconButton.Create(p.Glyph, Toggle, style: null, isEnabled: p.IsEnabled, parts: null, size: p.Size);
        return root with
        {
            OnRealized = TemplateParts.Chain(anchorCapture, root.OnRealized),
            OnKeyDown = onKey,
            OnFocusChanged = onFocus,
        };
    }
}
