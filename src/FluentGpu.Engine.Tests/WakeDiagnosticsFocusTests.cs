using System.Text;
using FluentGpu.Animation;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Scene;
using FluentGpu.Signals;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>The <c>[wake]</c> census's focus split: awake frames run while the window is NOT active are counted apart
/// from the rest, so "it gets slow when I click away" reads as two frame rates in the always-on log.</summary>
public sealed class WakeDiagnosticsFocusTests
{
    [Fact]
    public void FocusSplit_CountsOnlyAwakeFramesRunWhileTheWindowIsInactive()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        bool active = true;
        var diag = new WakeDiagnostics(new Signal<object?>(null), new AnimEngine(scene), scene, static _ => { }, () => active);

        for (int i = 0; i < 3; i++) diag.Record(WakeReasons.Timer, awake: true, rendered: true, reconciled: false, laidOut: false, minimized: false);
        active = false;
        for (int i = 0; i < 5; i++) diag.Record(WakeReasons.Timer, awake: true, rendered: true, reconciled: false, laidOut: false, minimized: false);
        diag.Record(WakeReasons.None, awake: false, rendered: false, reconciled: false, laidOut: false, minimized: false);   // an idle wake is not a frame
        active = true;
        diag.Record(WakeReasons.Timer, awake: true, rendered: true, reconciled: false, laidOut: false, minimized: false);

        Assert.Equal(5, diag.FramesInactiveForTest);
        var sb = new StringBuilder();
        diag.AppendFocusSplit(sb);
        string line = sb.ToString();
        Assert.StartsWith(" | focus act=", line);
        Assert.Contains("fps/", line);
        Assert.Contains(" inact=", line);
    }
}
