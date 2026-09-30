using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;

namespace FluentGpu.ScrollLab.Lab;

/// <summary>
/// The lab's global hotkeys, as keyboard ACCELERATORS on always-visible toolbar buttons (the engine's accelerator path
/// runs after focused routing leaves the chord unhandled — so they work while the list, a slider or a number box has
/// focus): F8 marks "felt wrong", F10 toggles recording, Ctrl+T toggles the in-window tuning pane, Ctrl+Shift+T opens
/// the detached tuning window. Phase 2 adds
/// F9 (A/B flip) and 1/2/0 (votes).
/// </summary>
public static class Hotkeys
{
    public static readonly KeyAccelerator FeltWrong = new(Keys.F8, KeyModifiers.None);
    public static readonly KeyAccelerator Record = new(Keys.F10, KeyModifiers.None);
    public static readonly KeyAccelerator TuningPane = new(Keys.T, KeyModifiers.Ctrl);
    public static readonly KeyAccelerator TuningWindow = new(Keys.T, KeyModifiers.Ctrl | KeyModifiers.Shift);

    public const string Hint = "F8 felt wrong   F10 record/stop   Ctrl+T tuning pane   Ctrl+Shift+T tuning window";

    /// <summary>A toolbar button that also answers <paramref name="accelerator"/>.</summary>
    public static BoxEl Button(string label, System.Action onClick, KeyAccelerator accelerator, ButtonAppearance appearance = ButtonAppearance.Standard)
        => Controls.Button.Create(label, onClick, appearance, ControlSize.Small) with { Accelerator = accelerator };

    public static void ToggleTuningPane() => LabState.TuningPaneOpen.Value = !LabState.TuningPaneOpen.Peek();
}
