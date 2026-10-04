using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Media;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;

// ── gate.media.usevideo.* (L2-10: UseVideo opens from an effect, on the host's default backend) ─────────────────────────
// UseVideo used to call player.Play(next) from inside Render (a signal write during render) on a player whose router was
// empty, so it could only ever fail with NoBackend. It now resolves the host's default backend and opens from a passive
// effect keyed on the source. The probe component reads the backend's open count around its UseVideo call: an open
// started inside Render would show up there. The default registrar is process-static, so the check installs and clears
// it itself.
static partial class ControlsSuite
{
    sealed class UseVideoCountingBackend : IMediaBackend
    {
        public readonly List<string> Opened = new();
        public int Disposed;
        public MediaCapabilities Capabilities => new(true, false, false);
        public ValueTask<IMediaSession> OpenAsync(MediaSource source, MediaOpenOptions opts, CancellationToken ct)
        {
            lock (Opened) Opened.Add(((FileSource)source).Path);
            return new ValueTask<IMediaSession>(new UseVideoNullSession(this));
        }
        public int OpenCount { get { lock (Opened) return Opened.Count; } }
    }

    sealed class UseVideoNullSession(UseVideoCountingBackend owner) : IMediaSession
    {
        public void ConnectSignals(MediaSignalSink sink) { }
        public ValueTask PlayAsync() => ValueTask.CompletedTask;
        public ValueTask PauseAsync() => ValueTask.CompletedTask;
        public ValueTask SeekAsync(TimeSpan to, SeekMode mode) => ValueTask.CompletedTask;
        public void SetRate(double rate) { }
        public void SetVolume(double volume) { }
        public void SetMuted(bool muted) { }
        public VideoDelivery Video => VideoDelivery.None;
        public ValueTask DisposeAsync() { Interlocked.Increment(ref owner.Disposed); return ValueTask.CompletedTask; }
    }

    sealed class UseVideoProbe(UseVideoCountingBackend backend) : Component
    {
        public readonly Signal<string> Path = new("a.mp4");
        public readonly Signal<int> Tick = new(0);
        public int OpensDuringRender;
        public int Renders;

        public override Element Render()
        {
            _ = Tick.Value;   // an unrelated re-render must not reopen the source
            int before = backend.OpenCount;
            UseVideo(() => MediaSource.FromFile(Path.Value));
            OpensDuringRender += backend.OpenCount - before;
            Renders++;
            return new BoxEl { Width = 40f, Height = 40f };
        }
    }

    static void UseVideoEffectChecks(StringTable strings)
    {
        var backend = new UseVideoCountingBackend();
        MediaRouter.SetDefaultRegistrar(router => router.RegisterDefault(MediaKind.MfVideoOrFile, () => backend));
        try
        {
            using var app = new HeadlessPlatformApp();
            var window = new HeadlessWindow(new WindowDesc("usevideo-effect", new Size2(120, 120), 1f));
            window.Show();
            var probe = new UseVideoProbe(backend);
            using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings, probe);

            for (int i = 0; i < 6; i++) host.RunFrame();
            int firstOpens = backend.OpenCount;
            string first = firstOpens > 0 ? backend.Opened[0] : "";

            probe.Tick.Value = 1;   // a re-render with the SAME source: nothing reopens
            for (int i = 0; i < 6; i++) host.RunFrame();
            int afterRerender = backend.OpenCount;

            probe.Path.Value = "b.mp4";   // a real source change: exactly one more open, of the new source
            for (int i = 0; i < 6; i++) host.RunFrame();
            int afterChange = backend.OpenCount;
            string second = afterChange > 1 ? backend.Opened[1] : "";

            Check("gate.media.usevideo.opens-from-effect-on-default-backend the default backend opens the first source once, from the effect and never inside Render; a same-source re-render opens nothing; a source change opens exactly the new source",
                firstOpens == 1 && first == "a.mp4" && probe.OpensDuringRender == 0 && afterRerender == 1 && afterChange == 2 && second == "b.mp4" && probe.Renders >= 3,
                $"firstOpens={firstOpens}({first}) opensDuringRender={probe.OpensDuringRender} afterRerender={afterRerender} afterChange={afterChange}({second}) renders={probe.Renders}");
        }
        finally { MediaRouter.SetDefaultRegistrar(null); }
    }
}
