using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// ── Date & time control demo pages ──────────

[Route("CalendarView")]
sealed class CalendarViewPage : Component
{
    public override Element Render() => GalleryPage.Shell("CalendarView",
        "Shows a large view of a calendar month and lets the user pick a date.",
        ControlExample.Build("A CalendarView",
            new BoxEl { Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Children = [CalendarView.Create()] },
            code: """
            // CalendarView owns its displayed month + selected day internally;
            // the bordered box is the WinUI CalendarViewBorder chrome.
            new BoxEl
            {
                Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f,
                Children = [CalendarView.Create()],
            }
            """));
}

[Route("DatePicker")]
sealed class DatePickerPage : Component
{
    public override Element Render()
    {
        var date = UseSignal<DateOnly?>(null);
        var noYear = UseSignal<DateOnly?>(null);
        var ranged = UseSignal<DateOnly?>(null);
        return GalleryPage.Shell("DatePicker",
            "Lets a user pick a date value using month / day / year fields.",
            ControlExample.Build("A simple DatePicker", DatePicker.Create(date),
                output: GalleryPage.LiveText(() => date.Value is { } d ? d.ToString("MMM d, yyyy") : "No date selected"),
                code: """
                var date = UseSignal<DateOnly?>(null);

                DatePicker.Create(date)
                """),
            ControlExample.Build("A DatePicker with the year hidden", DatePicker.Create(noYear, yearVisible: false),
                output: GalleryPage.LiveText(() => noYear.Value is { } d ? d.ToString("MMM d") : "No date selected"),
                code: """
                var noYear = UseSignal<DateOnly?>(null);

                // Each of the day / month / year columns can be hidden independently.
                DatePicker.Create(noYear, yearVisible: false)
                """),
            ControlExample.Build("A DatePicker with a constrained year range", DatePicker.Create(ranged, minYear: 2020, maxYear: 2030),
                output: GalleryPage.LiveText(() => ranged.Value is { } d ? d.ToString("MMM d, yyyy") : "No date selected"),
                code: """
                var ranged = UseSignal<DateOnly?>(null);

                // The flyout's year column spans 2020–2030 (default: today ±100 years).
                DatePicker.Create(ranged, minYear: 2020, maxYear: 2030)
                """));
    }
}

[Route("TimePicker")]
sealed class TimePickerPage : Component
{
    public override Element Render() => GalleryPage.Shell("TimePicker",
        "Lets a user pick a single time value using hour / minute / AM-PM fields.",
        ControlExample.Build("A simple TimePicker", TimePicker.Create(),
            code: """
            // Hour / minute / AM-PM state is owned by the control; tapping a
            // field opens a flyout list of its choices.
            TimePicker.Create()
            """));
}

[Route("CalendarDatePicker")]
sealed class CalendarDatePickerPage : Component
{
    public override Element Render() => GalleryPage.Shell("CalendarDatePicker",
        "A drop-down control that shows a calendar for picking a single date.",
        ControlExample.Build("A CalendarDatePicker", CalendarDatePicker.Create(),
            code: """
            // Opens an interactive CalendarView in a light-dismiss flyout below the field.
            CalendarDatePicker.Create()
            """));
}

[Route("datetime")]
sealed class DateTimeOverviewPage : Component
{
    public override Element Render()
    {
        var navigate = UseContext(NavigationView.Nav);
        return GalleryPage.Shell("Date & time", "Controls for entering dates and times.",
            GalleryPage.CategoryGrid("Date & time", navigate));
    }
}
