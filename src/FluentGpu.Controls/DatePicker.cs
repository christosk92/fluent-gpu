using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using FluentGpu.Signals;
using System;
using System.Globalization;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI DatePicker: a single Button FACE that shows Day / Month / Year text columns (ordered per locale from
/// <see cref="CultureInfo.CurrentCulture"/>'s ShortDatePattern) separated by 2px vertical spacers, and on tap opens a
/// <c>DatePickerFlyout</c> popup (296px wide) via the <see cref="Overlay"/> service. The popup hosts three side-by-side
/// LOOPING selector columns (item height 40) following the WinUI LoopingSelector RepeatButton model — ▲(E70E)/▼(E70D)
/// up/down step buttons revealed on hover plus click-to-select — under a centered 40px accent highlight pill, with a
/// 41px Accept(✓) / Dismiss(✗) bar. <see cref="SelectedDate"/> is nullable (a null value shows the day/month/year
/// placeholders). Day/Month/Year columns can each be hidden; the year range defaults to today ±100 years.
/// </summary>
/// <remarks>
/// FIDELITY NOTE: WinUI's LoopingSelector also supports a touch-fling momentum scroll-snap. The engine's
/// <c>ScrollEl</c> is layout-free with no programmatic scroll-offset signal and no snap-points seam, so this control
/// uses WinUI's RepeatButton + click-to-select fallback (which WinUI ships for the keyboard/mouse path) rather than
/// adding a new engine seam. The visual result — a fixed window with a centered selection and up/down repeat buttons —
/// is faithful; true touch-fling snapping is a deferred engine seam.
/// </remarks>
public sealed class DatePicker : Component
{
    // ── WinUI DatePicker / DatePickerFlyoutPresenter theme constants (generic.xaml lines 5920-5947, 8668-8882, 12801-12889).
    private const float FaceMinWidth = 296f;     // DatePickerThemeMinWidth
    private const float FaceMaxWidth = 456f;     // DatePickerThemeMaxWidth
    private const float FaceMinHeight = 32f;
    private const float PresenterWidth = 296f;   // DatePickerFlyoutPresenter Width/MinWidth
    private const float ItemHeight = 40f;        // DatePickerFlyoutPresenterItemHeight
    private const float HighlightHeight = 40f;   // DatePickerFlyoutPresenterHighlightHeight
    private const float AcceptBarHeight = 41f;   // DatePickerFlyoutPresenterAcceptDismissHostGridHeight
    private const float SpacerWidth = 2f;        // FirstPickerSpacing / SecondPickerSpacing
    private const int VisibleRows = 5;           // fixed window
    private const float ColumnHeight = ItemHeight * VisibleRows;   // 200
    // 5-column grid weights: DayColumn 78* | MonthColumn 132* | YearColumn 78*.
    private const float DayWeight = 78f;
    private const float MonthWeight = 132f;
    private const float YearWeight = 78f;

    // ── Public surface (mirrors the WinUI DatePicker DPs). ──
    /// <summary>Caller-owned selected date; null shows the placeholder (HasNoDate). A fallback UseState is used when null.</summary>
    public Signal<DateOnly?>? SelectedDate;
    public bool DayVisible = true;
    public bool MonthVisible = true;
    public bool YearVisible = true;
    public int? MinYear;   // default DateTime.Now.Year - 100
    public int? MaxYear;   // default DateTime.Now.Year + 100
    public Action<DateOnly?>? OnDateChanged;

    /// <summary>Zero-arg factory — keeps the existing demo call site (DateTimePages.cs) compiling unchanged.</summary>
    public static Element Create() => Embed.Comp(() => new DatePicker());

    public static Element Create(
        Signal<DateOnly?> selectedDate,
        bool dayVisible = true, bool monthVisible = true, bool yearVisible = true,
        int? minYear = null, int? maxYear = null,
        Action<DateOnly?>? onDateChanged = null)
        => Embed.Comp(() => new DatePicker
        {
            SelectedDate = selectedDate,
            DayVisible = dayVisible, MonthVisible = monthVisible, YearVisible = yearVisible,
            MinYear = minYear, MaxYear = maxYear, OnDateChanged = onDateChanged,
        });

    private enum Part : byte { Day, Month, Year }

    public override Element Render()
    {
        var svc = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var fallback = UseSignal<DateOnly?>(null);
        var date = SelectedDate ?? fallback;

        // Tentative selection while the flyout is open (committed only on Accept). The flyout body reads these, so a
        // bump re-renders ONLY the looping columns (signals-first granular re-render).
        var tentYear = UseSignal(0);
        var tentMonth = UseSignal(0);   // 0-based month index (0..11)
        var tentDay = UseSignal(0);     // 0-based day index (0..DaysInMonth-1)

        DateOnly? committed = date.Value;
        bool hasDate = committed.HasValue;

        int nowYear = DateTime.Now.Year;
        int firstYear = MinYear ?? (nowYear - 100);
        int lastYear = MaxYear ?? (nowYear + 100);
        if (lastYear < firstYear) lastYear = firstYear;
        int yearCount = lastYear - firstYear + 1;

        var monthAbbr = MonthAbbreviations();

        // ── FACE text per column (placeholder day/month/year when no date). ──
        var seed = committed ?? DateOnly.FromDateTime(DateTime.Now);
        string dayText = hasDate ? seed.Day.ToString(CultureInfo.CurrentCulture) : "day";
        string monthText = hasDate ? monthAbbr[seed.Month - 1] : "month";
        string yearText = hasDate ? seed.Year.ToString(CultureInfo.CurrentCulture) : "year";
        var faceColor = hasDate ? Tok.TextPrimary : Tok.TextSecondary;

        Element FaceCell(Part p) => p switch
        {
            Part.Day => new BoxEl
            {
                Direction = 0, Grow = DayWeight, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Padding = new Edges4(0, 3, 0, 6),   // DatePickerFlyoutPresenterItemPadding
                Children = [new TextEl(dayText) { Size = 14f, Color = faceColor }],
            },
            Part.Month => new BoxEl
            {
                Direction = 0, Grow = MonthWeight, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start,
                Padding = new Edges4(9, 3, 0, 6),   // DatePickerFlyoutPresenterMonthPadding
                Children = [new TextEl(monthText) { Size = 14f, Color = faceColor }],
            },
            _ => new BoxEl
            {
                Direction = 0, Grow = YearWeight, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Padding = new Edges4(0, 3, 0, 6),
                Children = [new TextEl(yearText) { Size = 14f, Color = faceColor }],
            },
        };

        Element Spacer() => new BoxEl { Width = SpacerWidth, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault };

        var order = VisibleOrder();
        var faceChildren = new System.Collections.Generic.List<Element>(order.Length * 2 - 1);
        for (int i = 0; i < order.Length; i++)
        {
            if (i > 0) faceChildren.Add(Spacer());
            faceChildren.Add(FaceCell(order[i]));
        }

        void OpenFlyout()
        {
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }

            // Seed tentatives from the committed date (or today when null) and clamp to the year range.
            int y = Math.Clamp(seed.Year, firstYear, lastYear);
            tentYear.Value = y - firstYear;
            tentMonth.Value = seed.Month - 1;
            int maxDay = DateTime.DaysInMonth(y, seed.Month);
            tentDay.Value = Math.Clamp(seed.Day, 1, maxDay) - 1;

            handle.Value = svc.Open(() => anchor.Value, FlyoutBody, FlyoutPlacement.BottomLeft);
        }

        void Commit()
        {
            int year = firstYear + Math.Clamp(tentYear.Peek(), 0, yearCount - 1);
            int month = Math.Clamp(tentMonth.Peek(), 0, 11) + 1;
            int maxDay = DateTime.DaysInMonth(year, month);
            int day = Math.Clamp(tentDay.Peek() + 1, 1, maxDay);
            var picked = new DateOnly(year, month, day);
            date.Value = picked;
            OnDateChanged?.Invoke(picked);
            handle.Value?.Close();
        }

        Element FlyoutBody() => Embed.Comp(() => new DatePickerFlyoutBody
        {
            FirstYear = firstYear, YearCount = yearCount, MonthAbbr = monthAbbr, Order = order,
            TentYear = tentYear, TentMonth = tentMonth, TentDay = tentDay,
            OnAccept = Commit, OnDismiss = () => handle.Value?.Close(),
        });

        return new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center,
            MinWidth = FaceMinWidth, MaxWidth = FaceMaxWidth, MinHeight = FaceMinHeight,
            Corners = Radii.ControlAll,
            BorderWidth = 1f, BorderBrush = Tok.ControlElevationBorder,
            Fill = Tok.FillControlDefault, HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
            ClipToBounds = true,
            Role = AutomationRole.ComboBox,
            OnRealized = h => anchor.Value = h,
            OnClick = OpenFlyout,
            Children = faceChildren.ToArray(),
        };
    }

    /// <summary>The visible {Day,Month,Year} parts in this culture's order (DatePicker_Partial.cpp GetOrder analog):
    /// scan the ShortDatePattern for the first 'd', 'M', 'y'. Hidden columns are dropped.</summary>
    private Part[] VisibleOrder()
    {
        string pat = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;
        int di = FirstIndexIgnoringLiterals(pat, 'd');
        int mi = FirstIndexIgnoringLiterals(pat, 'M');
        int yi = FirstIndexIgnoringLiterals(pat, 'y');
        if (di < 0) di = 0;
        if (mi < 0) mi = 1;
        if (yi < 0) yi = 2;

        var all = new (Part p, int idx, bool vis)[]
        {
            (Part.Day, di, DayVisible),
            (Part.Month, mi, MonthVisible),
            (Part.Year, yi, YearVisible),
        };
        Array.Sort(all, static (a, b) => a.idx.CompareTo(b.idx));

        var result = new System.Collections.Generic.List<Part>(3);
        foreach (var e in all) if (e.vis) result.Add(e.p);
        if (result.Count == 0) result.Add(Part.Day);   // never produce an empty face
        return result.ToArray();
    }

    private static int FirstIndexIgnoringLiterals(string pattern, char field)
    {
        bool inQuote = false;
        for (int i = 0; i < pattern.Length; i++)
        {
            char c = pattern[i];
            if (c == '\'') { inQuote = !inQuote; continue; }
            if (!inQuote && c == field) return i;
        }
        return -1;
    }

    private static string[] MonthAbbreviations()
    {
        // AbbreviatedMonthNames has 13 entries (index 12 is the empty extra month) — take the first 12.
        var names = CultureInfo.CurrentCulture.DateTimeFormat.AbbreviatedMonthNames;
        var months = new string[12];
        for (int i = 0; i < 12; i++) months[i] = names[i];
        return months;
    }

    /// <summary>The DatePickerFlyout popup content (inner panel only — OverlayHost's FlyoutSurface supplies the acrylic
    /// backdrop, 1px stroke, OverlayCornerRadius(8), shadow and clip). A hook-owning Component so it re-renders the
    /// looping columns on each tentative-signal change.</summary>
    private sealed class DatePickerFlyoutBody : Component
    {
        public int FirstYear;
        public int YearCount;
        public string[] MonthAbbr = [];
        public Part[] Order = [];
        public Signal<int> TentYear = new(0);
        public Signal<int> TentMonth = new(0);
        public Signal<int> TentDay = new(0);
        public Action OnAccept = static () => { };
        public Action OnDismiss = static () => { };

        public override Element Render()
        {
            // Subscribe to month/year so the day column re-clamps (Feb 28/29) when they change.
            int tentYear = TentYear.Value;
            int tentMonth = TentMonth.Value;
            int year = FirstYear + Math.Clamp(tentYear, 0, YearCount - 1);
            int month = Math.Clamp(tentMonth, 0, 11) + 1;
            int daysInMonth = DateTime.DaysInMonth(year, month);

            // Build the option lists.
            var days = new string[daysInMonth];
            for (int i = 0; i < daysInMonth; i++) days[i] = (i + 1).ToString(CultureInfo.CurrentCulture);

            var years = new string[YearCount];
            for (int i = 0; i < YearCount; i++) years[i] = (FirstYear + i).ToString(CultureInfo.CurrentCulture);

            Element ColumnFor(Part p) => p switch
            {
                Part.Day => DatePickerLoopColumn.Create(days, TentDay, FlexJustify.Center, DayWeight),
                Part.Month => DatePickerLoopColumn.Create(MonthAbbr, TentMonth, FlexJustify.Start, MonthWeight),
                _ => DatePickerLoopColumn.Create(years, TentYear, FlexJustify.Center, YearWeight),
            };

            Element Spacer() => new BoxEl { Width = SpacerWidth, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault };

            var columns = new System.Collections.Generic.List<Element>(Order.Length * 2 - 1);
            for (int i = 0; i < Order.Length; i++)
            {
                if (i > 0) columns.Add(Spacer());
                columns.Add(ColumnFor(Order[i]));
            }

            // PickerHostGrid: a ZStack with the centered accent highlight pill behind the columns.
            float highlightY = (ColumnHeight - HighlightHeight) / 2f;   // 80
            var pickerHost = new BoxEl
            {
                Direction = 0, Width = PresenterWidth, Height = ColumnHeight, ZStack = true, ClipToBounds = true,
                Children =
                [
                    // HighlightRect — Grid.ColumnSpan=5, vertically centered; SystemAccentColor @ Opacity 0.6.
                    new BoxEl
                    {
                        Width = PresenterWidth, Height = HighlightHeight, AlignSelf = FlexAlign.Start, OffsetY = highlightY,
                        Corners = Radii.ControlAll, Fill = Tok.AccentDefault with { A = 0.6f },
                    },
                    // The three looping columns + 2px spacers, in locale order.
                    new BoxEl
                    {
                        Direction = 0, Width = PresenterWidth, Height = ColumnHeight, AlignSelf = FlexAlign.Start,
                        Children = columns.ToArray(),
                    },
                ],
            };

            // AcceptDismissHostGrid: a 2px top divider + two equal stretched Accept / Dismiss buttons.
            Element BarCell(string glyph, Action onClick, AutomationRole role) => new BoxEl
            {
                Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                Role = role, OnClick = onClick,
                Children = [new TextEl(glyph) { Size = 16f, FontFamily = Theme.IconFont, Color = Tok.TextPrimary }],
            };

            var acceptBar = new BoxEl
            {
                Direction = 1, Width = PresenterWidth, Height = AcceptBarHeight,
                Children =
                [
                    new BoxEl { Height = SpacerWidth, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault },
                    new BoxEl
                    {
                        Direction = 0, Grow = 1f, AlignSelf = FlexAlign.Stretch,
                        Children =
                        [
                            BarCell(Icons.Accept, OnAccept, AutomationRole.Button),
                            BarCell(Icons.Cancel, OnDismiss, AutomationRole.Button),
                        ],
                    },
                ],
            };

            return new BoxEl
            {
                Direction = 1, Width = PresenterWidth, MaxHeight = 398f,
                Children = [pickerHost, acceptBar],
            };
        }
    }

    /// <summary>One looping selector column: a fixed 5-row window centered on the tentative index, with ▲/▼ RepeatButton
    /// step cells revealed at top/bottom (WinUI LoopingSelector RepeatButton model). Click a row to select it.</summary>
    private sealed class DatePickerLoopColumn : Component
    {
        public string[] Options = [];
        public Signal<int> Tentative = new(0);
        public FlexJustify Align = FlexJustify.Center;
        public float Weight = 1f;

        public static Element Create(string[] options, Signal<int> tentative, FlexJustify align, float weight)
            => Embed.Comp(() => new DatePickerLoopColumn { Options = options, Tentative = tentative, Align = align, Weight = weight });

        public override Element Render()
        {
            int n = Options.Length;
            if (n == 0) return new BoxEl { Grow = Weight };

            int sel = ((Tentative.Value % n) + n) % n;   // subscribe + normalize

            // Five rows; the center row (offset 0) is the selected value, ±2 above/below, looping.
            var rows = new Element[VisibleRows];
            for (int r = 0; r < VisibleRows; r++)
            {
                int offset = r - VisibleRows / 2;                 // -2..+2
                int idx = ((sel + offset) % n + n) % n;
                bool isCenter = offset == 0;
                rows[r] = new BoxEl
                {
                    Direction = 0, Height = ItemHeight, AlignItems = FlexAlign.Center, Justify = Align,
                    Padding = new Edges4(Align == FlexJustify.Start ? 9f : 0f, 0, 0, 0),
                    HoverFill = isCenter ? ColorF.Transparent : Tok.FillSubtleSecondary,
                    PressedFill = isCenter ? ColorF.Transparent : Tok.FillSubtleTertiary,
                    Role = AutomationRole.MenuItem,
                    OnClick = MakeSelect(idx),
                    Children = [new TextEl(Options[idx]) { Size = 14f, Color = isCenter ? Tok.TextPrimary : Tok.TextSecondary }],
                };
            }

            // ▲/▼ RepeatButton step cells (Height 22, FontSize 8) revealed at top/bottom of the column on the ZStack.
            BoxEl StepButton(string glyph, float offsetY, int step) => new()
            {
                Direction = 0, Width = Weight, Height = 22f, AlignSelf = FlexAlign.Start, OffsetY = offsetY,
                AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
                Repeats = true, Role = AutomationRole.Button,
                OnClick = () => Tentative.Value = ((Tentative.Peek() + step) % n + n) % n,
                Children = [new TextEl(glyph) { Size = 8f, FontFamily = Theme.IconFont, Color = Tok.TextSecondary }],
            };

            return new BoxEl
            {
                Direction = 1, Grow = Weight, Height = ColumnHeight, ZStack = true, ClipToBounds = true,
                Children =
                [
                    new BoxEl { Direction = 1, Width = Weight, Height = ColumnHeight, AlignSelf = FlexAlign.Start, Children = rows },
                    StepButton(Icons.ChevronUp, 0f, -1),                       // ▲ E70E — step up
                    StepButton(Icons.ChevronDown, ColumnHeight - 22f, +1),     // ▼ E70D — step down
                ],
            };
        }

        private Action MakeSelect(int idx) => () => Tentative.Value = idx;
    }
}
