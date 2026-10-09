using System;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

// ── DatePicker flyout: the day column follows the spun month's day count (Controls/DatePicker.cs) ──
//
// The day column was a propless Embed.Comp, so the reused DateTimeLoopColumn kept the option list it mounted with: open
// on Jan 31 and spin to February and the column still listed 31 days and centered "31" while Accept silently committed
// Feb 28; open on Feb 10 and spin to March and the 29th–31st could never be reached. Under InvariantGlobalization the
// columns run Month | Day | Year and the focus trap lands on Month at open.
static partial class ControlsSuite
{
    static void DatePickerDayCountChecks(StringTable strings)
    {
        static void Run(StringTable strings, string name, DateOnly start, Action<HeadlessWindow, AppHost, Action<int>, Func<string, bool>, Signal<DateOnly?>> body)
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc(name, new Size2(480, 480), 1f)); window.Show();
            var date = new Signal<DateOnly?>(start);
            var clock = new ManualFrameTimeSource();
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
                new OverlayHost
                {
                    Child = new BoxEl
                    {
                        Width = 480f, Height = 480f, Direction = 1,
                        Children = [DatePicker.Create(date, minYear: 2000, maxYear: 2040)],
                    },
                }, frameTime: clock);
            void Settle() { for (int i = 0; i < 20; i++) { clock.Advance(16f); host.RunFrame(); } }
            void Press(int key) { window.QueueInput(new InputEvent(InputKind.Key, default, 0, key)); Settle(); }
            bool Shown(string t) => !FindTextNode(host.Scene, strings, host.Scene.Root, t).IsNull;
            Settle();
            ClickNode(host, window, FindRole(host.Scene, host.Scene.Root, AutomationRole.ComboBox));   // open the flyout
            Settle();
            body(window, host, Press, Shown, date);
        }

        // ── Jan 31 → February: the window is 24…28 / 1…4 around a re-clamped 28th, and Accept commits that 28th ──
        Run(strings, "datepicker-feb", new DateOnly(2025, 1, 31), (window, host, press, shown, date) =>
        {
            bool opened = shown("2029");   // the flyout's year column (2021…2029); the face shows only 2025
            press(Keys.Down);              // Month (focused at open): January → February
            bool febWindow = shown("24") && shown("28") && !shown("29") && !shown("30") && !shown("5");
            press(Keys.Enter);
            bool committed = date.Peek() == new DateOnly(2025, 2, 28);
            Check("gate.controls.datepicker-day-count-shrink spinning Jan 31 to February re-lists the day column with 28 days centered on the clamped 28th (it kept January's 31 and showed 31 while committing Feb 28)",
                opened && febWindow && committed,
                $"opened={opened} 24={shown("24")} 28={shown("28")} 29={shown("29")} 30={shown("30")} 5={shown("5")} date={date.Peek()}");
        });

        // ── Feb 10 → March: the day column grows to 31, so stepping up past the 1st wraps to the 31st ──
        Run(strings, "datepicker-mar", new DateOnly(2025, 2, 10), (window, host, press, shown, date) =>
        {
            press(Keys.Down);                              // Month: February → March
            press(Keys.Right);                             // focus the Day column
            for (int i = 0; i < 10; i++) press(Keys.Up);   // 10th → 1st → wraps to the month's last day
            press(Keys.Enter);
            bool committed = date.Peek() == new DateOnly(2025, 3, 31);
            Check("gate.controls.datepicker-day-count-grow spinning Feb 10 to March lets the day column reach the 31st (it stayed at February's 28 days, so the wrap landed on Mar 28)",
                committed, $"date={date.Peek()}");
        });
    }
}
