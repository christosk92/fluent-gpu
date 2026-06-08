using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A single top-level entry in a <see cref="MenuBar"/>: a clickable title (e.g. "File") whose
/// <paramref name="Items"/> populate the <see cref="MenuFlyout"/> that opens beneath it.</summary>
public sealed record MenuBarItem(string Title, IReadOnlyList<MenuFlyoutItem> Items);

/// <summary>A WinUI MenuBar: a horizontal bar of top-level menu titles (File, Edit, View, …). Clicking a title opens its
/// <see cref="MenuFlyout"/> below the title; re-clicking the open title closes it. Each title is its own little
/// component so it carries its own flyout anchor + overlay handle.</summary>
public sealed class MenuBar : Component
{
    public IReadOnlyList<MenuBarItem> Menus = [];

    public static Element Create(IReadOnlyList<MenuBarItem> menus)
        => Embed.Comp(() => new MenuBar { Menus = menus });

    public override Element Render()
    {
        var buttons = new Element[Menus.Count];
        for (int i = 0; i < Menus.Count; i++)
        {
            var m = Menus[i];
            buttons[i] = Embed.Comp(() => new MenuBarButton { Title = m.Title, Items = m.Items });
        }

        return new BoxEl
        {
            Direction = 0,
            Gap = 2f,
            AlignItems = FlexAlign.Center,
            Height = 40f,
            Children = buttons,
        };
    }

    /// <summary>One top-level title in the bar. Owns the anchor node + overlay handle so each menu opens/closes
    /// independently and toggles on re-click.</summary>
    internal sealed class MenuBarButton : Component
    {
        public string Title = "";
        public IReadOnlyList<MenuFlyoutItem> Items = [];

        public override Element Render()
        {
            var svc = UseContext(Overlay.Service);
            var anchor = UseRef<NodeHandle>(default);
            var handle = UseRef<OverlayHandle?>(null);

            void Toggle()
            {
                if (handle.Value is { IsOpen: true } o) { o.Close(); return; }
                handle.Value = svc.Open(
                    () => anchor.Value,
                    () => MenuFlyout.Build(Items, () => handle.Value?.Close()),
                    FlyoutPlacement.BottomLeft);
            }

            return new BoxEl
            {
                Direction = 0,
                AlignItems = FlexAlign.Center,
                Padding = new Edges4(11, 6, 11, 6),
                Corners = Radii.ControlAll,
                HoverFill = Tok.FillSubtleSecondary,
                PressedFill = Tok.FillSubtleTertiary,
                Role = AutomationRole.MenuItem,
                OnRealized = h => anchor.Value = h,
                OnClick = Toggle,
                Children = [new TextEl(Title) { Size = 14f, Color = Tok.TextPrimary }],
            };
        }
    }
}
