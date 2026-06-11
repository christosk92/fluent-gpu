using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using System;
using System.Globalization;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI CalendarDatePicker: a single button showing the chosen date (or a "Pick a date" placeholder — WinUI's
/// localized default, calendardatepicker_partial.cpp:45-48) with a trailing calendar glyph (E787). Clicking opens an
/// interactive <see cref="CalendarView"/> in a light-dismissable flyout anchored below the button; the calendar's
/// <c>SelectedDatesChanged</c> is wired back — picking a date sets <see cref="Date"/>, CLOSES the flyout and raises
/// <see cref="OnDateChanged"/> (calendardatepicker_partial.cpp:201-204, :259-276; a deselect nulls the date but keeps
/// the flyout open). The face text uses the culture's short-date pattern by default (the WinUI ShortDate formatter,
/// calendardatepicker_partial.cpp:467-473), overridable via <see cref="DateFormat"/>.
/// </summary>
public sealed class CalendarDatePicker : Component
{
    // ── Public surface (mirrors the WinUI CalendarDatePicker DPs; Min/MaxDate, IsTodayHighlighted, DisplayMode and
    //    FirstDayOfWeek forward to the hosted CalendarView — CalendarDatePicker_themeresources.xaml:203). ──
    /// <summary>Caller-owned date; null shows <see cref="PlaceholderText"/>. A fallback signal is used when null.</summary>
    public Signal<DateOnly?>? Date;
    /// <summary>WinUI <c>PlaceholderText</c> — default localized "Pick a date" (calendardatepicker_partial.cpp:45-48).</summary>
    public string PlaceholderText = "Pick a date";
    /// <summary>WinUI <c>Header</c> — shown above the face (HeaderContentPresenter, CalendarDatePicker_themeresources.xaml:216).</summary>
    public string? Header;
    public DateOnly? MinDate;
    public DateOnly? MaxDate;
    public DayOfWeek? FirstDayOfWeek;
    public bool IsTodayHighlighted = true;
    public CalendarViewDisplayMode DisplayMode = CalendarViewDisplayMode.Month;
    /// <summary>WinUI <c>DateFormat</c> analog: a .NET date format string; null = the culture's ShortDatePattern
    /// (the WinUI default ShortDate formatter, calendardatepicker_partial.cpp:467-473).</summary>
    public string? DateFormat;
    public Action<DateOnly?>? OnDateChanged;

    /// <summary>Zero-arg factory — keeps the existing demo call site (DateTimePages.cs) compiling unchanged.</summary>
    public static Element Create() => Embed.Comp(() => new CalendarDatePicker());

    public static Element Create(
        Signal<DateOnly?> date,
        string? placeholderText = null, string? header = null,
        DateOnly? minDate = null, DateOnly? maxDate = null,
        DayOfWeek? firstDayOfWeek = null, bool isTodayHighlighted = true,
        CalendarViewDisplayMode displayMode = CalendarViewDisplayMode.Month,
        string? dateFormat = null,
        Action<DateOnly?>? onDateChanged = null)
        => Embed.Comp(() => new CalendarDatePicker
        {
            Date = date, PlaceholderText = placeholderText ?? "Pick a date", Header = header,
            MinDate = minDate, MaxDate = maxDate, FirstDayOfWeek = firstDayOfWeek,
            IsTodayHighlighted = isTodayHighlighted, DisplayMode = displayMode,
            DateFormat = dateFormat, OnDateChanged = onDateChanged,
        });

    public override Element Render()
    {
        var fallback = UseSignal<DateOnly?>(null);
        var date = Date ?? fallback;
        DateOnly? d = date.Value;   // subscribe — the face re-renders when the calendar writes the signal
        var svc = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var h = UseRef<OverlayHandle?>(null);

        // WinUI subscribes the CalendarView's SelectedDatesChanged (calendardatepicker_partial.cpp:201-204):
        // a date ADDED → set Date + close the flyout (:259-271); a deselect → Date=null, flyout stays open (:273-276).
        void OnCalendarDates(IReadOnlyList<DateOnly> dates)
        {
            if (dates.Count > 0)
            {
                h.Value?.Close();
                OnDateChanged?.Invoke(dates[0]);
            }
            else
            {
                OnDateChanged?.Invoke(null);
            }
        }

        void Toggle()
        {
            if (h.Value is { IsOpen: true } o) { o.Close(); return; }
            h.Value = svc.Open(
                () => anchor.Value,
                () => CalendarView.Create(
                    date, MinDate, MaxDate, CalendarViewSelectionMode.Single, FirstDayOfWeek,
                    IsTodayHighlighted, displayMode: DisplayMode, onSelectedDatesChanged: OnCalendarDates),
                FlyoutPlacement.BottomLeft);
        }

        // Default formatter = the system short-date (calendardatepicker_partial.cpp:473 get_ShortDate).
        string faceText = d is { } picked
            ? picked.ToString(DateFormat ?? CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern, CultureInfo.CurrentCulture)
            : PlaceholderText;

        var face = new BoxEl
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
                new TextEl(faceText)
                {
                    Size = 14f,
                    // Placeholder shows in TextSecondary; chosen date = CalendarDatePickerTextForeground =
                    // TextFillColorPrimary (CalendarDatePicker_themeresources.xaml:12).
                    Color = d is null ? Tok.TextSecondary : Tok.TextPrimary,
                    Grow = 1f,
                },
                // CalendarGlyph E787, FontSize 12, foreground TextFillColorSecondary
                // (CalendarDatePicker_themeresources.xaml:219, :8).
                new TextEl("") { Size = 12f, Color = Tok.TextSecondary, FontFamily = Theme.IconFont },
            ],
        };

        if (Header is null) return face;

        // HeaderContentPresenter: Margin = CalendarDatePickerTopHeaderMargin 0,0,0,8 (:83, :216), foreground
        // CalendarDatePickerHeaderForeground = TextFillColorPrimary (:17).
        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                new TextEl(Header) { Size = 14f, Color = Tok.TextPrimary, Margin = new Edges4(0, 0, 0, 8), Wrap = TextWrap.Wrap },
                face,
            ],
        };
    }
}
