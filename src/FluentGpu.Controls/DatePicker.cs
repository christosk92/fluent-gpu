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
/// <see cref="CultureInfo.CurrentCulture"/>'s ShortDatePattern, month names FULL — WinUI formats the face/flyout with
/// "day month.full year", DatePicker_Partial.cpp:1811) separated by 1px vertical spacers, and on tap opens a
/// <c>DatePickerFlyout</c> popup via the <see cref="Overlay"/> service. The popup is placed so the centered 40px
/// accent highlight band overlays the face (DateTimePickerFlyoutHelper.cpp:39-71) and is width-matched to the face's
/// realized width (DatePickerFlyout_Partial.cpp:202-206). It hosts three side-by-side LOOPING selector columns
/// (item height 40) following the WinUI LoopingSelector RepeatButton model — caret step buttons (EDDB/EDDC) revealed
/// on column hover plus click-to-select — under the highlight band, with a 41px Accept(E8FB) / Dismiss(E711) bar.
/// <see cref="SelectedDate"/> is nullable (a null value shows the day/month/year placeholders). Day/Month/Year columns
/// can each be hidden; the year range defaults to today ±100 years.
/// </summary>
/// <remarks>
/// FIDELITY NOTE: WinUI's LoopingSelector also supports a touch-fling momentum scroll-snap. As of the touch-support
/// Phase 4 the engine DOES carry the snap-points seam (<see cref="FluentGpu.Scene.ScrollSnap"/> — the ScrollPresenter
/// applicable-zone math — plus the <c>ScrollIntegrator</c> fling-retarget that lands a flick exactly on a configured
/// <c>ScrollState.SnapInterval</c> row): any control built on a real virtualized <c>ScrollEl</c> viewport now gets
/// touch-fling-snap-to-row for free (set <c>SnapInterval = itemHeight</c>). This DatePicker column is deliberately NOT
/// a scroll viewport — it is the fixed 9-row RepeatButton + click-to-select model WinUI ALSO ships for the
/// keyboard/mouse path, keyed off a tentative-index signal (no offset to fling). Converting it to a scrolling looping
/// strip is a separate control rewrite; the visual result here — a fixed window, centered selection, up/down repeat
/// buttons — is faithful, and the momentum-snap seam it would consume is the one this phase landed.
/// </remarks>
public sealed class DatePicker : Component
{
    // ── WinUI DatePicker theme constants (DatePicker_themeresources.xaml). The presenter metrics shared with
    //    TimePicker (item/highlight/accept-bar heights, 1px spacers, the 357px column window, step buttons) are
    //    single-owned by <see cref="PickerFlyout"/> below.
    private const float FaceMinWidth = 296f;       // DatePickerThemeMinWidth (DatePicker_themeresources.xaml:117)
    private const float FaceMaxWidth = 456f;       // DatePickerThemeMaxWidth (:118)
    private const float FaceMinHeight = 32f;
    private const float PresenterWidth = 296f;     // DatePickerFlyoutPresenter Width/MinWidth fallback (:261-262)
    private const float SpacerWidth = PickerFlyout.SpacerWidth;
    private const float ColumnHeight = PickerFlyout.ColumnHeight;
    // 5-column grid weights: DayColumn 78* | MonthColumn 132* | YearColumn 78* (DatePicker_themeresources.xaml:285-289).
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
    /// <summary>WinUI <c>Header</c> — shown above the face (HeaderContentPresenter, DatePicker_themeresources.xaml:237).</summary>
    public string? Header;
    public bool IsEnabled = true;
    public Action<DateOnly?>? OnChange;

    /// <summary>Zero-arg factory — keeps the existing demo call site (DateTimePages.cs) compiling unchanged.</summary>
    public static Element Create() => Embed.Comp(() => new DatePicker());

    public static Element Create(
        Signal<DateOnly?> selectedDate,
        bool dayVisible = true, bool monthVisible = true, bool yearVisible = true,
        int? minYear = null, int? maxYear = null,
        Action<DateOnly?>? onChange = null,
        string? header = null, bool isEnabled = true)
        => Embed.Comp(() => new DatePicker
        {
            SelectedDate = selectedDate,
            DayVisible = dayVisible, MonthVisible = monthVisible, YearVisible = yearVisible,
            MinYear = minYear, MaxYear = maxYear, OnChange = onChange,
            Header = header, IsEnabled = isEnabled,
        });

    private enum Part : byte { Day, Month, Year }

    public override Element Render()
    {
        var svc = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var fallback = UseSignal<DateOnly?>(null);
        var date = SelectedDate ?? fallback;
        bool enabled = IsEnabled;

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

        var monthNames = MonthNames();

        // ── FACE text per column (placeholder day/month/year when no date). ──
        var seed = committed ?? DateOnly.FromDateTime(DateTime.Now);
        string dayText = hasDate ? seed.Day.ToString(CultureInfo.CurrentCulture) : "day";
        string monthText = hasDate ? monthNames[seed.Month - 1] : "month";
        string yearText = hasDate ? seed.Year.ToString(CultureInfo.CurrentCulture) : "year";
        // HasNoDate → DatePickerButtonForegroundDefault = TextFillColorSecondary (DatePicker_themeresources.xaml:25, :228).
        var faceColor = hasDate ? Tok.TextPrimary : Tok.TextSecondary;

        // Hover → TextFillColorPrimary (:26), pressed → TextFillColorSecondary (:27), disabled → TextFillColorDisabled (:28).
        Element FaceText(string text) => new TextEl(text)
        {
            Size = 14f, Color = faceColor,
            HoverColor = Tok.TextPrimary, PressedColor = Tok.TextSecondary, DisabledColor = Tok.TextDisabled,
        };

        Element FaceCell(Part p) => p switch
        {
            Part.Day => new BoxEl
            {
                Direction = 0, Grow = DayWeight, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Padding = new Edges4(0, 3, 0, 6),   // DatePickerHostPadding (:121)
                Children = [FaceText(dayText)],
            },
            Part.Month => new BoxEl
            {
                Direction = 0, Grow = MonthWeight, AlignItems = FlexAlign.Center, Justify = FlexJustify.Start,
                Padding = new Edges4(9, 3, 0, 6),   // DatePickerHostMonthPadding (:122)
                Children = [FaceText(monthText)],
            },
            _ => new BoxEl
            {
                Direction = 0, Grow = YearWeight, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                Padding = new Edges4(0, 3, 0, 6),
                Children = [FaceText(yearText)],
            },
        };

        // Face spacers = DatePickerSpacerFill = ControlStrokeColorDefaultBrush (DatePicker_themeresources.xaml:11);
        // the FLYOUT spacers stay DividerStroke (:32). Disabled uses the same brush (:12).
        Element Spacer() => new BoxEl { Width = SpacerWidth, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeControlDefault };

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

            // WinUI sizes the presenter to the target's realized width at open (DatePickerFlyout_Partial.cpp:202-206:
            // presenter Width/MinWidth = target ActualWidth) — measure the face once here.
            var sceneNow = Context.Scene;
            RectF faceRect = sceneNow is not null && !anchor.Value.IsNull && sceneNow.IsLive(anchor.Value)
                ? sceneNow.AbsoluteRect(anchor.Value) : default;
            float presenterW = faceRect.W > 0f ? faceRect.W : PresenterWidth;

            Element Body() => Embed.Comp(() => new DatePickerFlyoutBody
            {
                Width = presenterW,
                FirstYear = firstYear, YearCount = yearCount, FullMonthNames = monthNames, Order = order,
                TentYear = tentYear, TentMonth = tentMonth, TentDay = tentDay,
                OnAccept = Commit, OnDismiss = () => handle.Value?.Close(),
            });

            // WinUI positions the flyout so the HighlightRect template part is CENTERED over the target element
            // (DateTimePickerFlyoutHelper.cpp:39-71: target center − highlight center). The highlight band sits at the
            // vertical center of the 357px column host, so popup top = face centerY − ColumnHeight/2. OverlapStretch
            // places the popup's top-left at the (pre-shifted) anchor origin, clamped into the container.
            handle.Value = svc.OpenAt(
                () =>
                {
                    var scene = Context.Scene;
                    var node = anchor.Value;
                    RectF f = scene is not null && !node.IsNull && scene.IsLive(node) ? scene.AbsoluteRect(node) : default;
                    return new RectF(f.X, f.Y + f.H * 0.5f - ColumnHeight * 0.5f, f.W, f.H);
                },
                Body, FlyoutPlacement.OverlapStretch,
                // FocusTrap: keyboard model lives in the flyout — focus moves to the first looping column on open and
                // Tab cycles inside (the WinUI picker flyout owns focus until accepted/dismissed).
                new PopupOptions(FocusTrap: true),
                owner: () => anchor.Value);
        }

        void Commit()
        {
            int year = firstYear + Math.Clamp(tentYear.Peek(), 0, yearCount - 1);
            int month = Math.Clamp(tentMonth.Peek(), 0, 11) + 1;
            int maxDay = DateTime.DaysInMonth(year, month);
            int day = Math.Clamp(tentDay.Peek() + 1, 1, maxDay);
            var picked = new DateOnly(year, month, day);
            date.Value = picked;
            OnChange?.Invoke(picked);
            handle.Value?.Close();
        }

        var face = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center,
            MinWidth = FaceMinWidth, MaxWidth = FaceMaxWidth, MinHeight = FaceMinHeight,
            Corners = Radii.ControlAll,
            BorderWidth = 1f,
            // Rest/hover = ControlElevationBorderBrush (:15-16); pressed = ControlStrokeColorDefaultBrush (:17);
            // disabled = ControlStrokeColorDefaultBrush (:18).
            BorderBrush = enabled ? Tok.ControlElevationBorder : GradientSpec.Solid(Tok.StrokeControlDefault),
            PressedBorderBrush = GradientSpec.Solid(Tok.StrokeControlDefault),
            // Backgrounds: Default/PointerOver/Pressed = ControlFillColor Default/Secondary/Tertiary (:19-21);
            // disabled = ControlFillColorDisabled (:22).
            Fill = enabled ? Tok.FillControlDefault : Tok.FillControlDisabled,
            HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
            ClipToBounds = true,
            IsEnabled = enabled,
            Role = AutomationRole.ComboBox,
            OnRealized = h => anchor.Value = h,
            OnClick = OpenFlyout,
            Children = faceChildren.ToArray(),
        };

        if (Header is null) return face;

        // HeaderContentPresenter row: Margin = DatePickerTopHeaderMargin 0,0,0,4 (:113), foreground
        // DatePickerHeaderForeground = TextFillColorPrimary (:13) / disabled TextFillColorDisabled (:14).
        return new BoxEl
        {
            Direction = 1,
            Children =
            [
                new TextEl(Header)
                {
                    Size = 14f, Color = enabled ? Tok.TextPrimary : Tok.TextDisabled,
                    Margin = new Edges4(0, 0, 0, 4), MaxWidth = FaceMaxWidth, Wrap = TextWrap.Wrap,
                },
                face,
            ],
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

    private static string[] MonthNames()
    {
        // FULL month names — WinUI's default MonthFormat is {month.full} ("day month.full year",
        // DatePicker_Partial.cpp:1811). MonthNames has 13 entries (index 12 is the empty extra month) — take 12.
        var names = CultureInfo.CurrentCulture.DateTimeFormat.MonthNames;
        var months = new string[12];
        for (int i = 0; i < 12; i++) months[i] = names[i];
        return months;
    }

    /// <summary>The DatePickerFlyout popup content (inner panel only — OverlayHost's FlyoutSurface supplies the acrylic
    /// backdrop, stroke, shadow and clip). A hook-owning Component so it re-renders the looping columns on each
    /// tentative-signal change.</summary>
    private sealed class DatePickerFlyoutBody : Component
    {
        public float Width = PresenterWidth;   // = the face's realized width (DatePickerFlyout_Partial.cpp:202-206)
        public int FirstYear;
        public int YearCount;
        public string[] FullMonthNames = [];
        public Part[] Order = [];
        public Signal<int> TentYear = new(0);
        public Signal<int> TentMonth = new(0);
        public Signal<int> TentDay = new(0);
        public Action OnAccept = static () => { };
        public Action OnDismiss = static () => { };

        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            var colNodes = UseRef<NodeHandle[]>(new NodeHandle[3]);

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

            // Left/Right move focus across the selector columns without wrapping (WinUI
            // DateTimePickerFlyoutHelper.cpp:100-123 picks the prev/next selector in layout order).
            void MoveColumn(int from, int dir)
            {
                int to = from + dir;
                if (to < 0 || to >= Order.Length) return;
                var n = colNodes.Value[to];
                if (!n.IsNull) hooks.MoveFocusVisual?.Invoke(n);
            }

            Element ColumnFor(Part p, int i)
            {
                var registered = colNodes;
                Action<NodeHandle> realize = h => registered.Value[i] = h;
                Action<int> move = dir => MoveColumn(i, dir);
                return p switch
                {
                    Part.Day => DateTimeLoopColumn.Create(days, TentDay, FlexJustify.Center, DayWeight, realize, move, OnAccept),
                    Part.Month => DateTimeLoopColumn.Create(FullMonthNames, TentMonth, FlexJustify.Start, MonthWeight, realize, move, OnAccept),
                    _ => DateTimeLoopColumn.Create(years, TentYear, FlexJustify.Center, YearWeight, realize, move, OnAccept),
                };
            }

            // Flyout column spacers = DatePickerFlyoutPresenterSpacerFill = DividerStrokeColorDefaultBrush
            // (DatePicker_themeresources.xaml:32), width = DatePickerSpacerThemeWidth 1 (:6).
            Element Spacer() => new BoxEl { Width = SpacerWidth, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault };

            var columns = new System.Collections.Generic.List<Element>(Order.Length * 2 - 1);
            for (int i = 0; i < Order.Length; i++)
            {
                if (i > 0) columns.Add(Spacer());
                columns.Add(ColumnFor(Order[i], i));
            }

            // PickerHostGrid: a ZStack with the accent highlight band behind the columns.
            var pickerHost = new BoxEl
            {
                Direction = 0, Width = Width, Height = ColumnHeight, ZStack = true, ClipToBounds = true,
                Children =
                [
                    PickerFlyout.HighlightBand(),
                    // The looping columns + 1px spacers, in locale order (fills the stack).
                    new BoxEl
                    {
                        Direction = 0, Height = ColumnHeight, AlignSelf = FlexAlign.Start,
                        Children = columns.ToArray(),
                    },
                ],
            };

            // Accept E8FB Margin 4,4,2,4 / Dismiss E711 Margin 2,4,4,4 (DatePickerFlyoutPresenterAccept/DismissMargin,
            // DatePicker_themeresources.xaml:123-124, :305-306).
            var acceptBar = PickerFlyout.AcceptDismissBar(
                Width, OnAccept, OnDismiss, acceptMargin: new Edges4(4, 4, 2, 4), dismissMargin: new Edges4(2, 4, 4, 4));

            return new BoxEl
            {
                Direction = 1, Width = Width, MaxHeight = PickerFlyout.PresenterMaxHeight,
                Children = [pickerHost, acceptBar],
            };
        }
    }

}

/// <summary>Shared DatePicker/TimePicker flyout-presenter metrics + chrome — the single owner of the values both
/// presenters restate in WinUI (DatePicker_themeresources.xaml / TimePicker_themeresources.xaml define identical
/// item/highlight/accept-bar heights, 1px spacers and accept/dismiss buttons; the LoopingSelector step buttons live
/// in DateTimePickerFlyout_themeresources.xaml). File-internal to keep the two pickers from drifting apart.</summary>
internal static class PickerFlyout
{
    internal const float PresenterMaxHeight = 398f;   // presenter MaxHeight (DatePicker_themeresources.xaml:263/:276; TimePicker_themeresources.xaml:262/:274)
    internal const float ItemHeight = 40f;            // ...FlyoutPresenterItemHeight (DatePicker:115; TimePicker:118)
    internal const float HighlightHeight = 40f;       // ...FlyoutPresenterHighlightHeight (DatePicker:114; TimePicker:114)
    internal const float AcceptBarHeight = 41f;       // ...AcceptDismissHostGridHeight (DatePicker:116; TimePicker:115)
    internal const float SpacerWidth = 1f;            // Date/TimePickerSpacerThemeWidth (DatePicker:6; TimePicker:105)
    // The looping columns fill the presenter above the accept bar: 398 − 41 = 357px (star-sized row, DatePicker:279).
    internal const float ColumnHeight = PresenterMaxHeight - AcceptBarHeight;   // 357
    internal const int VisibleRows = 9;               // 9 × 40 = 360 ≥ 357 — partial top/bottom rows, like WinUI's fill
    internal const float StepButtonHeight = 34f;      // LoopingSelectorUpDownButtonHeight (DateTimePickerFlyout_themeresources.xaml:76)
    internal const string CaretUp8 = "";        // LoopingSelector UpButton Content (DateTimePickerFlyout_themeresources.xaml:210)
    internal const string CaretDown8 = "";      // LoopingSelector DownButton Content (:211)
    internal const string AcceptGlyph = "";     // AcceptButton Content (DatePicker_themeresources.xaml:305; TimePicker:306)

    /// <summary>HighlightRect — ColumnSpan all, VerticalAlignment=Center, Height 40, Margin 4,2,4,2, CornerRadius
    /// ControlCornerRadius, Background = ...FlyoutPresenterHighlightFill = AccentAAFillColorDefaultBrush — the OPAQUE
    /// theme-shifted accent (SystemAccentColorLight2 dark / Dark1 light, Deprecated_themeresources_any.xaml:22/:48)
    /// == Tok.AccentDefault (DatePicker_themeresources.xaml:293/:33; TimePicker_themeresources.xaml:289/:27).</summary>
    internal static Element HighlightBand() => new BoxEl
    {
        Height = HighlightHeight, Margin = new Edges4(4, 2, 4, 2), AlignSelf = FlexAlign.Center,
        Corners = Radii.ControlAll, Fill = Tok.AccentDefault,
    };

    /// <summary>AcceptDismissHostGrid (DatePicker_themeresources.xaml:299-307; TimePicker_themeresources.xaml:300-308):
    /// 1px top divider + two equal inset buttons — Accept E8FB / Dismiss E711, FontSize 16, Padding 4, CornerRadius
    /// ControlCornerRadius, background ramp SubtleFillColor Transparent→Secondary→Tertiary
    /// (DateTimePickerFlyout_themeresources.xaml:5-7). Margins are per-picker (the callers cite theirs).</summary>
    internal static Element AcceptDismissBar(float width, Action onAccept, Action onDismiss,
                                             Edges4 acceptMargin, Edges4 dismissMargin)
    {
        Element BarCell(string glyph, Action onClick, Edges4 margin) => new BoxEl
        {
            Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Margin = margin, Padding = Edges4.All(4f), Corners = Radii.ControlAll,
            Fill = Tok.FillSubtleTransparent, HoverFill = Tok.FillSubtleSecondary, PressedFill = Tok.FillSubtleTertiary,
            Role = AutomationRole.Button, OnClick = onClick,
            Children = [new TextEl(glyph) { Size = 16f, FontFamily = Theme.IconFont, Color = Tok.TextPrimary }],
        };

        return new BoxEl
        {
            Direction = 1, Width = width, Height = AcceptBarHeight,
            Children =
            [
                // 1px divider: Rectangle Height={...SpacerThemeWidth} Fill=...FlyoutPresenterSpacerFill =
                // DividerStrokeColorDefaultBrush (DatePicker_themeresources.xaml:304/:32; TimePicker:305/:26).
                new BoxEl { Height = SpacerWidth, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault },
                new BoxEl
                {
                    Direction = 0, Grow = 1f, AlignSelf = FlexAlign.Stretch,
                    Children =
                    [
                        BarCell(AcceptGlyph, onAccept, acceptMargin),
                        BarCell(Icons.Cancel, onDismiss, dismissMargin),
                    ],
                },
            ],
        };
    }
}

/// <summary>One looping selector column (shared by <see cref="DatePicker"/> and <see cref="TimePicker"/>): a fixed
/// 9-row window centered on the tentative index, with caret RepeatButton step cells revealed at top/bottom on column
/// hover (the WinUI LoopingSelector PointerOver state, DateTimePickerFlyout_themeresources.xaml:197-206). Click a row
/// to select it; Up/Down step, Left/Right move focus across columns, Enter/Space accept
/// (DatePickerFlyout_Partial.cpp:378-390).</summary>
internal sealed class DateTimeLoopColumn : Component
{
    public string[] Options = [];
    public Signal<int> Tentative = new(0);
    public FlexJustify Align = FlexJustify.Center;
    public float Weight = 1f;
    public Action<NodeHandle>? OnColumnRealized;
    public Action<int>? OnMoveColumn;   // dir: -1 left / +1 right
    public Action? OnCommit;

    public static Element Create(string[] options, Signal<int> tentative, FlexJustify align, float weight,
                                 Action<NodeHandle>? onRealized = null, Action<int>? onMoveColumn = null,
                                 Action? onCommit = null)
        => Embed.Comp(() => new DateTimeLoopColumn
        {
            Options = options, Tentative = tentative, Align = align, Weight = weight,
            OnColumnRealized = onRealized, OnMoveColumn = onMoveColumn, OnCommit = onCommit,
        });

    public override Element Render()
    {
        int n = Options.Length;
        if (n == 0) return new BoxEl { Grow = Weight };

        int sel = ((Tentative.Value % n) + n) % n;   // subscribe + normalize
        // Step buttons are Collapsed until the SELECTOR is PointerOver (LoopingSelector template PointerOver
        // state flips UpButton/DownButton Visibility, DateTimePickerFlyout_themeresources.xaml:197-206). Hover
        // routes to the deepest interactive node, so every part funnels into this signal (the ScrollBar lane idiom).
        var hovered = UseSignal(false);
        bool showSteps = hovered.Value;

        void Step(int delta) => Tentative.Value = ((Tentative.Peek() + delta) % n + n) % n;

        Action<Point2> hoverOn = _ => { if (!hovered.Peek()) hovered.Value = true; };
        Action hoverOff = () => { if (hovered.Peek()) hovered.Value = false; };

        // Nine rows; the center row (offset 0) is the selected value, ±4 above/below, looping.
        var rows = new Element[PickerFlyout.VisibleRows];
        for (int r = 0; r < PickerFlyout.VisibleRows; r++)
        {
            int offset = r - PickerFlyout.VisibleRows / 2;    // -4..+4
            int idx = ((sel + offset) % n + n) % n;
            bool isCenter = offset == 0;
            // LoopingSelectorItem: the 40px slot hosts an inset plate — Margin 4,2,4,2
            // (LoopingSelectorItemMargin, DateTimePickerFlyout_themeresources.xaml:74), CornerRadius
            // ControlCornerRadius (:222/:226), hover/press bg SubtleSecondary/Tertiary (:24-25), foreground
            // TextPrimary at rest (:19) / TextSecondary pressed (:22). The center row renders in
            // TextOnAccentAAFillColorPrimary over the opaque highlight band (#000 dark / #FFF light,
            // Deprecated_themeresources_any.xaml:7/:33 — what WinUI's MonochromaticOverlayPresenter produces,
            // DatePicker_themeresources.xaml:294-296).
            rows[r] = new BoxEl
            {
                Direction = 0, Height = PickerFlyout.ItemHeight,
                Children =
                [
                    new BoxEl
                    {
                        Direction = 0, Grow = 1f, Margin = new Edges4(4, 2, 4, 2), Corners = Radii.ControlAll,
                        AlignItems = FlexAlign.Center, Justify = Align,
                        Padding = new Edges4(Align == FlexJustify.Start ? 9f : 0f, 0, 0, 0),
                        HoverFill = isCenter ? ColorF.Transparent : Tok.FillSubtleSecondary,
                        PressedFill = isCenter ? ColorF.Transparent : Tok.FillSubtleTertiary,
                        Role = AutomationRole.MenuItem,
                        OnClick = MakeSelect(idx),
                        OnHoverMove = hoverOn, OnPointerExit = hoverOff,
                        Children =
                        [
                            new TextEl(Options[idx])
                            {
                                Size = 14f,
                                Color = isCenter ? Tok.TextOnAccentPrimary : Tok.TextPrimary,
                                PressedColor = isCenter ? Tok.TextOnAccentPrimary : Tok.TextSecondary,
                            },
                        ],
                    },
                ],
            };
        }

        // Caret RepeatButton step cells (EDDB/EDDC FontSize 8, Height 34) pinned to the column's top/bottom on
        // the ZStack. Backplate = LoopingSelectorUpDownButtonBackground = AcrylicBackgroundFillColorDefaultBrush
        // (DateTimePickerFlyout_themeresources.xaml:16); hover/press change only the FOREGROUND
        // (TextSecondary→TextPrimary, :13-15; bg states are SubtleFillColorTransparent, :17-18); pressed scale
        // 0.875 (LoopingSelectorUpDownButtonScalePressed :77, :152-159 — WinUI scales the glyph TextBlock; the
        // engine's PressScale rides the clickable node).
        BoxEl StepButton(string glyph, bool top, int step) => new()
        {
            Key = top ? "step-up" : "step-down",
            Direction = 0, Height = PickerFlyout.StepButtonHeight, AlignSelf = top ? FlexAlign.Start : FlexAlign.End,
            AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Acrylic = Tok.AcrylicFlyout,
            PressScale = 0.875f,
            Repeats = true, Role = AutomationRole.Button,
            OnClick = () => Step(step),
            OnHoverMove = hoverOn, OnPointerExit = hoverOff,
            Children =
            [
                new TextEl(glyph)
                {
                    Size = 8f, FontFamily = Theme.IconFont,
                    Color = Tok.TextSecondary, HoverColor = Tok.TextPrimary, PressedColor = Tok.TextPrimary,
                },
            ],
        };

        var children = new Element[showSteps ? 3 : 1];
        // The 9×40=360px row stack, vertically centered in the 357px window (−1.5px overhang each side).
        children[0] = new BoxEl
        {
            Key = "rows",
            Direction = 1, Height = PickerFlyout.VisibleRows * PickerFlyout.ItemHeight, AlignSelf = FlexAlign.Start,
            OffsetY = (PickerFlyout.ColumnHeight - PickerFlyout.VisibleRows * PickerFlyout.ItemHeight) * 0.5f,
            Children = rows,
        };
        if (showSteps)
        {
            children[1] = StepButton(PickerFlyout.CaretUp8, top: true, -1);      // ▲ EDDB — step up (:210)
            children[2] = StepButton(PickerFlyout.CaretDown8, top: false, +1);   // ▼ EDDC — step down (:211)
        }

        return new BoxEl
        {
            Direction = 1, Grow = Weight, Height = PickerFlyout.ColumnHeight, ZStack = true, ClipToBounds = true,
            Focusable = true,
            OnHoverMove = hoverOn, OnPointerExit = hoverOff,
            // Keyboard model (DatePickerFlyout_Partial.cpp:378-390 + DateTimePickerFlyoutHelper.cpp:100-123):
            // Up/Down step the looping value; Left/Right move focus to the adjacent column; Enter/Space confirm.
            OnKeyDown = e =>
            {
                if (e.Handled) return;
                switch (e.KeyCode)
                {
                    case Keys.Up: Step(-1); e.Handled = true; break;
                    case Keys.Down: Step(+1); e.Handled = true; break;
                    case Keys.Left: OnMoveColumn?.Invoke(-1); e.Handled = true; break;
                    case Keys.Right: OnMoveColumn?.Invoke(+1); e.Handled = true; break;
                    case Keys.Enter:
                    case Keys.Space: OnCommit?.Invoke(); e.Handled = true; break;
                }
            },
            OnRealized = h => OnColumnRealized?.Invoke(h),
            Children = children,
        };
    }

    private Action MakeSelect(int idx) => () => Tentative.Value = idx;
}
