using FluentGpu.Foundation;

namespace FluentGpu.Dsl;

/// <summary>
/// Lightweight template-part styling — the FluentGpu equivalent of CSS <c>::part</c> / WinUI lightweight styling,
/// signals-native. A control names its internal template parts (<c>public const string PartHeader = "Header";</c> …)
/// and routes each built part element through <see cref="TemplatePartsExtensions.Apply{T}"/>; app code layers ANY
/// element props onto any part with a <c>with</c> expression:
///
/// <code>
/// Parts = new()
/// {
///     [Expander.PartHeader] = b => b with
///     {
///         StickyTop = 8f,                                    // CSS position:sticky, top: 8px
///         OnPinned  = p => stuck.Value = p,                  // the :stuck observable
///         Fill = stuck.Value ? Tok.FillSolidBase : b.Fill,   // restyle ANYTHING off the signal
///         BrushTransitionMs = Motion.ControlFast,            // …and the swap cross-fades
///     },
/// }
/// </code>
///
/// Contract (apply order): control defaults → part modifier → the control RE-ASSERTS its mechanics-critical props
/// (click handlers, reflow specs, ref captures — chained via <see cref="Chain{T}"/>, never clobbered), so a modifier
/// can restyle everything but break nothing. Modifier rules: a PURE <c>with</c>-copy of its input; reading a signal
/// inside one subscribes the OWNING CONTROL (modifiers run inside its Render — that's the granular :stuck restyle
/// loop); never WRITE a signal in a modifier; no per-frame-hot reads (use <c>FillBind</c>/<c>TransformBind</c> for
/// those); type-preserving (a modifier that changes the record type is ignored — parts style, content SLOTS like
/// <c>Content</c>/<c>HeaderContent</c> restructure); avoid reshaping <c>Children</c> (structural reconcile).
///
/// List-UNIFORM (content-independent) item-chrome modifiers are cached apply-once via the parts epoch
/// (<see cref="TryApplyCached"/>); per-item CONTENT differences use the PartDelta value seam (SelectorVisuals.cs),
/// never a per-item modifier in a recycled scroll path.
/// </summary>
public sealed class TemplateParts
{
    private readonly Dictionary<string, Func<Element, Element>> _map = new(StringComparer.Ordinal);

    private int _epoch;
    /// <summary>Bumped on every modifier-map mutation; the apply-once prototype cache keys on it so an app changing a
    /// modifier invalidates the cached list-uniform prototype.</summary>
    public int Epoch => _epoch;

    private readonly Dictionary<(string, int), Element> _protoCache = new();

    /// <summary>Box parts — the common case, object-initializer friendly:
    /// <c>Parts = new() { [Expander.PartHeader] = b => b with { … } }</c>. Setting null removes the modifier.</summary>
    public Func<BoxEl, BoxEl>? this[string part]
    {
        set
        {
            if (value is null) { _map.Remove(part); _epoch++; }
            else { _map[part] = el => el is BoxEl b ? value(b) : el; _epoch++; }
        }
    }

    /// <summary>Any element type (a <see cref="TextEl"/> glyph part, an image…). Modifiers must be type-preserving.</summary>
    public void Set<T>(string part, Func<T, T> modify) where T : Element
    {
        _map[part] = el => el is T t ? modify(t) : el;
        _epoch++;
    }

    internal bool TryApply(string part, Element el, out Element result)
    {
        if (_map.TryGetValue(part, out var modify)) { result = modify(el); return true; }
        result = el;
        return false;
    }

    /// <summary>Apply a part modifier ONCE per (part, epoch) for a list-uniform (content-independent) modifier: every
    /// recycled row reuses the cached prototype instead of re-running the Func per item. SOUND ONLY for
    /// content-independent modifiers — per-item content differences MUST use the PartDelta value seam, never this.
    /// Invalidation key is (part, Epoch) only — a theme switch forces full reconstruction (theme is startup-resolved),
    /// so no theme-epoch term is needed.</summary>
    internal bool TryApplyCached(string part, Element prototype, out Element result)
    {
        if (!_map.ContainsKey(part)) { result = prototype; return false; }
        var key = (part, _epoch);
        if (_protoCache.TryGetValue(key, out var cached)) { result = cached; return true; }
        TryApply(part, prototype, out result);
        _protoCache[key] = result;
        return true;
    }

    /// <summary>Compose a control-internal handler with a modifier-supplied one (<c>OnRealized</c>/<c>OnPinned</c>):
    /// the control's runs first, the user's after. Collapses when the modifier left the control's own handler in
    /// place (the <c>with</c>-copy default) or supplied none.</summary>
    public static Action<T>? Chain<T>(Action<T>? control, Action<T>? user)
        => control is null ? user
         : user is null || ReferenceEquals(user, control) ? control
         : x => { control(x); user(x); };
}

public static class TemplatePartsExtensions
{
    /// <summary>Null-safe, type-stable apply: a no-op (no allocation) when <paramref name="parts"/> is null or the
    /// part has no modifier; keeps the ORIGINAL element if a modifier changed the record type (parts style, slots
    /// restructure). The control re-asserts its mechanics-critical props on the result.</summary>
    public static T Apply<T>(this TemplateParts? parts, string part, T el) where T : Element
        => parts is not null && parts.TryApply(part, el, out var modified) && modified is T typed ? typed : el;
}
