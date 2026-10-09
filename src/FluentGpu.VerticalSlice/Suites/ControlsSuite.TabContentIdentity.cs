using System.Collections.Generic;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;

// ── A tab switch must remount the new tab's content (Controls/TabView.cs, Controls/Pivot.cs) ──
//
// TabView and Pivot placed the selected tab's body UNKEYED in the content box, so the reconciler paired the old and new
// body by position and type. A same-type component per tab (an editor bound to its document) was reused: tab 2 showed
// tab 1's instance and state and its own factory never ran. The pins: each switch mounts the new tab's component, and a
// re-render of the SAME tab does not remount it.
static partial class ControlsSuite
{
    sealed class TabDocProbe : Component
    {
        public TabDocProbe(List<string> log, string doc) => log.Add(doc);   // the factory ran: a fresh instance for `doc`
        public override Element Render() => new BoxEl { Height = 20f };
    }

    static void TabContentIdentityChecks(StringTable strings)
    {
        // TabView: tab 0 carries an author Key, tabs 1 and 2 fall back to reference identity.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("tab-content-identity", new Size2(640, 240), 1f));
            window.Show();
            var log = new List<string>();
            var sel = new Signal<int>(0);
            var items = new TabViewItem[3];
            for (int i = 0; i < 3; i++)
            {
                string doc = "d" + i;
                items[i] = new TabViewItem
                {
                    Key = i == 0 ? "doc-0" : null,
                    Header = doc,
                    Content = () => Embed.Comp(() => new TabDocProbe(log, doc)),
                };
            }
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
                new W0fStaticProbe { Build = () => new BoxEl { Direction = 1, Grow = 1f, Children = [TabView.Create(items, sel)] } });
            Settle(host);
            string afterMount = string.Join(",", log);
            sel.Value = 1; Settle(host);
            sel.Value = 2; Settle(host);
            sel.Value = 0; Settle(host);
            string afterSwitches = string.Join(",", log);
            Check("controls.tabview.content.identity selecting a different tab mounts THAT tab's content (keyed by TabViewItem.Key, else the item instance) instead of reusing the previous tab's same-type component",
                afterMount == "d0" && afterSwitches == "d0,d1,d2,d0",
                $"mount={afterMount} switches={afterSwitches}");
        }

        // Pivot: one component type for every index; a parent re-render re-pushes props and re-renders the SAME item.
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("pivot-content-identity", new Size2(640, 240), 1f));
            window.Show();
            var log = new List<string>();
            var sel = new Signal<int>(0);
            var gen = new Signal<int>(0);
            string[] headers = ["a", "b", "c"];
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
                new W0fStaticProbe
                {
                    Build = () =>
                    {
                        int g = gen.Value;   // a parent re-render re-pushes fresh Pivot props (a new closure each time)
                        return new BoxEl
                        {
                            Direction = 1, Grow = 1f,
                            Children = [Pivot.Create(headers, i => Embed.Comp(() => new TabDocProbe(log, "p" + i + "@" + g)), selectedIndex: sel)],
                        };
                    },
                });
            Settle(host);
            gen.Value = 1; Settle(host);                // same item, re-rendered: must NOT remount
            string sameItem = string.Join(",", log);
            sel.Value = 1; Settle(host);
            sel.Value = 2; Settle(host);
            string afterSwitches = string.Join(",", log);
            Check("controls.pivot.content.identity a header switch mounts the new PivotItem's content, and a re-render of the same item keeps its instance",
                sameItem == "p0@0" && afterSwitches == "p0@0,p1@1,p2@1",
                $"same={sameItem} switches={afterSwitches}");
        }
    }
}
