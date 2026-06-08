using FluentGpu;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

// ── Date & time control demo pages ──────────

sealed class CalendarViewPage : Component
{
    public override Element Render() => GalleryPage.Shell("CalendarView",
        "Shows a large view of a calendar month and lets the user pick a date.",
        ControlExample.Build("A CalendarView",
            new BoxEl { Corners = Radii.OverlayAll, BorderColor = Tok.StrokeCardDefault, BorderWidth = 1f, Children = [CalendarView.Create()] }));
}

sealed class DatePickerPage : Component
{
    public override Element Render() => GalleryPage.Shell("DatePicker",
        "Lets a user pick a date value using month / day / year fields.",
        ControlExample.Build("A DatePicker", DatePicker.Create()));
}

sealed class TimePickerPage : Component
{
    public override Element Render() => GalleryPage.Shell("TimePicker",
        "Lets a user pick a single time value using hour / minute / AM-PM fields.",
        ControlExample.Build("A TimePicker", TimePicker.Create()));
}

sealed class CalendarDatePickerPage : Component
{
    public override Element Render() => GalleryPage.Shell("CalendarDatePicker",
        "A drop-down control that shows a calendar for picking a single date.",
        ControlExample.Build("A CalendarDatePicker", CalendarDatePicker.Create()));
}

sealed class DateTimeOverviewPage : Component
{
    public override Element Render() => GalleryPage.Shell("Date & time",
        "Controls for entering dates and times: CalendarView, CalendarDatePicker, DatePicker, TimePicker.");
}
