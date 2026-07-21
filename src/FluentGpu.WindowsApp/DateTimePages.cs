using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;
using static FluentGpu.Dsl.Ui;

// ── Date & time control demo pages ──────────

[GalleryPage("CalendarView", "CalendarView", "Date & time", Icon = Icons.Grid)]
sealed partial class CalendarViewPage : Component
{
    public override Element Render() => GalleryPage.Shell("CalendarView",
        "Shows a large view of a calendar month and lets the user pick a date.",
        ExampleCard.Show(BasicSample));

    [Sample("A CalendarView")]
    static Element Basic()
    {
        // CalendarView owns its displayed month + selected day internally;
        // the bordered box is the WinUI CalendarViewBorder chrome.
        return new BoxEl
        {
            Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
            Children = [CalendarView.Create()],
        };
    }
}

[GalleryPage("DatePicker", "DatePicker", "Date & time", Icon = Icons.Document)]
sealed partial class DatePickerPage : Component
{
    static readonly Signal<DateOnly?> _date = new(null);
    static readonly Signal<DateOnly?> _noYear = new(null);
    static readonly Signal<DateOnly?> _ranged = new(null);

    public override Element Render() => GalleryPage.Shell("DatePicker",
        "Lets a user pick a date value using month / day / year fields.",
        ExampleCard.Show(SimpleSample),
        ExampleCard.Show(NoYearSample),
        ExampleCard.Show(RangedSample));

    [Sample("A simple DatePicker")]
    static Element Simple() => VStack(8,
        DatePicker.Create(_date),
        GalleryPage.LiveText(() => _date.Value is { } d ? d.ToString("MMM d, yyyy") : "No date selected"));

    [Sample("A DatePicker with the year hidden")]
    static Element NoYear() => VStack(8,
        // Each of the day / month / year columns can be hidden independently.
        DatePicker.Create(_noYear, yearVisible: false),
        GalleryPage.LiveText(() => _noYear.Value is { } d ? d.ToString("MMM d") : "No date selected"));

    [Sample("A DatePicker with a constrained year range")]
    static Element Ranged() => VStack(8,
        // The flyout's year column spans 2020–2030 (default: today ±100 years).
        DatePicker.Create(_ranged, minYear: 2020, maxYear: 2030),
        GalleryPage.LiveText(() => _ranged.Value is { } d ? d.ToString("MMM d, yyyy") : "No date selected"));
}

[GalleryPage("TimePicker", "TimePicker", "Date & time", Icon = Icons.Document)]
sealed partial class TimePickerPage : Component
{
    public override Element Render() => GalleryPage.Shell("TimePicker",
        "Lets a user pick a single time value using hour / minute / AM-PM fields.",
        ExampleCard.Show(SimpleSample));

    [Sample("A simple TimePicker")]
    static Element Simple()
    {
        // Hour / minute / AM-PM state is owned by the control; tapping a
        // field opens a flyout list of its choices.
        return TimePicker.Create();
    }
}

[GalleryPage("CalendarDatePicker", "CalendarDatePicker", "Date & time", Icon = Icons.Document)]
sealed partial class CalendarDatePickerPage : Component
{
    public override Element Render() => GalleryPage.Shell("CalendarDatePicker",
        "A drop-down control that shows a calendar for picking a single date.",
        ExampleCard.Show(BasicSample));

    [Sample("A CalendarDatePicker")]
    static Element Basic()
    {
        // Opens an interactive CalendarView in a light-dismiss flyout below the field.
        return CalendarDatePicker.Create();
    }
}

[GalleryPage("datetime", "Date & time", "Overview", Hidden = true)]
sealed class DateTimeOverviewPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Date & time", "Controls for entering dates and times.",
            GalleryPage.CategoryGrid("Date & time", navigate));
    }
}
