using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using System;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI CalendarView: a month grid. A header row ("MonthName Year" + prev/next chevrons), a Mon..Sun weekday
/// header, then a 7-column grid of the month's day numbers. The selected day is an accent-filled circle; the cells
/// hover-highlight. Internal state is the displayed <see cref="DateOnly"/> month (first-of-month) plus the selected day.
/// </summary>
public sealed class CalendarView : Component
{
    public static Element Create() => Embed.Comp(() => new CalendarView());

    static readonly string[] Weekdays = ["Mo", "Tu", "We", "Th", "Fr", "Sa", "Su"];

    public override Element Render()
    {
        var (month, setMonth) = UseState(new DateOnly(DateTime.Now.Year, DateTime.Now.Month, 1));
        var (sel, setSel) = UseState(DateOnly.FromDateTime(DateTime.Now));
        var today = DateOnly.FromDateTime(DateTime.Now);

        // ── header: "MMMM yyyy" + prev/next chevrons ──
        var header = new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Gap = 4f,
            Children =
            [
                new TextEl($"{month:MMMM yyyy}") { Size = 16f, Bold = true, Color = Tok.TextPrimary, Grow = 1f },
                ChevronButton("", () => setMonth(month.AddMonths(-1))),
                ChevronButton("", () => setMonth(month.AddMonths(1))),
            ],
        };

        // ── weekday header row (Mo..Su) ──
        var weekdayCells = new Element[7];
        for (int i = 0; i < 7; i++)
            weekdayCells[i] = new BoxEl
            {
                Width = 36f,
                Height = 28f,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Children = [new TextEl(Weekdays[i]) { Size = 12f, Color = Tok.TextTertiary }],
            };
        var weekdayHeader = new BoxEl { Direction = 0, Children = weekdayCells };

        // ── day grid: lead blank cells, then a cell per day; group into rows of 7 ──
        int lead = ((int)month.DayOfWeek + 6) % 7;       // Monday = 0
        int daysInMonth = DateTime.DaysInMonth(month.Year, month.Month);

        var cells = new List<Element>(lead + daysInMonth);
        for (int i = 0; i < lead; i++)
            cells.Add(new BoxEl { Width = 36f, Height = 36f });

        for (int d = 1; d <= daysInMonth; d++)
        {
            int day = d;                                  // capture in a local
            var thatDate = new DateOnly(month.Year, month.Month, day);
            bool isSel = thatDate == sel;
            bool isToday = thatDate == today;
            cells.Add(new BoxEl
            {
                Width = 36f,
                Height = 36f,
                AlignItems = FlexAlign.Center,
                Justify = FlexJustify.Center,
                Corners = Radii.Circle(36f),
                Fill = isSel ? Tok.AccentDefault : ColorF.Transparent,
                HoverFill = Tok.FillSubtleSecondary,
                BorderWidth = (isToday && !isSel) ? 1.5f : 0f,
                BorderColor = (isToday && !isSel) ? Tok.AccentDefault : ColorF.Transparent,
                Role = AutomationRole.Button,
                OnClick = () => setSel(thatDate),
                Children =
                [
                    new TextEl(day.ToString()) { Size = 13f, Color = isSel ? Tok.TextOnAccentPrimary : Tok.TextPrimary },
                ],
            });
        }

        // group cells into rows of 7
        var rows = new List<Element>();
        for (int i = 0; i < cells.Count; i += 7)
        {
            int n = Math.Min(7, cells.Count - i);
            var rowCells = new Element[n];
            for (int j = 0; j < n; j++) rowCells[j] = cells[i + j];
            rows.Add(new BoxEl { Direction = 0, Children = rowCells });
        }

        var grid = new BoxEl { Direction = 1, Gap = 2f, Children = rows.ToArray() };

        return new BoxEl
        {
            Direction = 1,
            Width = 300f,
            Gap = 8f,
            Padding = Edges4.All(8f),
            Children = [header, weekdayHeader, grid],
        };
    }

    static Element ChevronButton(string glyph, Action onClick) => new BoxEl
    {
        Width = 32f,
        Height = 32f,
        AlignItems = FlexAlign.Center,
        Justify = FlexJustify.Center,
        Corners = Radii.ControlAll,
        Fill = ColorF.Transparent,
        HoverFill = Tok.FillSubtleSecondary,
        PressedFill = Tok.FillSubtleTertiary,
        Role = AutomationRole.Button,
        OnClick = onClick,
        Children = [new TextEl(glyph) { Size = 12f, Color = Tok.TextSecondary, FontFamily = Theme.IconFont }],
    };
}
