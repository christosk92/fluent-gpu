namespace FluentGpu.Foundation;

/// <summary>
/// The semantic role of a node, set by the control factories (a button IS a BoxEl, so the role is how it announces
/// itself). Carried on the input/a11y column; surfaced to the future UIA layer (ControlType), devtools, and UI tests.
/// Default <see cref="None"/> = a plain box/container.
/// </summary>
public enum AutomationRole : byte
{
    None = 0,
    Button,
    ToggleButton,
    Slider,
    ScrollBar,
    NavigationItem,
}
