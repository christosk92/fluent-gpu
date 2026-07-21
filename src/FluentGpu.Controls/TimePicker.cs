using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Localization;
using FluentGpu.Scene;
using FluentGpu.Signals;
using System;
using System.Globalization;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI TimePicker: a single Button FACE split into Hour | Minute | AM/PM text columns (equal star widths,
/// TimePicker_themeresources.xaml:234-239) separated by 1px vertical spacers, and on tap opens ONE
/// <c>TimePickerFlyout</c> presenter via the <see cref="Overlay"/> service (the whole face is a single FlyoutButton,
/// TimePicker_themeresources.xaml:232). The popup is placed so the centered 40px accent highlight band overlays the
/// face (DateTimePickerFlyoutHelper.cpp:39-71) and is width-matched to the face's realized width
/// (TimePickerFlyout_Partial.cpp:196-200). It hosts side-by-side LOOPING selector columns (item height 40) — the
/// shared <see cref="DateTimeLoopColumn"/> — under the highlight band, with a 41px Accept(E8FB) / Dismiss(E711) bar.
/// <see cref="SelectedTime"/> is nullable (null shows the hour/minute/AM placeholders, the WinUI HasNoTime state);
/// <see cref="ClockIdentifier"/> selects the 12/24-hour clock and <see cref="MinuteIncrement"/> coarsens the minute
/// column.
/// </summary>
public sealed class TimePicker : Component
{
    // ── WinUI TimePicker theme constants (TimePicker_themeresources.xaml). Presenter metrics shared with
    //    DatePicker are single-owned by PickerFlyout (DatePicker.cs).
    private const float FaceMinWidth = 242f;       // TimePickerThemeMinWidth (TimePicker_themeresources.xaml:116)
    private const float FaceMaxWidth = 456f;       // TimePickerThemeMaxWidth (:117)
    private const float FaceMinHeight = 32f;
    private const float PresenterWidth = 242f;     // TimePickerFlyoutPresenter Width/MinWidth fallback (:260-261)
    private const float SpacerWidth = PickerFlyout.SpacerWidth;   // TimePickerSpacerThemeWidth = 1 (:105)
    private const float ColumnHeight = PickerFlyout.ColumnHeight;

    /// <summary>WinUI <c>ClockIdentifier</c> values (TimePicker_Partial.cpp:45 <c>s_strTwelveHourClock</c>).</summary>
    public const string TwelveHourClock = "12HourClock";
    public const string TwentyFourHourClock = "24HourClock";

    // ── Public surface (mirrors the WinUI TimePicker DPs: Time/SelectedTime, ClockIdentifier, MinuteIncrement,
    //    Header, TimeChanged/SelectedTimeChanged). ──
    /// <summary>Caller-owned selected time; null shows the placeholders (HasNoTime, TimePicker_themeresources.xaml:220-225).
    /// A fallback signal is used when null.</summary>
    public Signal<TimeOnly?>? SelectedTime;
    /// <summary>"12HourClock" (default, TimePicker IDL) or "24HourClock" — the 24h clock drops the AM/PM column.</summary>
    public string ClockIdentifier = TwelveHourClock;
    /// <summary>WinUI <c>MinuteIncrement</c> (0-59; 0 behaves as 1): the minute column lists 0, inc, 2·inc, …</summary>
    public int MinuteIncrement = 1;
    /// <summary>WinUI <c>Header</c> — shown above the face (HeaderContentPresenter, TimePicker_themeresources.xaml:231).</summary>
    public string? Header;
    public bool IsEnabled = true;
    public Action<TimeOnly?>? OnChange;

    /// <summary>Zero-arg factory — keeps the existing demo call site (DateTimePages.cs) compiling unchanged.</summary>
    public static Element Create() => Embed.Comp(() => new TimePicker());

    public static Element Create(
        Signal<TimeOnly?> selectedTime,
        string clockIdentifier = TwelveHourClock,
        int minuteIncrement = 1,
        Action<TimeOnly?>? onChange = null,
        string? header = null, bool isEnabled = true)
        => Embed.Comp(() => new TimePicker
        {
            SelectedTime = selectedTime, ClockIdentifier = clockIdentifier, MinuteIncrement = minuteIncrement,
            OnChange = onChange, Header = header, IsEnabled = isEnabled,
        });

    public override Element Render()
    {
        var svc = UseContext(Overlay.Service);
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var fallback = UseSignal<TimeOnly?>(null);
        var time = SelectedTime ?? fallback;
        bool enabled = IsEnabled;
        bool is12h = ClockIdentifier != TwentyFourHourClock;   // TimePicker_Partial.cpp:442 compares against "12HourClock"
        int inc = Math.Clamp(MinuteIncrement, 1, 59);          // 0 behaves as 1 (WinUI MinuteIncrement coercion)
        int minuteCount = 59 / inc + 1;

        // Tentative selection while the flyout is open (committed only on Accept) — same model as DatePicker.
        var tentHour = UseSignal(0);     // 12h: index 0 == "12" (TimePicker_Partial.cpp:1698); 24h: index == hour
        var tentMinute = UseSignal(0);   // index into the increment-stepped minute list
        var tentPeriod = UseSignal(0);   // 0 = AM, 1 = PM

        TimeOnly? committed = time.Value;
        bool hasTime = committed.HasValue;
        var dtf = CultureInfo.CurrentCulture.DateTimeFormat;

        // ── FACE text per column (placeholders "hour"/"minute"/"AM" when no time —
        //    Microsoft.UI.Xaml.Common.rc:430-432 TEXT_TIMEPICKER_*_PLACEHOLDER). ──
        var seed = committed ?? TimeOnly.FromDateTime(DateTime.Now);
        int hour12 = seed.Hour % 12 == 0 ? 12 : seed.Hour % 12;
        string hourText = hasTime ? (is12h ? hour12 : seed.Hour).ToString(CultureInfo.CurrentCulture) : Loc.Get(Strings.TimePicker.Hour);
        string minuteText = hasTime ? seed.Minute.ToString("00", CultureInfo.CurrentCulture) : Loc.Get(Strings.TimePicker.Minute);
        string periodText = hasTime ? (seed.Hour >= 12 ? dtf.PMDesignator : dtf.AMDesignator) : dtf.AMDesignator;
        // HasNoTime → TimePickerButtonForegroundDefault = TextFillColorSecondary (TimePicker_themeresources.xaml:19, :222).
        var faceColor = hasTime ? Tok.TextPrimary : Tok.TextSecondary;

        // Hover → TextFillColorPrimary (:20), pressed → TextFillColorSecondary (:21), disabled → TextFillColorDisabled (:22).
        Element FaceCell(string text) => new BoxEl
        {
            Direction = 0, Grow = 1f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
            Padding = new Edges4(0, 3, 0, 6),   // TimePickerHostPadding (:120)
            Children =
            [
                new TextEl(text)
                {
                    Size = 14f, Color = faceColor,
                    HoverColor = Tok.TextPrimary, PressedColor = Tok.TextSecondary, DisabledColor = Tok.TextDisabled,
                },
            ],
        };

        // Face spacers = TimePickerSpacerFill = ControlStrokeColorDefaultBrush (TimePicker_themeresources.xaml:5);
        // the FLYOUT spacers stay DividerStroke (:26). Disabled uses the same brush (:6).
        Element Spacer() => new BoxEl { Width = SpacerWidth, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeControlDefault };

        // Equal star columns Hour | Minute | (AM/PM) with Auto spacer columns (TimePicker_themeresources.xaml:234-251);
        // the third host collapses on a 24-hour clock (TimePicker_Partial.cpp:1589-1603).
        var faceChildren = is12h
            ? new Element[] { FaceCell(hourText), Spacer(), FaceCell(minuteText), Spacer(), FaceCell(periodText) }
            : new Element[] { FaceCell(hourText), Spacer(), FaceCell(minuteText) };

        void OpenFlyout()
        {
            if (handle.Value is { IsOpen: true } open) { open.Close(); return; }

            // Seed tentatives from the committed time (or now when null — WinUI seeds the sentinel from current time).
            tentHour.Value = is12h ? seed.Hour % 12 : seed.Hour;   // 12h: 12AM/12PM land on index 0 ("12")
            tentMinute.Value = Math.Min(seed.Minute / inc, minuteCount - 1);
            tentPeriod.Value = seed.Hour >= 12 ? 1 : 0;

            // WinUI sizes the presenter to the target's realized width at open (TimePickerFlyout_Partial.cpp:196-200:
            // presenter Width/MinWidth = target ActualWidth) — measure the face once here.
            var sceneNow = Context.Scene;
            RectF faceRect = sceneNow is not null && !anchor.Value.IsNull && sceneNow.IsLive(anchor.Value)
                ? sceneNow.AbsoluteRect(anchor.Value) : default;
            float presenterW = faceRect.W > 0f ? faceRect.W : PresenterWidth;

            Element Body() => Embed.Comp(() => new TimePickerFlyoutBody
            {
                Width = presenterW, Is12h = is12h, MinuteInc = inc,
                TentHour = tentHour, TentMinute = tentMinute, TentPeriod = tentPeriod,
                OnAccept = Commit, OnDismiss = () => handle.Value?.Close(),
            });

            // WinUI positions the flyout so the HighlightRect template part is CENTERED over the target element
            // (DateTimePickerFlyoutHelper.cpp:39-71) — same placement math as DatePicker.
            handle.Value = svc.OpenAt(
                () =>
                {
                    var scene = Context.Scene;
                    var node = anchor.Value;
                    RectF f = scene is not null && !node.IsNull && scene.IsLive(node) ? scene.AbsoluteRect(node) : default;
                    return new RectF(f.X, f.Y + f.H * 0.5f - ColumnHeight * 0.5f, f.W, f.H);
                },
                Body, FlyoutPlacement.OverlapStretch,
                new PopupOptions(FocusTrap: true),
                owner: () => anchor.Value);
        }

        void Commit()
        {
            // 12h: index 0 == "12" → hour24 = index + 12·pm covers 12AM(0)/12PM(12) too (TimePicker_Partial.cpp:913-914).
            int hourIdx = Math.Clamp(tentHour.Peek(), 0, is12h ? 11 : 23);
            int hour = is12h ? hourIdx + (tentPeriod.Peek() == 1 ? 12 : 0) : hourIdx;
            int minute = Math.Min(Math.Clamp(tentMinute.Peek(), 0, minuteCount - 1) * inc, 59);
            var picked = new TimeOnly(hour, minute);
            time.Value = picked;
            OnChange?.Invoke(picked);
            handle.Value?.Close();
        }

        var face = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center,
            MinWidth = FaceMinWidth, MaxWidth = FaceMaxWidth, MinHeight = FaceMinHeight,
            Corners = Radii.ControlAll,
            BorderWidth = 1f,
            // Rest/hover = ControlElevationBorderBrush (TimePicker_themeresources.xaml:9-10); pressed/disabled =
            // ControlStrokeColorDefaultBrush (:11-12).
            BorderBrush = enabled ? Tok.ControlElevationBorder : GradientSpec.Solid(Tok.StrokeControlDefault),
            PressedBorderBrush = GradientSpec.Solid(Tok.StrokeControlDefault),
            // Backgrounds: Default/PointerOver/Pressed = ControlFillColor Default/Secondary/Tertiary (:13-15);
            // disabled = ControlFillColorDisabled (:16).
            Fill = enabled ? Tok.FillControlDefault : Tok.FillControlDisabled,
            HoverFill = Tok.FillControlSecondary, PressedFill = Tok.FillControlTertiary,
            ClipToBounds = true,
            IsEnabled = enabled,
            Role = AutomationRole.ComboBox,
            OnRealized = h => anchor.Value = h,
            OnClick = OpenFlyout,
            Children = faceChildren,
        };

        if (Header is null) return face;

        // HeaderContentPresenter row: Margin = TimePickerTopHeaderMargin 0,0,0,4 (:113), foreground
        // TimePickerHeaderForeground = TextFillColorPrimary (:7) / disabled TextFillColorDisabled (:8).
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

    /// <summary>The TimePickerFlyout popup content: equal-width looping Hour | Minute | (AM/PM) columns
    /// (TimePicker_themeresources.xaml:282-298) under the shared highlight band, over the accept/dismiss bar.
    /// A hook-owning Component so it re-renders the looping columns on each tentative-signal change.</summary>
    private sealed class TimePickerFlyoutBody : Component
    {
        public float Width = PresenterWidth;   // = the face's realized width (TimePickerFlyout_Partial.cpp:196-200)
        public bool Is12h = true;
        public int MinuteInc = 1;
        public Signal<int> TentHour = new(0);
        public Signal<int> TentMinute = new(0);
        public Signal<int> TentPeriod = new(0);
        public Action OnAccept = static () => { };
        public Action OnDismiss = static () => { };

        public override Element Render()
        {
            var hooks = UseContext(InputHooks.Current);
            var colNodes = UseRef<NodeHandle[]>(new NodeHandle[3]);
            int colCount = Is12h ? 3 : 2;

            // Option lists. 12-hour clock time flow is 12, 1, 2 … 11 (TimePicker_Partial.cpp:913-914) — 12AM/12PM
            // are always the first element (:1698); 24h lists 0..23.
            string[] hours;
            if (Is12h)
            {
                hours = new string[12];
                hours[0] = "12";
                for (int i = 1; i < 12; i++) hours[i] = i.ToString(CultureInfo.CurrentCulture);
            }
            else
            {
                hours = new string[24];
                for (int i = 0; i < 24; i++) hours[i] = i.ToString(CultureInfo.CurrentCulture);
            }

            int minuteCount = 59 / MinuteInc + 1;
            var minutes = new string[minuteCount];
            for (int i = 0; i < minuteCount; i++) minutes[i] = (i * MinuteInc).ToString("00", CultureInfo.CurrentCulture);

            var dtf = CultureInfo.CurrentCulture.DateTimeFormat;
            string[] periods = [dtf.AMDesignator, dtf.PMDesignator];

            // Left/Right move focus across the selector columns without wrapping (DateTimePickerFlyoutHelper.cpp:100-123).
            void MoveColumn(int from, int dir)
            {
                int to = from + dir;
                if (to < 0 || to >= colCount) return;
                var n = colNodes.Value[to];
                if (!n.IsNull) hooks.MoveFocusVisual?.Invoke(n);
            }

            Element Column(string[] options, Signal<int> tentative, int i)
            {
                var registered = colNodes;
                Action<NodeHandle> realize = h => registered.Value[i] = h;
                Action<int> move = dir => MoveColumn(i, dir);
                return DateTimeLoopColumn.Create(options, tentative, FlexJustify.Center, 1f, realize, move, OnAccept);
            }

            // Flyout column spacers = TimePickerFlyoutPresenterSpacerFill = DividerStrokeColorDefaultBrush
            // (TimePicker_themeresources.xaml:26), width = TimePickerSpacerThemeWidth 1 (:105).
            Element Spacer() => new BoxEl { Width = SpacerWidth, AlignSelf = FlexAlign.Stretch, Fill = Tok.StrokeDividerDefault };

            var columns = new Element[colCount * 2 - 1];
            columns[0] = Column(hours, TentHour, 0);
            columns[1] = Spacer();
            columns[2] = Column(minutes, TentMinute, 1);
            if (Is12h)
            {
                columns[3] = Spacer();
                columns[4] = Column(periods, TentPeriod, 2);
            }

            // PickerHostGrid: a ZStack with the accent highlight band behind the columns (:281-298).
            var pickerHost = new BoxEl
            {
                Direction = 0, Width = Width, Height = ColumnHeight, ZStack = true, ClipToBounds = true,
                Children =
                [
                    PickerFlyout.HighlightBand(),
                    new BoxEl
                    {
                        Direction = 0, Height = ColumnHeight, AlignSelf = FlexAlign.Start,
                        Children = columns,
                    },
                ],
            };

            // TimePicker's AcceptButton AND DismissButton both use DatePickerFlyoutPresenterDismissMargin = 2,4,4,4
            // (TimePicker_themeresources.xaml:306-307 — the template binds DismissMargin for both).
            var acceptBar = PickerFlyout.AcceptDismissBar(
                Width, OnAccept, OnDismiss, acceptMargin: new Edges4(2, 4, 4, 4), dismissMargin: new Edges4(2, 4, 4, 4));

            return new BoxEl
            {
                Direction = 1, Width = Width, MaxHeight = PickerFlyout.PresenterMaxHeight,
                Children = [pickerHost, acceptBar],
            };
        }
    }
}
