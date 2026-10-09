using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Rhi.Headless;
using FluentGpu.Signals;
using FluentGpu.Text.Headless;
using static FluentGpu.VerticalSlice.Harness.Gate;
using static FluentGpu.VerticalSlice.Harness.Asserts;

// ── NavigateRequest must re-request the key it last requested (Controls/NavigationView.cs) ──
//
// The request effect was deps-gated on the request string. The gallery shell re-requests with a ""-then-key pair (Back
// to a page the user left via the pane, or a search for the last-requested page), and both writes land in one render
// that reads the same key, so the gate stayed shut and the view kept the clicked page. The pins: the re-request selects
// again, and a later pane click is not reverted by the stale request (OnSelect reads a signal, as the shell's does).
static partial class ControlsSuite
{
    static void NavigateRequestRepeatChecks(StringTable strings)
    {
        using var app = new HeadlessPlatformApp();
        var window = new HeadlessWindow(new WindowDesc("nav-request-repeat", new Size2(1200, 700), 1f));
        window.Show();
        var req = new Signal<string>("");
        var visits = new Signal<int>(0);
        string selected = "";
        var nav = NavigationView.Create(new NavigationViewOptions
        {
            Initial = "home",
            Items = new[] { new NavItem("home", Icons.Home, "Home"), new NavItem("files", Icons.Folder, "Files") },
            Content = key => new TextEl("page:" + key) { Size = 16f, Color = Tok.TextPrimary },
            OnSelect = k => { selected = k; visits.Value = visits.Value + 1; },   // reads a signal, like the shell's history
            NavigateRequest = req,
        });
        using var host = new AppHost(app, window, new HeadlessGpuDevice(), new HeadlessFontSystem(strings), strings,
            new W0fStaticProbe { Build = () => nav });
        Settle(host);

        req.Value = ""; req.Value = "files"; Settle(host);           // first request
        string afterRequest = selected;
        var items = Roles(host.Scene, AutomationRole.NavigationItem);
        if (items.Count >= 2) ClickNode(host, window, items[0]);    // the user leaves via the pane
        Settle(host);
        string afterClick = selected;
        req.Value = ""; req.Value = "files"; Settle(host);           // re-request the same key (shell Back / re-search)
        string afterRepeat = selected;
        items = Roles(host.Scene, AutomationRole.NavigationItem);
        if (items.Count >= 2) ClickNode(host, window, items[0]);    // a later click must stick
        Settle(host);
        string afterSecondClick = selected;

        Check("controls.navview.navigate-request.repeat a \"\"-then-key write re-requests the key the request last held after a pane click, and a later click is not reverted by the stale request",
            items.Count >= 2 && afterRequest == "files" && afterClick == "home" && afterRepeat == "files" && afterSecondClick == "home",
            $"items={items.Count} request={afterRequest} click={afterClick} repeat={afterRepeat} click2={afterSecondClick}");
    }
}
