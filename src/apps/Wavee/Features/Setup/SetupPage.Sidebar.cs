using FluentGpu.Dsl;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using Wavee.Core.Sidebar;

namespace Wavee;

/// <summary>Page 5 · Sidebar (<c>data-step="5"</c>). The two shipped layouts apply live through
/// <c>SidebarPreferences.SwitchDesign</c>; Custom remains visible as a disabled “Coming soon” preview and exposes no
/// template/customizer controls until that design is ready.</summary>
sealed class SetupSidebarPage : Component
{
    public override Element Render()
    {
        var prefs = UseContext(SidebarPreferences.Slot);
        var svc = UseContext(Services.Slot);
        var settings = svc?.Settings;

        Element body = SetupRows.Stack(
            SetupRows.Lead(Loc.Get(Strings.Sidebar.Chooser.Subtitle)),
            SidebarDesignPicker.Row(prefs, settings, compact: true, allowCustom: false));

        return SetupPageHost.Frame(SetupPage.Sidebar, Loc.Get(Strings.Setup.Eyebrow.Sidebar),
            Loc.Get(Strings.Sidebar.Chooser.Title), body);
    }
}
