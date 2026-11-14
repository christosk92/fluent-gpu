using Bench.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace WinUIBench;

/// <summary>
/// The WinUI half of the desktop-driven scenarios (window-resize, theme-switch). The driver itself is the shared
/// <see cref="LiveWindowDriver"/>; this file only builds the same content the FluentGPU host builds.
/// </summary>
internal sealed partial class BenchmarkWindow
{
    private readonly SolidColorBrush _sizeProbeW = new(Windows.UI.Color.FromArgb(255, 16, 16, 16));
    private readonly SolidColorBrush _sizeProbeH = new(Windows.UI.Color.FromArgb(255, 16, 16, 16));

    /// <summary>
    /// window-resize root: a Grid that fills the window - rows 64 / * / 6, columns * / 6 - holding the size probes, the
    /// responsive detail page, the right edge strip and the bottom edge strip. The strips are layout children, so they sit
    /// on the edge of whatever size the content was laid out at. The probes paint that size from SizeChanged, which XAML
    /// raises after the layout pass and before the frame is rendered. Mirrors FluentBenchState.BuildResizeRoot.
    /// </summary>
    private FrameworkElement BuildResizeRoot()
    {
        var strip = new SolidColorBrush(Windows.UI.Color.FromArgb(255,
            BenchWorkload.ResizeEdgeR, BenchWorkload.ResizeEdgeG, BenchWorkload.ResizeEdgeB));
        var root = new Grid { Background = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0x24, 0x21, 0x1C)) };
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(BenchWorkload.ResizeProbeRowHeight) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(BenchWorkload.ResizeEdgeStrip) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        root.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(BenchWorkload.ResizeEdgeStrip) });

        var probes = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Padding = new Thickness(BenchWorkload.ResizeProbeMargin),
            Spacing = BenchWorkload.ResizeProbeMargin,
        };
        probes.Children.Add(new Border { Width = BenchWorkload.ResizeProbeSize, Height = BenchWorkload.ResizeProbeSize, Background = _sizeProbeW });
        probes.Children.Add(new Border { Width = BenchWorkload.ResizeProbeSize, Height = BenchWorkload.ResizeProbeSize, Background = _sizeProbeH });
        Grid.SetColumnSpan(probes, 2);
        root.Children.Add(probes);

        var page = (FrameworkElement)BuildDetailPage(0, resizable: true);
        Grid.SetRow(page, 1);
        root.Children.Add(page);

        var right = new Border { Background = strip };
        Grid.SetRow(right, 1);
        Grid.SetColumn(right, 1);
        root.Children.Add(right);

        var bottom = new Border { Background = strip };
        Grid.SetRow(bottom, 2);
        Grid.SetColumnSpan(bottom, 2);
        root.Children.Add(bottom);

        root.SizeChanged += (_, e) =>
        {
            FrameIdProbe.Encode((int)Math.Round(e.NewSize.Width), out byte r, out byte g, out byte b);
            _sizeProbeW.Color = Windows.UI.Color.FromArgb(255, r, g, b);
            FrameIdProbe.Encode((int)Math.Round(e.NewSize.Height), out r, out g, out b);
            _sizeProbeH.Color = Windows.UI.Color.FromArgb(255, r, g, b);
        };
        return root;
    }

    /// <summary>theme-switch content: the detail page (built once) in the shared 1200 x 720 frame.</summary>
    private FrameworkElement BuildThemeHost() => new ContentControl
    {
        Width = BenchWorkload.WindowWidth,
        Height = BenchWorkload.WindowHeight,
        HorizontalContentAlignment = HorizontalAlignment.Stretch,
        VerticalContentAlignment = VerticalAlignment.Stretch,
        ContentTransitions = null,
        Content = BuildDetailPage(0),
    };

    /// <summary>
    /// Makes the theme-switch tree theme-aware the WinUI way: every surface and text colour comes from a Style whose
    /// setter is a <c>{ThemeResource}</c> (App.xaml ThemeDictionaries), so a <c>RequestedTheme</c> change re-resolves
    /// it. The page builder paints with fixed brushes (the navigation scenario's), so each fixed brush is swapped for the
    /// matching style here, once, before the window is shown.
    /// </summary>
    private void ApplyThemeAwareness(FrameworkElement root)
    {
        Style rootStyle = Resource<Style>("BenchThemeRootStyle");
        Style card = Resource<Style>("BenchThemeCardStyle");
        Style thumb = Resource<Style>("BenchThemeThumbStyle");
        Style accent = Resource<Style>("BenchThemeAccentStyle");
        Style primary = Resource<Style>("BenchThemeTextPrimaryStyle");
        Style secondary = Resource<Style>("BenchThemeTextSecondaryStyle");
        Style tertiary = Resource<Style>("BenchThemeTextTertiaryStyle");
        var grid = (Grid)root;
        grid.ClearValue(Panel.BackgroundProperty);
        grid.Style = rootStyle;
        grid.RequestedTheme = ElementTheme.Dark;
        int swapped = 0;
        Walk(root);
        if (swapped == 0) throw new InvalidOperationException("theme-switch: no element was made theme-aware.");

        void Walk(DependencyObject node)
        {
            switch (node)
            {
                case Border border when border.Background is { } bg:
                    Style? s = ReferenceEquals(bg, _navCardBrush) ? card
                        : ReferenceEquals(bg, _navThumbBrush) ? thumb
                        : ReferenceEquals(bg, _navAccentBrush) ? accent : null;
                    if (s is not null) { border.ClearValue(Border.BackgroundProperty); border.Style = s; swapped++; }
                    break;
                case TextBlock text:
                    Style? t = ReferenceEquals(text.Foreground, _navTextPrimary) ? primary
                        : ReferenceEquals(text.Foreground, _navTextSecondary) ? secondary
                        : ReferenceEquals(text.Foreground, _navTextTertiary) ? tertiary : null;
                    if (t is not null) { text.ClearValue(TextBlock.ForegroundProperty); text.Style = t; swapped++; }
                    break;
            }
            switch (node)
            {
                case Panel panel:
                    foreach (UIElement child in panel.Children) Walk(child);
                    break;
                case Border b when b.Child is { } c:
                    Walk(c);
                    break;
                case ContentControl cc when cc.Content is DependencyObject d:
                    Walk(d);
                    break;
            }
        }
    }

    /// <summary>UI thread: paint the probe id and switch the theme in the same turn, the standard WinUI way.</summary>
    private void ApplyTheme(int probeId, bool dark)
    {
        FrameIdProbe.Encode(probeId, out byte r, out byte g, out byte b);
        _probeBrush.Color = Windows.UI.Color.FromArgb(255, r, g, b);
        _root.RequestedTheme = dark ? ElementTheme.Dark : ElementTheme.Light;
    }
}
