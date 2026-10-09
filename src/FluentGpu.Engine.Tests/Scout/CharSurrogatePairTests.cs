using System.Collections.Generic;
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

namespace FluentGpu.Engine.Tests.Scout;

/// <summary>
/// WM_CHAR delivers UTF-16 code units: an emoji typed through SendInput/VK_PACKET arrives as a high then a low surrogate.
/// OnCharInput promises a codepoint (every text control feeds it to char.ConvertFromUtf32, which throws on a lone
/// surrogate and took down the UI thread), so the dispatcher must join the pair and drop an orphaned half.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class CharSurrogatePairTests
{
    private sealed class Root : Component
    {
        public readonly List<int> Codepoints = new();
        public NodeHandle Node;

        public override Element Render() => new BoxEl
        {
            Width = 200f, Height = 40f, Focusable = true,
            OnRealized = h => Node = h,
            OnCharInput = e =>
            {
                Codepoints.Add(e.Codepoint);
                _ = char.ConvertFromUtf32(e.Codepoint);   // what EditableText/ComboBox/ItemsView/MenuFlyout do
                e.Handled = true;
            },
        };
    }

    private static void Run(int[] units, int[] expected)
    {
        var strings = new StringTable();
        var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("char-surrogate", new Size2(320, 240), 1f));
        window.Show();
        var root = new Root();
        var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, root);
        try
        {
            for (int i = 0; i < 2; i++) host.RunFrame();
            Assert.False(root.Node.IsNull);
            host.Input.SetFocus(root.Node);
            foreach (int u in units) window.QueueInput(new InputEvent(InputKind.Char, default, 0, u));
            host.RunFrame();
            Assert.Equal(expected, root.Codepoints.ToArray());
        }
        finally { host.Dispose(); app.Dispose(); }
    }

    [Fact]
    public void ASurrogatePair_ReachesTheHandler_AsOneCodepoint()
        => Run([0xD83D, 0xDE00], [0x1F600]);   // 😀

    [Fact]
    public void ALoneLowSurrogate_IsDropped_AndAStrandedHighHalf_DoesNotEatTheNextChar()
        => Run([0xDE00, 'a', 0xD83D, 'b'], ['a', 'b']);
}
