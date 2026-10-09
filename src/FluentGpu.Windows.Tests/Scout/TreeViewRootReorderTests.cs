using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Windows.Tests.Scout;

/// <summary>
/// Shift+Alt+Down on a root row of a TreeView whose Roots is an array (the default reorder-enabled shape:
/// <c>TreeView.Create(new[] { ... })</c>) threw NotSupportedException out of the frame loop: the IList branch caught the
/// fixed-size array before the array branch and called RemoveAt on it. Array roots now move in place, and read-only
/// root lists (a collection expression) stay put instead of crashing.
/// </summary>
public sealed class TreeViewRootReorderTests
{
    private sealed class Root(IReadOnlyList<TreeNode> roots) : Component
    {
        public override Element Render() => new BoxEl
        {
            Direction = 1, Width = 320f, Height = 240f,
            Children = [TreeView.Create(roots)],
        };
    }

    private static void ShiftAltDownOnFirstRoot(IReadOnlyList<TreeNode> roots)
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("treeview-root-reorder", new Size2(320, 240), 1f));
        window.Show();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, new Root(roots));
        try
        {
            for (int i = 0; i < 3; i++) host.RunFrame();
            var scene = host.Scene;
            NodeHandle row = default;
            for (int i = 0; i < scene.Capacity && row.IsNull; i++)
            {
                var h = scene.HandleAt(i);
                if (!h.IsNull && scene.IsLive(h) && (scene.Flags(h) & NodeFlags.Focusable) != 0) row = h;
            }
            Assert.False(row.IsNull);
            host.Input.SetFocus(row);
            host.RunFrame();

            void Key(int vk, KeyModifiers mods)
            {
                window.QueueInput(new InputEvent(InputKind.Key, default, 0, vk, Mods: mods));
                for (int i = 0; i < 3; i++) host.RunFrame();
            }

            Key(Keys.Home, KeyModifiers.None);                       // focus root row 0 ("A")
            Key(Keys.Down, KeyModifiers.Shift | KeyModifiers.Alt);   // keyboard reorder: A one step forward
        }
        finally { host.Dispose(); app.Dispose(); }
    }

    [Fact]
    public void ArrayRoots_ShiftAltDown_MovesTheRootInPlace()
    {
        TreeNode a = new("A"), b = new("B"), c = new("C");
        var roots = new[] { a, b, c };
        ShiftAltDownOnFirstRoot(roots);
        Assert.Same(b, roots[0]);
        Assert.Same(a, roots[1]);
        Assert.Same(c, roots[2]);
    }

    [Fact]
    public void ReadOnlyRoots_ShiftAltDown_DoesNotThrow_AndLeavesTheOrder()
    {
        TreeNode a = new("A"), b = new("B");
        IReadOnlyList<TreeNode> roots = [a, b];   // the compiler's read-only list: IList<T> with IsReadOnly
        ShiftAltDownOnFirstRoot(roots);
        Assert.Same(a, roots[0]);
        Assert.Same(b, roots[1]);
    }
}
