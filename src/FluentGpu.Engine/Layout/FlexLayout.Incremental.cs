namespace FluentGpu.Layout;

// P0 (Operation ultra-fast GPU engine): mechanical partial-class split so P4 can add the incremental-layout
// machinery (subtree-dirty walk, arranged-rect validity, the cross-pass measure/text-cache rings, arrange
// early-out) without editing FlexLayout.cs directly. Empty shell; P4 fills this in.
public sealed partial class FlexLayout
{
}
