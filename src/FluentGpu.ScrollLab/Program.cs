using FluentGpu;
using FluentGpu.ScrollLab.Lab;

namespace FluentGpu.ScrollLab;

static class Program
{
    // STA: the WindowsApi file pickers (tuning Import/Export) are STA-only coclasses (see the gallery's Program.cs).
    [STAThread]
    static void Main()
    {
        // Attach to the live host while the interactive loop continues (return false = do not take over the run).
        FluentApp.DiagnosticRun = (host, window, device) => { LabHost.Attach(host, window, device); return false; };
        FluentApp.FrameCompleted += LabHost.OnFrame;
        FluentApp.Run(() => new LabShell(), new AppOptions
        {
            Title = "Scroll Lab",
            Width = 1280,
            Height = 860,
            AdaptiveGpuPacing = false,   // a capture must see the raw cadence, never the governor's
            WarmCadenceMs = 0f,          // raw cadence for capture (plan §4): no post-input warm hold
        });
    }
}
