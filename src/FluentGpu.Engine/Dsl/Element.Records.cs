namespace FluentGpu.Dsl;

// PERF r3 MODEL M4b: what a node RECORD drops (Reconciler/NodeRecords.cs): the element-valued members the reconciler reads from
// the scene instead (a container's children, a scroll viewer's content), so a record never points at an element tree.
public partial struct BoxEl { internal void DropTree() => this = this with { Children = default }; }
public partial struct GridEl { internal void DropTree() => this = this with { Children = default }; }
public partial struct ScrollEl { internal void DropTree() => this = this with { Content = default }; }
