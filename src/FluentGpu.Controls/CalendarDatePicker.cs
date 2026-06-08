using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using System;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI CalendarDatePicker: a single button showing the chosen date (or a "Pick a date" placeholder) with a trailing
/// calendar glyph (""). Clicking opens an interactive <see cref="CalendarView"/> in a light-dismissable flyout
/// anchored below the button. (For the demo the flyout's selection isn't wired back into the button's date — the
/// calendar is interactive on its own.)
/// </summary>
public sealed class CalendarDatePicker : Component
{
    public static Element Create() => Embed.Comp(() => new CalendarDatePicker());

    public override Element Render()
    {
        var (date, setDate) = UseState<DateOnly?>(null);   // selected date (placeholder when null)
        var svc = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var h = UseRef<OverlayHandle?>(null);

        void Toggle()
        {
            if (h.Value is { IsOpen: true } o) { o.Close(); return; }
            h.Value = svc.Open(() => anchor.Value, () => CalendarView.Create(), FlyoutPlacement.BottomLeft);
        }

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            MinHeight = 32f,
            MinWidth = 64f,                       // grows to content; no fixed 240 width (WinUI is flexible)
            // WinUI CalendarDatePickerTextBlock Padding = 12,0,0,2 (left text inset). TextEl has no Padding channel,
            // so the left inset lives on the field; a trailing inset is kept for the glyph column.
            Padding = new Edges4(12, 5, 11, 6),
            Corners = Radii.ControlAll,
            BorderWidth = 1f,
            BorderBrush = Tok.ControlElevationBorder,
            Fill = Tok.FillControlDefault,
            HoverFill = Tok.FillControlSecondary,
            PressedFill = Tok.FillControlTertiary,   // WinUI Pressed = ControlFillColorTertiary
            OnRealized = x => anchor.Value = x,
            OnClick = Toggle,
            Role = AutomationRole.ComboBox,
            Children =
            [
                new TextEl(date is { } d ? d.ToString("MMM d, yyyy") : "Pick a date")
                {
                    Size = 14f,
                    Color = date is null ? Tok.TextSecondary : Tok.TextPrimary,
                    Grow = 1f,
                },
                new TextEl("") { Size = 12f, Color = Tok.TextSecondary, FontFamily = Theme.IconFont },   // CalendarDatePickerCalendarGlyphFontSize = 12
            ],
        };
    }
}
