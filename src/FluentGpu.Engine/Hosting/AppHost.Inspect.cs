using FluentGpu.Foundation;
using FluentGpu.Scene;
using FluentGpu.Text;

namespace FluentGpu.Hosting;

public sealed partial class AppHost
{
    /// <summary>The font system this host measures text with (the one passed to the constructor): DirectWrite in a real or
    /// offscreen host, the deterministic headless model otherwise. For inspection queries such as
    /// <see cref="TextOverflowQuery"/>; layout owns it, so call it on the UI thread between frames.</summary>
    public IFontSystem FontSystem => _fonts;

    /// <summary>Whether text node <paramref name="node"/> shows all of its content: <see cref="TextOverflow.Trimmed"/> when its
    /// width or line budget cut it (<see cref="TextOverflowQuery.Query"/> over this host's scene and font system). UI thread,
    /// between frames.</summary>
    public TextOverflow QueryTextOverflow(NodeHandle node) => TextOverflowQuery.Query(_scene, _fonts, node);

    /// <summary>True when text node <paramref name="node"/> was trimmed (see <see cref="QueryTextOverflow"/>); false for a
    /// non-text node.</summary>
    public bool IsTextTrimmed(NodeHandle node) => QueryTextOverflow(node).Trimmed;

    /// <summary>The glyph ink of text node <paramref name="node"/> as drawn, relative to its box (<see cref="TextOverflowQuery.TryInk"/>
    /// over this host's scene and font system): false under the headless text model or for a run with no ink. UI thread, between frames.</summary>
    public bool TryQueryTextInk(NodeHandle node, out RectF ink) => TextOverflowQuery.TryInk(_scene, _fonts, node, out ink);

    /// <summary>Append every live view slot (<see cref="FluentGpu.Theming.Slots.View{TProps}"/>) that resolved a template: its
    /// host's anchor node, the view key id and the template id, so an inspector can say which theme's template drew a subtree.
    /// A slot rendering nothing (no registry, an unregistered key) is left out. UI thread, between frames.</summary>
    public void CollectViewSlots(List<FluentGpu.Theming.ViewSlotInfo> into)
    {
        ArgumentNullException.ThrowIfNull(into);
        foreach (FluentGpu.Hooks.Component c in _reconciler.LiveComponents)
            if (c is FluentGpu.Theming.IViewSlotHost { ViewId: { } view, TemplateId: { } template } && !c.Context.AnchorNode.IsNull)
                into.Add(new FluentGpu.Theming.ViewSlotInfo(c.Context.AnchorNode, view, template));
    }
}
