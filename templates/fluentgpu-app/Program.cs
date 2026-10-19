using FluentGpu;

namespace FluentGpuApp;

static class Program
{
    // STA: the FluentGpu.WindowsApi file pickers and the shell/taskbar pillars are STA COM objects.
    [STAThread]
    static void Main()
    {
        FluentApp.Run(() => new App(), new AppOptions
        {
            Title = "FluentGpuApp",
            Width = 960,
            Height = 640,
            MinWidth = 480,
            MinHeight = 360,
            Mica = true,          // the DWM Mica backdrop shows through every transparent box
            CustomFrame = true,   // the app draws its own title bar (App's TitleBar), with engine caption buttons
        });
    }
}
