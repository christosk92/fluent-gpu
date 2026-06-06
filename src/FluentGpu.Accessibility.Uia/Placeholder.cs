namespace FluentGpu.Accessibility.Uia;

/// <summary>
/// SCAFFOLD (the reference Windows a11y backend — see design/subsystems/input-a11y.md §11). Will project the retained
/// SceneStore to UI Automation via generated CCWs (IRawElementProviderSimple + Invoke/Value/Toggle/Text/Grid patterns,
/// collection relations, ITextRangeProvider), gated by <c>UiaClientsAreListening</c>, marshaled with UseComThreading.
/// </summary>
public static class UiaBackend;
