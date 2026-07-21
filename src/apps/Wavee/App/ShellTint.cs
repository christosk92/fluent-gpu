using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>The published Mica-tint state: the colour to wash the passthrough region with (null ⇒ plain Mica) plus an
/// OWNER token. The owner makes nav transitions race-free — a page clears the tint only if it is still the owner, so
/// "park old page + activate new page" lands on the new page's colour regardless of which effect fires first.</summary>
public readonly record struct ShellTintState(ColorF? Color, object? Owner);

/// <summary>
/// The shell-owned, page-scoped Mica-tint channel. The shell publishes one <see cref="Signal{T}"/> at the root and
/// paints a translucent scrim quad over the Mica-passthrough region from it (null ⇒ plain Mica). A detail page sets it
/// to its art colour while it is the active, visible page and clears it on park / unmount — so the window's translucent
/// chrome carries the album/playlist colour and reverts when you navigate away. This is the canon-blessed "tinted Mica"
/// pattern (design/subsystems/window-backdrop-mica.md §"tinted Mica"): DWM owns the system-Mica tint and exposes no
/// recolour API, so we do NOT recolour Mica — we let it show through a low-alpha scrim. No native interop, no GPU pass.
/// </summary>
public static class ShellTint
{
    /// <summary>Context slot — the shell provides its tint signal here; consumers read it with
    /// <c>UseContext(ShellTint.Slot)</c>. Null when no shell is mounted (e.g. headless tests), in which case a
    /// consumer simply no-ops.</summary>
    public static readonly Context<Signal<ShellTintState>?> Slot = new(null);
}
