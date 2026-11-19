using FluentGpu.Foundation;

namespace FluentGpu.Scene;

/// <summary>
/// What a frame changed of the STRUCTURE the parts declare: a form a size-reactive box chose (a grid's columns), where a multi-line run breaks
/// its lines. A window drag changes every width on every frame, and most of what follows a width (a card's cover growing with its column) is a
/// size the layout lands at once. Only a change of structure is a step: a body that gains a row, a heading that takes a second line. So on a
/// frame the window resized, the host starts or re-aims a resize animation (<see cref="LayoutTransition.AnimateDuringResize"/>) only for the parts
/// such a change lies under, or whose animation is already in flight; every other part follows the drag.
/// <para>Allocation-free once the list has grown: the host clears it every frame.</para>
/// </summary>
public sealed partial class SceneStore
{
    private readonly List<NodeHandle> _structureChanged = new(16);

    /// <summary>The nodes whose declared structure changed this frame (<see cref="NoteStructureChanged"/>), in the order noted.</summary>
    public List<NodeHandle> StructureChanged => _structureChanged;

    /// <summary>Set by the host: called when a node's declared structure changes, while the node and its subtree still hold the rects the frame
    /// laid them out at before the change (a size-reactive box notes its new form from its bounds callback, before it re-renders). The host
    /// captures there the parts under it that animate through a resize, so they move from where they were drawn.</summary>
    public Action<NodeHandle>? OnStructureChanged { get; set; }

    /// <summary>A part's declared structure changed (a form box chose another form, a run broke its lines elsewhere): see the type summary.
    /// Layout calls it for text; a component whose form changed calls it through its scene.</summary>
    public void NoteStructureChanged(NodeHandle node)
    {
        if (node.IsNull) return;
        _structureChanged.Add(node);
        OnStructureChanged?.Invoke(node);
    }
}
