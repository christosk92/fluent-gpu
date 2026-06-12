namespace FluentGpu.WindowsApi.Shell;

/// <summary>
/// The taskbar button's progress-indicator mode — the public, framework-neutral spelling of the shell's
/// <c>TBPFLAG</c> (<c>ShObjIdl_core.h</c>). <see cref="TaskbarManager.SetProgressState"/> maps these one-to-one to the
/// underlying <c>TBPF_*</c> values, so callers do not reference TerraFX/Win32 enums directly.
/// </summary>
/// <remarks>
/// <para><b>Determinate vs not.</b> <see cref="Normal"/>, <see cref="Error"/>, and <see cref="Paused"/> draw the bar at
/// the fraction last set by <see cref="TaskbarManager.SetProgress"/> (green / red / yellow respectively).
/// <see cref="Indeterminate"/> shows the marquee/cycling animation and ignores the fraction. <see cref="None"/> clears
/// the indicator entirely.</para>
/// <para>Mapping (<c>ShObjIdl_core.h</c>): <c>None→TBPF_NOPROGRESS (0)</c>, <c>Indeterminate→TBPF_INDETERMINATE (1)</c>,
/// <c>Normal→TBPF_NORMAL (2)</c>, <c>Error→TBPF_ERROR (4)</c>, <c>Paused→TBPF_PAUSED (8)</c>.</para>
/// </remarks>
public enum TaskbarProgressState
{
    /// <summary>No progress indicator (clears the bar). Maps to <c>TBPF_NOPROGRESS</c>.</summary>
    None = 0,

    /// <summary>Cycling/marquee animation for indeterminate work (the fraction is ignored). Maps to <c>TBPF_INDETERMINATE</c>.</summary>
    Indeterminate = 1,

    /// <summary>Determinate green progress at the set fraction. Maps to <c>TBPF_NORMAL</c>.</summary>
    Normal = 2,

    /// <summary>Determinate red progress (error) at the set fraction. Maps to <c>TBPF_ERROR</c>.</summary>
    Error = 4,

    /// <summary>Determinate yellow progress (paused) at the set fraction. Maps to <c>TBPF_PAUSED</c>.</summary>
    Paused = 8,
}
