using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using System;
using System.Globalization;

namespace FluentGpu.Controls;

/// <summary>WinUI <c>CalendarViewSelectionMode</c>: None / Single / Multiple (controls.idl).</summary>
public enum CalendarViewSelectionMode : byte { None, Single, Multiple }

/// <summary>WinUI <c>CalendarViewDisplayMode</c> — the zoom level: Month (day grid) / Year (4-column months) /
/// Decade (4-column years).</summary>
public enum CalendarViewDisplayMode : byte { Month, Year, Decade }

/// <summary>
/// A WinUI CalendarView: a header row (a HeaderButton that ZOOMS Month→Year→Decade + EDDB/EDDC caret nav buttons),
/// a 1px divider, then the active view — a localized weekday header over a fixed 6-week day grid (Month), a 4-column
/// month grid (Year) or a 4-column year grid (Decade) — cross-zooming with the WinUI 150/500ms scale-fade transition
/// (CalendarView_themeresources.xaml:496-528). Today is an accent-FILLED circle; selected days carry a 1px accent
/// RING with accent text; out-of-scope (adjacent-month) days render in secondary text and navigate+select on click.
/// Selection is signal-based (<see cref="SelectedDate"/>) with None/Single/Multiple modes and Min/MaxDate bounds.
/// </summary>
public sealed class CalendarView : Component
{
    // ── WinUI CalendarView theme constants (CalendarView_themeresources.xaml). ──
    private const float CellSize = 40f;            // CalendarViewDayItem MinWidth/MinHeight (:244-245)
    private const float CellMargin = 1f;           // CalendarViewDayItem Margin=1 (:246) — 2px gutters on both axes
    private const float CellStride = CellSize + 2f * CellMargin;   // 42
    private const float GridWidth = 7f * CellStride;               // 294
    private const float WeekdayHeight = 40f;       // CaptionTextBlock (12px) + CalendarViewWeekDayPadding 12 (:69)
    private const float HeaderFontSize = 14f;      // CalendarViewHeaderNavigationButtonFontSize (:65)
    private const float NavGlyphSize = 8f;         // CalendarViewNavigationButtonFontSize (:66)
    private const float NavScalePressed = 0.875f;  // CalendarViewNavigationButtonScalePressed (:242)
    private const int YearDecadeCols = 4;          // m_colsInYearDecadeView default (CalendarView_Partial.h:369)
    private const string CaretUp8 = "";      // PreviousButton Content (CalendarView_themeresources.xaml:682)
    private const string CaretDown8 = "";    // NextButton Content (:683)

    // ── Public surface (mirrors the WinUI CalendarView DPs). ──
    /// <summary>Caller-owned single-selection date (two-way); ignored in Multiple mode. Null = no selection.</summary>
    public Signal<DateOnly?>? SelectedDate;
    public CalendarViewSelectionMode SelectionMode = CalendarViewSelectionMode.Single;
    public DateOnly? MinDate;
    public DateOnly? MaxDate;
    /// <summary>Null = the culture's first day of week (CalendarView_Partial.cpp:2275-2286; Sunday in en-US).</summary>
    public DayOfWeek? FirstDayOfWeek;
    public bool IsTodayHighlighted = true;
    /// <summary>2-8 weeks, default 6 (s_min/max/defaultNumberOfWeeks, CalendarView_Partial.h:364-366).</summary>
    public int NumberOfWeeksInView = 6;
    /// <summary>The initial zoom level (the header button / item picks navigate it afterwards).</summary>
    public CalendarViewDisplayMode DisplayMode = CalendarViewDisplayMode.Month;
    /// <summary>False renders adjacent-month cells blank (WinUI IsOutOfScopeEnabled).</summary>
    public bool IsOutOfScopeEnabled = true;
    /// <summary>WinUI <c>SelectedDatesChanged</c>: invoked with the post-change selected dates (empty on deselect).</summary>
    public Action<IReadOnlyList<DateOnly>>? OnSelectedDatesChanged;

    /// <summary>Zero-arg factory — keeps the existing demo call sites (DateTimePages.cs) compiling unchanged.</summary>
    public static Element Create() => Embed.Comp(() => new CalendarView());

    public static Element Create(
        Signal<DateOnly?> selectedDate,
        DateOnly? minDate = null, DateOnly? maxDate = null,
        CalendarViewSelectionMode selectionMode = CalendarViewSelectionMode.Single,
        DayOfWeek? firstDayOfWeek = null,
        bool isTodayHighlighted = true,
        int numberOfWeeksInView = 6,
        CalendarViewDisplayMode displayMode = CalendarViewDisplayMode.Month,
        bool isOutOfScopeEnabled = true,
        Action<IReadOnlyList<DateOnly>>? onSelectedDatesChanged = null)
        => Embed.Comp(() => new CalendarView
        {
            SelectedDate = selectedDate, MinDate = minDate, MaxDate = maxDate, SelectionMode = selectionMode,
            FirstDayOfWeek = firstDayOfWeek, IsTodayHighlighted = isTodayHighlighted,
            NumberOfWeeksInView = numberOfWeeksInView, DisplayMode = displayMode,
            IsOutOfScopeEnabled = isOutOfScopeEnabled, OnSelectedDatesChanged = onSelectedDatesChanged,
        });

    private static int Depth(CalendarViewDisplayMode m)
        => m switch { CalendarViewDisplayMode.Month => 2, CalendarViewDisplayMode.Year => 1, _ => 0 };

    public override Element Render()
    {
        var today = DateOnly.FromDateTime(DateTime.Now);
        var fallbackSel = UseSignal<DateOnly?>(null);
        var selected = SelectedDate ?? fallbackSel;
        DateOnly? sel = selected.Value;                       // subscribe (granular re-render on selection change)
        var multiSel = UseRef<List<DateOnly>>(new List<DateOnly>());
        var multiVersion = UseSignal(0);
        _ = multiVersion.Value;                               // subscribe to Multiple-mode toggles

        var initial = sel ?? today;
        var (month, setMonth) = UseState(new DateOnly(initial.Year, initial.Month, 1));
        var (mode, setMode) = UseState(DisplayMode);
        var prevMode = UseRef(DisplayMode);
        var mounted = UseRef(false);

        int weeks = Math.Clamp(NumberOfWeeksInView, 2, 8);    // s_min/maxNumberOfWeeks (CalendarView_Partial.h:364-365)
        float viewsHeight = WeekdayHeight + weeks * CellStride;
        var dtf = CultureInfo.CurrentCulture.DateTimeFormat;
        DayOfWeek firstDow = FirstDayOfWeek ?? dtf.FirstDayOfWeek;   // culture default (CalendarView_Partial.cpp:2275-2286)

        bool InRange(DateOnly d) => (MinDate is null || d >= MinDate.Value) && (MaxDate is null || d <= MaxDate.Value);

        // ── Selection (CalendarView_Partial_Selection.cpp OnSelectDayItem:55-100: clicking a selected day
        //    DEselects it; Single replaces any existing selection; None ignores). ──
        void SelectDay(DateOnly d)
        {
            switch (SelectionMode)
            {
                case CalendarViewSelectionMode.None:
                    return;
                case CalendarViewSelectionMode.Single:
                    bool deselect = selected.Peek() == d;
                    selected.Value = deselect ? null : d;
                    OnSelectedDatesChanged?.Invoke(deselect ? Array.Empty<DateOnly>() : new[] { d });
                    return;
                default:   // Multiple — toggle membership (Partial_Selection.cpp:85-100)
                    var list = multiSel.Value;
                    int idx = list.IndexOf(d);
                    if (idx >= 0) list.RemoveAt(idx); else list.Add(d);
                    multiVersion.Value++;
                    OnSelectedDatesChanged?.Invoke(list.ToArray());
                    return;
            }
        }

        bool IsSelected(DateOnly d) => SelectionMode == CalendarViewSelectionMode.Multiple
            ? multiSel.Value.Contains(d)
            : SelectionMode == CalendarViewSelectionMode.Single && sel == d;

        // ── Header (HeaderButton + PreviousButton/NextButton, CalendarView_themeresources.xaml:681-683). ──
        int decadeStart = month.Year - month.Year % 10;
        string headerText = mode switch
        {
            CalendarViewDisplayMode.Month => month.ToString("MMMM yyyy", CultureInfo.CurrentCulture),
            CalendarViewDisplayMode.Year => month.Year.ToString(CultureInfo.CurrentCulture),
            _ => $"{decadeStart} - {decadeStart + 9}",
        };

        // Prev/next enablement = TemplateSettings.HasMoreContentBefore/After (:682-683), gated by Min/MaxDate.
        bool canPrev = mode switch
        {
            CalendarViewDisplayMode.Month => MinDate is null || month > new DateOnly(MinDate.Value.Year, MinDate.Value.Month, 1),
            CalendarViewDisplayMode.Year => MinDate is null || month.Year > MinDate.Value.Year,
            _ => MinDate is null || decadeStart > MinDate.Value.Year - MinDate.Value.Year % 10,
        };
        bool canNext = mode switch
        {
            CalendarViewDisplayMode.Month => MaxDate is null || month < new DateOnly(MaxDate.Value.Year, MaxDate.Value.Month, 1),
            CalendarViewDisplayMode.Year => MaxDate is null || month.Year < MaxDate.Value.Year,
            _ => MaxDate is null || decadeStart < MaxDate.Value.Year - MaxDate.Value.Year % 10,
        };

        void Navigate(int dir)
        {
            setMonth(mode switch
            {
                CalendarViewDisplayMode.Month => month.AddMonths(dir),
                CalendarViewDisplayMode.Year => month.AddYears(dir),
                _ => month.AddYears(dir * 10),
            });
        }

        // HeaderButton zooms out Month→Year→Decade; disabled at Decade (IsEnabled=TemplateSettings.HasMoreViews, :681).
        bool hasMoreViews = mode != CalendarViewDisplayMode.Decade;
        var headerButton = new BoxEl
        {
            Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start,
            Margin = new Edges4(7, 6, 3, 7),        // CalendarViewHeaderNavigationButtonMargin (:75)
            Padding = new Edges4(8, 7, 8, 8),       // CalendarViewHeaderNavigationButtonPadding (:73)
            Corners = Radii.ControlAll,             // ControlCornerRadius (:342)
            // Background ramp SubtleFillColor Transparent→Secondary→Tertiary (:23, :53-54).
            Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            FocusVisualMargin = Edges4.All(-2f),    // CalendarViewNavigationButtonFocusVisualMargin (:236, :343)
            IsEnabled = hasMoreViews,
            Role = AutomationRole.Button,
            OnClick = () => setMode(mode == CalendarViewDisplayMode.Month ? CalendarViewDisplayMode.Year : CalendarViewDisplayMode.Decade),
            Children =
            [
                // FontSize 14 (:65), SemiBold (CalendarViewHeaderNavigationFontWeight, :79); foreground
                // TextPrimary → hover TextPrimary → pressed TextSecondary → disabled TextDisabled (:56-59).
                new TextEl(headerText)
                {
                    Size = HeaderFontSize, Weight = 600, Color = Tok.TextPrimary,
                    HoverColor = Tok.TextPrimary, PressedColor = Tok.TextSecondary, DisabledColor = Tok.TextDisabled,
                },
            ],
        };

        // EDDB/EDDC caret nav buttons (:682-683): FontSize 8 (:66), Padding 12,11.5,12,11.5 (:74), foreground
        // ControlStrongFillColorDefault for rest/hover/pressed (:55, :24-25) / ControlStrongFillColorDisabled (:26),
        // background SubtleFillColor Transparent→Secondary→Tertiary (:23, :53-54), pressed scale 0.875 (:242, :813-819).
        BoxEl NavButton(string glyph, Edges4 margin, bool isEnabled, Action onClick) => new()
        {
            Direction = 0, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Margin = margin,
            Padding = new Edges4(12f, 11.5f, 12f, 11.5f),
            Corners = Radii.ControlAll,
            Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            PressScale = NavScalePressed,
            FocusVisualMargin = Edges4.All(-2f),    // CalendarViewNavigationButtonFocusVisualMargin (:78, :386)
            IsEnabled = isEnabled,
            Role = AutomationRole.Button,
            OnClick = onClick,
            Children =
            [
                new TextEl(glyph)
                {
                    Size = NavGlyphSize, FontFamily = Theme.IconFont,
                    Color = Tok.FillControlStrong, HoverColor = Tok.FillControlStrong,
                    PressedColor = Tok.FillControlStrong, DisabledColor = Tok.FillControlStrongDisabled,
                },
            ],
        };

        var header = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center,
            Children =
            [
                headerButton,
                NavButton(CaretUp8, new Edges4(3, 6, 3, 7), canPrev, () => Navigate(-1)),    // CalendarViewNavigationPreviousButtonMargin (:76)
                NavButton(CaretDown8, new Edges4(3, 6, 7, 7), canNext, () => Navigate(+1)),  // CalendarViewNavigationNextButtonMargin (:77)
            ],
        };

        // 1px header divider = the control's BorderBrush = CalendarViewBorderBrush = ControlStrokeColorDefaultBrush
        // (template Border Height=1 Background={TemplateBinding BorderBrush}, :685, :21).
        var separator = new BoxEl { Height = 1f, Fill = Tok.StrokeControlDefault };

        // ── The active view, keyed per zoom level inside a fixed clipped ZStack so the exiting orphan and the
        //    entering view overlay during the WinUI zoom transition (Views Grid.Clip, :687-689). ──
        Element activeView = mode switch
        {
            CalendarViewDisplayMode.Month => BuildMonthView(),
            CalendarViewDisplayMode.Year => BuildYearView(),
            _ => BuildDecadeView(),
        };

        var viewsHost = new BoxEl
        {
            Direction = 1, Width = GridWidth, Height = viewsHeight, ZStack = true, ClipToBounds = true,
            Margin = Edges4.All(2f),   // the view ScrollViewers' Margin=2 (:721, :725, :731)
            Children = [activeView],
        };

        prevMode.Value = mode;
        mounted.Value = true;

        return new BoxEl
        {
            Direction = 1,
            Width = GridWidth + 4f,    // grid + the 2px view margins
            Children = [header, separator, viewsHost],
        };

        // ── Zoom transition (CalendarView_themeresources.xaml DisplayModeStates transitions, :496-528 / :530-573):
        //    the OUTGOING view fades while scaling over 150ms — to 0.84 when zooming out, to 1.29 when zooming in —
        //    and the INCOMING view fades in from the opposite scale (1.29 / 0.84) settling over 500ms, both on
        //    ControlFastOutSlowInKeySpline = cubic-bezier(0,0,0,1) (Common_themeresources_any.xaml:602).
        //    COMPROMISE: WinUI holds the incoming view 150ms before its 350ms ease; LayoutTransition has ONE shared
        //    DelayMs for the enter AND exit legs, so the enter runs 0-350ms instead — exit timing (what drives the
        //    perceived click response) is kept exact. A view's EXIT scale is fixed at author time: Month only ever
        //    exits zooming OUT (0.84) and Decade only zooming IN (1.29); Year exits via the dominant Year→Month path
        //    (1.29 — the rarer Year→Decade exit approximates).
        LayoutTransition ZoomSpec(float exitScale)
        {
            bool zoomIn = Depth(mode) > Depth(prevMode.Value);
            float enterScale = zoomIn ? 0.84f : 1.29f;
            var spline = EasingSpec.CubicBezier(0f, 0f, 0f, 1f);
            return new LayoutTransition(
                TransitionChannels.Opacity,
                TransitionDynamics.Tween(350f, spline),
                Enter: mounted.Value
                    ? new EnterExit(Sx: enterScale, Sy: enterScale, Opacity: 0f, Active: true)
                    : default,   // no zoom-in on the control's initial mount
                Exit: new EnterExit(Sx: exitScale, Sy: exitScale, Opacity: 0f, Active: true),
                ExitDynamics: TransitionDynamics.Tween(150f, spline));
        }

        // ── MONTH view: localized weekday header + a FIXED 6-week day grid (s_defaultNumberOfWeeks,
        //    CalendarView_Partial.h:366) padded with adjacent-month out-of-scope days. ──
        Element BuildMonthView()
        {
            // Weekday header: star columns (:704-712), names localized via the culture's shortest day names
            // (TemplateSettings.WeekDayNames, :713-719), CaptionTextBlockStyle 12px + CalendarViewWeekDayFontWeight
            // SemiBold (:325-331, :80), foreground = CalendarItemForeground = TextFillColorPrimary (:16, :713).
            var weekdayCells = new Element[7];
            for (int i = 0; i < 7; i++)
            {
                int dowIdx = ((int)firstDow + i) % 7;
                weekdayCells[i] = new BoxEl
                {
                    Grow = 1f, Height = WeekdayHeight, Margin = Edges4.All(CellMargin),   // WeekDayMargin=1 (:68)
                    AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    Children = [new TextEl(dtf.ShortestDayNames[dowIdx]) { Size = 12f, Weight = 600, Color = Tok.TextPrimary }],
                };
            }
            var weekdayHeader = new BoxEl { Direction = 0, Children = weekdayCells };

            // The grid always shows `weeks` full rows: lead with the previous month's trailing days.
            int lead = (((int)month.DayOfWeek - (int)firstDow) + 7) % 7;
            DateOnly first = month.AddDays(-lead);

            var rows = new Element[weeks];
            for (int w = 0; w < weeks; w++)
            {
                var rowCells = new Element[7];
                for (int i = 0; i < 7; i++)
                {
                    DateOnly d = first.AddDays(w * 7 + i);
                    rowCells[i] = DayCell(d);
                }
                rows[w] = new BoxEl { Direction = 0, Children = rowCells };
            }

            return new BoxEl
            {
                Key = "month", Direction = 1, Animate = ZoomSpec(0.84f),   // MonthView exit scale (:509-513)
                Children = [weekdayHeader, new BoxEl { Direction = 1, Children = rows }],
            };
        }

        Element DayCell(DateOnly d)
        {
            bool inScope = d.Year == month.Year && d.Month == month.Month;
            if (!inScope && !IsOutOfScopeEnabled)
                return new BoxEl { Width = CellSize, Height = CellSize, Margin = Edges4.All(CellMargin) };

            bool inRange = InRange(d);
            bool isToday = IsTodayHighlighted && d == today;
            bool isSel = IsSelected(d);
            bool ring = isSel && !isToday;
            var thatDate = d;

            // Foregrounds: normal TextPrimary / pressed TextSecondary (:16, :14); selected = AccentTextFillColorPrimary,
            // hover same, pressed AccentTextFillColorTertiary (:13, :37-38); today = TextOnAccentFillColorPrimary (:11);
            // out-of-scope = TextSecondary, hover TextPrimary, pressed TextTertiary (:15, :40-41); disabled (:36).
            ColorF fg = isToday ? Tok.TextOnAccentPrimary
                      : isSel ? Tok.AccentTextPrimary
                      : inScope ? Tok.TextPrimary : Tok.TextSecondary;
            ColorF fgHover = isToday ? Tok.TextOnAccentPrimary
                           : isSel ? Tok.AccentTextPrimary
                           : inScope ? Tok.TextPrimary : Tok.TextPrimary;
            ColorF fgPressed = isToday ? Tok.TextOnAccentPrimary
                             : isSel ? Tok.AccentTextTertiary
                             : inScope ? Tok.TextSecondary : Tok.TextTertiary;

            return new BoxEl
            {
                Width = CellSize, Height = CellSize,
                Margin = Edges4.All(CellMargin),     // CalendarViewDayItem Margin=1 (:246)
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = Radii.Circle(CellSize),    // rounded chrome (CalendarViewBaseItemRoundedChromeEnabled, :62)
                // Today: accent disc, hover AccentFillColorSecondary, pressed AccentFillColorTertiary
                // (CalendarViewTodayBackground/Today{Hover,Pressed}Background, :46, :49-50). Other cells: transparent
                // with the CalendarItem{Hover,Pressed}Background subtle ramp (:18, :42-43).
                Fill = isToday ? Tok.AccentDefault : ColorF.Transparent,
                HoverFill = isToday ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
                PressedFill = isToday ? Tok.AccentTertiary : Tok.FillSubtleTertiary,
                // Selected ring: 1px (CalendarItemBorderThickness, :311), AccentFillColorDefault → hover
                // AccentFillColorSecondary → pressed SubtleFillColorTertiary (Selected{,Hover,Pressed}BorderBrush,
                // :8, :6-7). Today+selected: 1px inner ring in TodaySelectedInnerBorderBrush =
                // TextOnAccentFillColorPrimary over the accent disc (:35).
                BorderWidth = ring || (isSel && isToday) ? 1f : 0f,
                BorderColor = isSel && isToday ? Tok.TextOnAccentPrimary : ring ? Tok.AccentDefault : ColorF.Transparent,
                HoverBorderColor = isSel && isToday ? Tok.TextOnAccentPrimary : ring ? Tok.AccentSecondary : default,
                PressedBorderColor = isSel && isToday ? Tok.TextOnAccentPrimary : ring ? Tok.FillSubtleTertiary : default,
                IsEnabled = inRange,
                Role = AutomationRole.Button,
                OnClick = () =>
                {
                    // Out-of-scope picks navigate to that month, then select (WinUI scope navigation).
                    if (!inScope) setMonth(new DateOnly(thatDate.Year, thatDate.Month, 1));
                    SelectDay(thatDate);
                },
                Children =
                [
                    // TodayFontWeight = Normal (:81); DayItemFontSize = ControlContentThemeFontSize 14 (:60).
                    new TextEl(d.Day.ToString(CultureInfo.CurrentCulture))
                    {
                        Size = 14f, Color = fg, HoverColor = fgHover, PressedColor = fgPressed,
                        DisabledColor = Tok.TextDisabled,
                    },
                ],
            };
        }

        // ── YEAR view: 4×4 month items — the displayed year's 12 months + the next year's lead months out-of-scope
        //    (m_colsInYearDecadeView = 4, CalendarView_Partial.h:369); the current month gets the today disc. ──
        Element BuildYearView()
        {
            float cellW = GridWidth / YearDecadeCols - 2f * CellMargin;
            float cellH = viewsHeight / YearDecadeCols - 2f * CellMargin;
            var rows = new Element[YearDecadeCols];
            for (int r = 0; r < YearDecadeCols; r++)
            {
                var rowCells = new Element[YearDecadeCols];
                for (int c = 0; c < YearDecadeCols; c++)
                {
                    int idx = r * YearDecadeCols + c;            // 0..15
                    int year = month.Year + idx / 12;
                    int m = idx % 12 + 1;
                    bool inScope = year == month.Year;
                    bool isThisMonth = IsTodayHighlighted && year == today.Year && m == today.Month;
                    var firstOf = new DateOnly(year, m, 1);
                    bool inRange = (MinDate is null || firstOf.AddMonths(1).AddDays(-1) >= MinDate.Value)
                                && (MaxDate is null || firstOf <= MaxDate.Value);
                    rowCells[c] = MonthYearCell(
                        dtf.AbbreviatedMonthNames[m - 1], cellW, cellH, isThisMonth, inScope, inRange,
                        () => { setMonth(firstOf); setMode(CalendarViewDisplayMode.Month); });
                }
                rows[r] = new BoxEl { Direction = 0, Children = rowCells };
            }
            // YearView exit: it only re-enters Month (zoom in) on item pick — outgoing scales to 1.29 (:546-551).
            return new BoxEl { Key = "year", Direction = 1, Animate = ZoomSpec(1.29f), Children = rows };
        }

        // ── DECADE view: 4×4 year items — the decade's 10 years + the next decade's lead years out-of-scope. ──
        Element BuildDecadeView()
        {
            float cellW = GridWidth / YearDecadeCols - 2f * CellMargin;
            float cellH = viewsHeight / YearDecadeCols - 2f * CellMargin;
            var rows = new Element[YearDecadeCols];
            for (int r = 0; r < YearDecadeCols; r++)
            {
                var rowCells = new Element[YearDecadeCols];
                for (int c = 0; c < YearDecadeCols; c++)
                {
                    int year = decadeStart + r * YearDecadeCols + c;
                    bool inScope = year < decadeStart + 10;
                    bool isThisYear = IsTodayHighlighted && year == today.Year;
                    bool inRange = (MinDate is null || year >= MinDate.Value.Year)
                                && (MaxDate is null || year <= MaxDate.Value.Year);
                    int y = year;
                    rowCells[c] = MonthYearCell(
                        year.ToString(CultureInfo.CurrentCulture), cellW, cellH, isThisYear, inScope, inRange,
                        () =>
                        {
                            int mm = Math.Clamp(month.Month, 1, 12);
                            setMonth(new DateOnly(y, mm, 1));
                            setMode(CalendarViewDisplayMode.Year);
                        });
                }
                rows[r] = new BoxEl { Direction = 0, Children = rowCells };
            }
            // DecadeView only ever exits zooming IN to Year — outgoing scales to 1.29 (the Year→Decade reverse).
            return new BoxEl { Key = "decade", Direction = 1, Animate = ZoomSpec(1.29f), Children = rows };
        }

        // A Year/Decade view item: same brush model as the day cells (MonthYearItemFontSize =
        // ControlContentThemeFontSize 14, :61; the "today" item carries the accent disc treatment).
        Element MonthYearCell(string label, float w, float h, bool isToday_, bool inScope, bool inRange, Action onClick)
        {
            float diameter = MathF.Min(w, h);
            return new BoxEl
            {
                Width = w, Height = h, Margin = Edges4.All(CellMargin),
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Corners = CornerRadius4.All(diameter / 2f),
                Fill = isToday_ ? Tok.AccentDefault : ColorF.Transparent,
                HoverFill = isToday_ ? Tok.AccentSecondary : Tok.FillSubtleSecondary,
                PressedFill = isToday_ ? Tok.AccentTertiary : Tok.FillSubtleTertiary,
                IsEnabled = inRange,
                Role = AutomationRole.Button,
                OnClick = onClick,
                Children =
                [
                    new TextEl(label)
                    {
                        Size = 14f,
                        Color = isToday_ ? Tok.TextOnAccentPrimary : inScope ? Tok.TextPrimary : Tok.TextSecondary,
                        HoverColor = isToday_ ? Tok.TextOnAccentPrimary : Tok.TextPrimary,
                        PressedColor = isToday_ ? Tok.TextOnAccentPrimary : inScope ? Tok.TextSecondary : Tok.TextTertiary,
                        DisabledColor = Tok.TextDisabled,
                    },
                ],
            };
        }
    }
}
