using System;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>Page-published overrides for the shell masthead band (<c>ShellMastheadBand</c>). Owner-token'd exactly
/// like <see cref="ShellMaterialState"/>: on an animated swap the INCOMING page's activate-publish lands ~250ms
/// BEFORE the outgoing page's deactivate-clear, so a clear must be owner-gated or it blanks the new page's masthead
/// mid-read.</summary>
public sealed record ShellMastheadState(object? Owner, string? Title, string? Caption,
    bool ToolsVisible = false, bool ToolsLoading = false, Action? ToolsAction = null);

/// <summary>
/// The shell-owned, page-scoped MASTHEAD channel. The shell mounts ONE <c>ShellMastheadBand</c> above the
/// content card's KeepAlive boundary, and a page overrides its title/caption/tools by writing this signal while it
/// is the active, visible page — mirroring <see cref="ShellMaterial"/>'s owner-token contract.
/// </summary>
public static class ShellMasthead
{
    /// <summary>Context slot — the shell provides its masthead signal here; consumers read it with
    /// <c>UseContext(ShellMasthead.Slot)</c>. Null when no shell is mounted (e.g. headless tests), in which case a
    /// publisher simply no-ops and <c>ShellMastheadBand</c> falls back to the route-derived trail.</summary>
    public static readonly Context<Signal<ShellMastheadState?>?> Slot = new(null);
}
