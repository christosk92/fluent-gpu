using System;
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

// ── A re-keyed ItemsView over an app-owned SelectionModel must take its forwarder off the model (Controls/ItemsView.cs) ──
//
// ItemsView forwards model.SelectionChanged to its OnChange. An app that passes a long-lived model through
// ListOptions.Selection and re-keys the list (Track.Table's filterKey) remounts the view while the model lives on. The
// forwarder was added during render and never removed, so the model pinned every dead ItemsView and one selection change
// raised OnChange once per mount so far. The pins: three re-keys leave ONE forwarder (one OnChange per change), and
// unmounting the list leaves none.
static partial class ControlsSuite
{
    sealed class SelectionUnsubscribeProbe : Component
    {
        public readonly SelectionModel Model = new();
        public readonly Signal<int> Generation = new(0);
        public readonly Signal<bool> Show = new(true);
        public int OnChangeCalls;
        Action? _onChange;

        public override Element Render()
        {
            int gen = Generation.Value;
            bool show = Show.Value;
            _onChange ??= () => OnChangeCalls++;
            return new BoxEl
            {
                Direction = 1, Width = 300f, Height = 200f,
                Children = show
                    ? [ItemsView.Create(5, static _ => new BoxEl { Height = 20f }, RepeatLayout.Stack(20f),
                          new ListOptions { Selection = Model, OnChange = _onChange }) with { Key = "sel-unsub:" + gen }]
                    : [],
            };
        }
    }

    static void SelectionUnsubscribeChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("selection-unsubscribe", new Size2(400, 300), 1f));
        window.Show();
        var probe = new SelectionUnsubscribeProbe();
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);
        Settle(host);

        for (int i = 1; i <= 3; i++) { probe.Generation.Value = i; Settle(host); }   // three remounts over ONE model
        int forwarders = probe.Model.SelectionChanged?.GetInvocationList().Length ?? 0;
        probe.Model.Select(1);
        int calls = probe.OnChangeCalls;

        probe.Show.Value = false;                                                      // unmount the list entirely
        Settle(host);
        int forwardersAfterUnmount = probe.Model.SelectionChanged?.GetInvocationList().Length ?? 0;
        probe.Model.Select(2);
        int callsAfterUnmount = probe.OnChangeCalls - calls;

        Check("gate.itemsview.selection.unsubscribe a re-keyed ItemsView over an app-owned SelectionModel keeps ONE forwarder on the model (one OnChange per change after 3 remounts) and leaves none once unmounted",
            forwarders == 1 && calls == 1 && forwardersAfterUnmount == 0 && callsAfterUnmount == 0,
            $"forwarders={forwarders} calls={calls} forwardersAfterUnmount={forwardersAfterUnmount} callsAfterUnmount={callsAfterUnmount}");
    }
}
