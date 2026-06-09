using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;

namespace FluentGpu.Controls;

/// <summary>A WinUI <c>Flyout</c>: a button that opens an anchored, light-dismissable flyout holding arbitrary content
/// (not a menu). Opens through the shared overlay service (<see cref="OverlayHost"/>) exactly like
/// <see cref="DropDownButton"/>; the host wraps the body in the acrylic <c>FlyoutSurface</c> (the
/// FlyoutPresenterBackground acrylic + 1px FlyoutBorderThemeBrush stroke + elevation shadow + OverlayCornerRadius corners +
/// the MenuPopupThemeTransition reveal), and captures/restores focus on open/close. The body returned by the content thunk
/// is the WinUI <c>FlyoutPresenter</c> card: <c>FlyoutContentPadding</c> 16,15,16,17 with the Flyout*Theme min/max
/// constraints. Toggling re-click closes it (WinUI Flyout toggle semantics). Light dismiss + Escape are host-provided.</summary>
public sealed class FlyoutButton : Component
{
    public string Label = "";
    public Func<Element> Content = () => new BoxEl();
    public bool OpenOnMount;   // deterministic visual-shot hook: open the real flyout after first mount

    /// <summary>Placement of the flyout relative to its anchor. Defaults to <see cref="FlyoutPlacement.BottomLeft"/>
    /// (the positioner flips above the anchor when it does not fit below).</summary>
    public FlyoutPlacement Placement = FlyoutPlacement.BottomLeft;

    // WinUI FlyoutPresenter_themeresources.xaml DefaultFlyoutPresenterStyle:
    //   Padding   = FlyoutContentPadding 16,15,16,17
    //   MinWidth  = FlyoutThemeMinWidth  96
    //   MaxWidth  = FlyoutThemeMaxWidth  456
    //   MinHeight = FlyoutThemeMinHeight 40   (generic_perf2026.xaml:52)
    //   MaxHeight = FlyoutThemeMaxHeight 758
    // Background/BorderBrush/BorderThickness/CornerRadius (AcrylicInAppFillColorDefault / SurfaceStrokeColorFlyout /
    // 1px / OverlayCornerRadius 8) are supplied by the shared FlyoutSurface, so the presenter card here is transparent
    // chrome over that surface — it owns only the content padding + size constraints.
    static readonly Edges4 FlyoutContentPadding = new(16f, 15f, 16f, 17f);
    const float FlyoutThemeMinWidth = 96f, FlyoutThemeMaxWidth = 456f, FlyoutThemeMinHeight = 40f, FlyoutThemeMaxHeight = 758f;

    public static Element Create(string label, Func<Element> content)
        => Embed.Comp(() => new FlyoutButton { Label = label, Content = content });

    public static Element Create(string label, Func<Element> content, FlyoutPlacement placement)
        => Embed.Comp(() => new FlyoutButton { Label = label, Content = content, Placement = placement });

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);
        var placement = Placement;
        var content = Content;
        var autoOpened = UseRef(false);

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } h) { h.Close(); return; }
            handle.Value = svc.Open(
                () => anchor.Value,
                () => Presenter(content),
                placement,
                // WinUI Flyout: light dismiss (click-outside + Escape) + focus moves into the flyout on open and is
                // restored to the invoker on close. No focus trap (Tab can leave; FlyoutShowMode_Auto). Both handled
                // by OverlayHost (DismissBehavior.LightDismiss is the default; SavedFocus capture/restore is host-wired).
                new PopupOptions(FocusTrap: false, DismissBehavior: DismissBehavior.LightDismiss));
        }

        UseEffect(() =>
        {
            if (!OpenOnMount || autoOpened.Value) return;
            autoOpened.Value = true;
            Toggle();
        }, OpenOnMount);

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Gap = 8f,
            MinHeight = 32f,
            Padding = new Edges4(11, 5, 11, 6),
            Corners = Radii.ControlAll,
            BorderWidth = 1f,
            BorderBrush = Tok.ControlElevationBorder,
            Fill = Tok.FillControlDefault,
            HoverFill = Tok.FillControlSecondary,
            PressedFill = Tok.FillControlTertiary,
            ClipToBounds = true,
            Role = AutomationRole.Button,
            OnRealized = h => anchor.Value = h,
            OnClick = Toggle,
            Children = [new TextEl(Label) { Size = 14f, Color = Tok.TextPrimary }],
        };
    }

    // The FlyoutPresenter card: transparent over the FlyoutSurface acrylic/stroke/shadow/corner chrome, contributing the
    // 16,15,16,17 content padding and the Flyout*Theme size constraints (1:1 with DefaultFlyoutPresenterStyle).
    static Element Presenter(Func<Element> content) => new BoxEl
    {
        Direction = 1,
        Fill = ColorF.Transparent,
        Padding = FlyoutContentPadding,
        MinWidth = FlyoutThemeMinWidth,
        MaxWidth = FlyoutThemeMaxWidth,
        MinHeight = FlyoutThemeMinHeight,
        MaxHeight = FlyoutThemeMaxHeight,
        Children = [content()],
    };
}
