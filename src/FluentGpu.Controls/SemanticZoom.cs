using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;

namespace FluentGpu.Controls;

/// <summary>The two logical views owned by a <see cref="SemanticZoom"/>.</summary>
public enum SemanticZoomViewKind : byte
{
    ZoomedIn,
    ZoomedOut,
}

/// <summary>A semantic-zoom view and, when virtualized, its item-navigation handle.</summary>
public sealed record SemanticZoomView(Element Content, ItemsViewController? Items = null);

/// <summary>The detail and overview slots of a <see cref="SemanticZoom"/>.</summary>
public sealed record SemanticZoomSlots(
    SemanticZoomView ZoomedIn,
    SemanticZoomView ZoomedOut,
    TemplateParts? Parts = null);

/// <summary>One requested semantic view change. Invalid/unavailable item anchors are reported as -1.</summary>
public readonly record struct SemanticZoomViewChange(
    long OperationId,
    SemanticZoomViewKind From,
    SemanticZoomViewKind To,
    int SourceIndex,
    int DestinationIndex);

/// <summary>Options for <see cref="SemanticZoom.Create"/>.</summary>
public sealed record SemanticZoomOptions
{
    /// <summary>Controlled active view. Null gives the control an internal signal.</summary>
    public Signal<bool>? IsZoomedOut { get; init; }

    /// <summary>Maps a detail item to its overview item. Null is the identity mapping.</summary>
    public Func<int, int>? MapInToOut { get; init; }

    /// <summary>Maps an overview item to its detail item. Null is the identity mapping.</summary>
    public Func<int, int>? MapOutToIn { get; init; }

    public Action<SemanticZoomViewChange>? ViewChangeStarted { get; init; }
    public Action<SemanticZoomViewChange>? ViewChangeCompleted { get; init; }
    public SemanticZoomController? Controller { get; init; }
    public bool CanChangeViews { get; init; } = true;
}

/// <summary>Imperative semantic-zoom verbs. The controller is a mount-stable identity and is inert while detached.</summary>
public sealed class SemanticZoomController
{
    private object? _owner;
    private Action<int>? _toggle;
    private Action<int>? _zoomOut;
    private Action<int>? _zoomIn;

    public void ToggleActiveView(int sourceIndex = -1) => _toggle?.Invoke(sourceIndex);
    public void ZoomOutTo(int sourceIndex = -1) => _zoomOut?.Invoke(sourceIndex);
    public void ZoomInTo(int sourceIndex = -1) => _zoomIn?.Invoke(sourceIndex);

    internal void Attach(object owner, Action<int> toggle, Action<int> zoomOut, Action<int> zoomIn)
    {
        _owner = owner;
        _toggle = toggle;
        _zoomOut = zoomOut;
        _zoomIn = zoomIn;
    }

    internal void Detach(object owner)
    {
        if (!ReferenceEquals(_owner, owner)) return;
        _owner = null;
        _toggle = null;
        _zoomOut = null;
        _zoomIn = null;
    }
}

/// <summary>
/// A reusable two-level semantic zoom. Both views remain mounted through <see cref="Flow.KeepAlive{TKey}"/>;
/// after an active-view swap, the control parks the mapped item at the incoming viewport's leading edge in its first
/// layout effect and completes the operation after that committed presentation.
/// </summary>
public static class SemanticZoom
{
    public const string PartRoot = "Root";
    public const string PartZoomedIn = "ZoomedIn";
    public const string PartZoomedOut = "ZoomedOut";

    public static Element Create(SemanticZoomSlots slots, SemanticZoomOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(slots);
        ArgumentNullException.ThrowIfNull(slots.ZoomedIn);
        ArgumentNullException.ThrowIfNull(slots.ZoomedOut);
        return Embed.Comp(new Props(slots, options ?? new SemanticZoomOptions()),
            static () => new SemanticZoomCore());
    }

    internal sealed record Props(SemanticZoomSlots Slots, SemanticZoomOptions Options);
}

internal sealed class SemanticZoomCore : Component
{
    private sealed class PendingChange
    {
        public required SemanticZoomViewChange Change;
        public required SemanticZoomView Incoming;
        public bool RestoreFocus;
    }

    public override Element Render()
    {
        var props = UseProps<SemanticZoom.Props>();
        var input = UseContext(InputHooks.Current);
        var post = UsePost();
        var initial = props.Options.IsZoomedOut?.Peek() ?? false;
        var active = UseSignal(initial);
        var operation = UseRef(0L);
        var pending = UseRef<PendingChange?>(null);
        var latestProps = UseRef(props);
        var root = UseRef(NodeHandle.Null);
        var zoomedInRoot = UseRef(NodeHandle.Null);
        var zoomedOutRoot = UseRef(NodeHandle.Null);
        latestProps.Value = props;

        static SemanticZoomViewKind Kind(bool zoomedOut)
            => zoomedOut ? SemanticZoomViewKind.ZoomedOut : SemanticZoomViewKind.ZoomedIn;

        static bool Contains(SceneStore scene, NodeHandle ancestor, NodeHandle node)
        {
            if (ancestor.IsNull || node.IsNull || !scene.IsLive(ancestor) || !scene.IsLive(node)) return false;
            for (var n = node; !n.IsNull && scene.IsLive(n); n = scene.Parent(n))
                if (n == ancestor) return true;
            return false;
        }

        void CompleteIfLatest(PendingChange change)
        {
            if (pending.Value is not { } current || current.Change.OperationId != change.Change.OperationId) return;
            if (active.Peek() != (change.Change.To == SemanticZoomViewKind.ZoomedOut)) return;
            pending.Value = null;
            latestProps.Value.Options.ViewChangeCompleted?.Invoke(change.Change);

            if (!change.RestoreFocus) return;
            var incomingRoot = change.Change.To == SemanticZoomViewKind.ZoomedOut
                ? zoomedOutRoot.Value
                : zoomedInRoot.Value;
            var target = !incomingRoot.IsNull ? input.FirstFocusableIn?.Invoke(incomingRoot) ?? NodeHandle.Null : NodeHandle.Null;
            if (target.IsNull) target = root.Value;
            if (!target.IsNull) (input.RestoreFocus ?? (h => input.FocusNode?.Invoke(h, false))).Invoke(target);
        }

        void BeginChange(bool targetZoomedOut, int requestedSource, bool mirrorControlled)
        {
            var currentProps = latestProps.Value;
            if (!currentProps.Options.CanChangeViews || active.Peek() == targetZoomedOut) return;

            bool fromZoomedOut = active.Peek();
            var outgoing = fromZoomedOut ? currentProps.Slots.ZoomedOut : currentProps.Slots.ZoomedIn;
            var incoming = targetZoomedOut ? currentProps.Slots.ZoomedOut : currentProps.Slots.ZoomedIn;
            int source = requestedSource;
            if (source < 0 && outgoing.Items is { } outgoingItems
                && !outgoingItems.TryGetItemIndex(0.5f, 0f, out source))
                source = -1;

            int destination = source;
            if (source >= 0)
            {
                var map = targetZoomedOut ? currentProps.Options.MapInToOut : currentProps.Options.MapOutToIn;
                destination = map?.Invoke(source) ?? source;
                if (destination < 0) destination = -1;
            }

            var change = new SemanticZoomViewChange(
                ++operation.Value, Kind(fromZoomedOut), Kind(targetZoomedOut), source, destination);
            var focused = input.GetFocus?.Invoke() ?? NodeHandle.Null;
            var outgoingRoot = fromZoomedOut ? zoomedOutRoot.Value : zoomedInRoot.Value;
            bool restoreFocus = Context.Scene is { } scene && Contains(scene, outgoingRoot, focused);
            var next = new PendingChange
            {
                Change = change,
                Incoming = incoming,
                RestoreFocus = restoreFocus,
            };
            pending.Value = next;
            currentProps.Options.ViewChangeStarted?.Invoke(change);

            active.Value = targetZoomedOut;
            if (mirrorControlled && currentProps.Options.IsZoomedOut is { } external)
                external.Value = targetZoomedOut;
        }

        void Toggle(int source) => BeginChange(!active.Peek(), source, mirrorControlled: true);
        void ZoomOut(int source) => BeginChange(true, source, mirrorControlled: true);
        void ZoomIn(int source) => BeginChange(false, source, mirrorControlled: true);

        var controller = props.Options.Controller;
        UseEffect(() =>
        {
            if (controller is null) return null;
            controller.Attach(this, Toggle, ZoomOut, ZoomIn);
            return () => controller.Detach(this);
        }, DepKey.FromRef((object?)controller ?? this));

        // Direct writes to the controlled signal use the same operation/map/layout-effect anchoring path as controller
        // requests. The private signal keeps the KeepAlive key and the imperative controller on one source of truth.
        UseEffect(() =>
        {
            var external = latestProps.Value.Options.IsZoomedOut;
            if (external is null) return;
            bool requested = external.Value;
            if (requested != active.Peek()) BeginChange(requested, -1, mirrorControlled: false);
        });
        UseEffect(() =>
        {
            var external = latestProps.Value.Options.IsZoomedOut;
            if (external is not null && external.Peek() != active.Peek())
                BeginChange(external.Peek(), -1, mirrorControlled: false);
        }, DepKey.FromRef((object?)props.Options.IsZoomedOut ?? this));

        // Reading the active signal here subscribes this component as well as the KeepAlive structural boundary. The
        // structural queue swaps the key first; this render then schedules a phase-6.5 layout effect with valid incoming
        // bounds. BringIntoView therefore lands before the phase-7 animation tick and before the view's first record.
        _ = active.Value;
        long pendingOperation = pending.Value?.Change.OperationId ?? 0L;
        UseLayoutEffect(() =>
        {
            if (pending.Value is not { } change || change.Change.OperationId != pendingOperation) return;
            if (change.Change.DestinationIndex >= 0 && change.Incoming.Items is { } items)
                items.StartBringItemIntoView(change.Change.DestinationIndex, alignmentRatio: 0f, animate: false);
            // Posting observes the first committed presentation. If another request wins first, CompleteIfLatest
            // rejects this operation by id and only the newest request publishes completion/focus restoration.
            post(() => CompleteIfLatest(change));
        }, pendingOperation);

        LayoutTransition? Transition(object _, object to)
            => to is true ? MotionRecipes.SemanticZoomOut : MotionRecipes.SemanticZoomIn;

        Element View(bool zoomedOut)
        {
            var view = zoomedOut ? latestProps.Value.Slots.ZoomedOut : latestProps.Value.Slots.ZoomedIn;
            var part = zoomedOut ? SemanticZoom.PartZoomedOut : SemanticZoom.PartZoomedIn;
            var capture = zoomedOut
                ? (Action<NodeHandle>)(h => zoomedOutRoot.Value = h)
                : h => zoomedInRoot.Value = h;
            var box = new BoxEl
            {
                Key = zoomedOut ? "semantic-zoom-out" : "semantic-zoom-in",
                Grow = 1f,
                Basis = 0f,
                MinWidth = 0f,
                MinHeight = 0f,
                Children = [view.Content],
            };
            var styled = latestProps.Value.Slots.Parts.Apply(part, box);
            return styled with
            {
                Grow = 1f,
                Basis = 0f,
                MinWidth = 0f,
                MinHeight = 0f,
                OnRealized = TemplateParts.Chain(capture, styled.OnRealized),
                Children = styled.Children,
            };
        }

        var keepAlive = Flow.KeepAlive(
            () => active.Value,
            static zoomedOut => zoomedOut ? "zoomed-out" : "zoomed-in",
            View,
            new KeepAliveOptions(
                MaxEntries: 2,
                TransitionFor: Transition));

        void OnKey(KeyEventArgs e)
        {
            if (e.Handled || e.KeyCode != Keys.Escape || !active.Peek()) return;
            ZoomIn(-1);
            e.Handled = true;
        }

        var rootBox = new BoxEl
        {
            Grow = 1f,
            Basis = 0f,
            MinWidth = 0f,
            MinHeight = 0f,
            Focusable = true,
            TabStop = false,
            OnKeyDown = OnKey,
            Children = [keepAlive],
        };
        var rootStyled = props.Slots.Parts.Apply(SemanticZoom.PartRoot, rootBox);
        return rootStyled with
        {
            Grow = 1f,
            Basis = 0f,
            MinWidth = 0f,
            MinHeight = 0f,
            Focusable = true,
            TabStop = false,
            OnKeyDown = OnKey,
            OnRealized = TemplateParts.Chain<NodeHandle>(h => root.Value = h, rootStyled.OnRealized),
            Children = [keepAlive],
        };
    }
}
