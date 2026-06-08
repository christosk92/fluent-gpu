using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using System;
using System.Collections.Generic;
using System.Globalization;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI DatePicker: three side-by-side fields (Month | Day | Year) inside a single bordered, pill-shaped box,
/// separated by 1px dividers. Each field opens a small <see cref="MenuFlyout"/> list to pick a value, and the box
/// shows the chosen date. Selecting a value rebuilds the held <see cref="DateOnly"/> (clamping the day to the new
/// month's length so e.g. switching to February from the 31st stays valid).
/// </summary>
public sealed class DatePicker : Component
{
    public static Element Create() => Embed.Comp(() => new DatePicker());

    public override Element Render()
    {
        var (date, setDate) = UseState(DateOnly.FromDateTime(DateTime.Now));

        // Month names: AbbreviatedMonthNames has 13 entries (index 12 is the empty extra month) — take the first 12.
        var fmt = CultureInfo.InvariantCulture.DateTimeFormat;
        var months = new string[12];
        for (int i = 0; i < 12; i++) months[i] = fmt.AbbreviatedMonthNames[i];

        var days = new string[31];
        for (int i = 0; i < 31; i++) days[i] = (i + 1).ToString(CultureInfo.InvariantCulture);

        int nowYear = DateTime.Now.Year;
        int firstYear = nowYear - 50;
        int yearCount = 61; // (now-50)..(now+10) inclusive.
        var years = new string[yearCount];
        for (int i = 0; i < yearCount; i++) years[i] = (firstYear + i).ToString(CultureInfo.InvariantCulture);

        static int DaysInMonth(int year, int month) => DateTime.DaysInMonth(year, month);

        static DateOnly Rebuild(int year, int month, int day)
        {
            int max = DaysInMonth(year, month);
            if (day > max) day = max;
            return new DateOnly(year, month, day);
        }

        Element Divider() => new BoxEl { Width = 1f, Height = 16f, Fill = Tok.StrokeDividerDefault };

        var monthField = PickerField.Create(
            months[date.Month - 1], months,
            i => setDate(Rebuild(date.Year, i + 1, date.Day)));

        var dayField = PickerField.Create(
            days[date.Day - 1], days,
            i => setDate(Rebuild(date.Year, date.Month, i + 1)));

        var yearField = PickerField.Create(
            years[date.Year - firstYear], years,
            i => setDate(Rebuild(firstYear + i, date.Month, date.Day)));

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            Corners = Radii.ControlAll,
            BorderWidth = 1f,
            BorderBrush = Tok.ControlElevationBorder,
            Fill = Tok.FillControlDefault,
            ClipToBounds = true,
            Children = new Element[]
            {
                monthField,
                Divider(),
                dayField,
                Divider(),
                yearField,
            },
        };
    }

    /// <summary>One column of the DatePicker: a clickable cell showing <see cref="Display"/> that, on click, opens a
    /// <see cref="MenuFlyout"/> of <see cref="Options"/> anchored below it; picking row <c>i</c> invokes <see cref="OnPick"/>(i).</summary>
    private sealed class PickerField : Component
    {
        public string Display = "";
        public IReadOnlyList<string> Options = [];
        public Action<int> OnPick = static _ => { };

        public static Element Create(string display, IReadOnlyList<string> options, Action<int> onPick)
            => Embed.Comp(() => new PickerField { Display = display, Options = options, OnPick = onPick });

        public override Element Render()
        {
            var svc = UseContext(Overlay.Service);
            var anchor = UseRef<NodeHandle>(default);
            var h = UseRef<OverlayHandle?>(null);

            void Toggle()
            {
                if (h.Value is { IsOpen: true } o) { o.Close(); return; }
                var items = new List<MenuFlyoutItem>(Options.Count);
                for (int i = 0; i < Options.Count; i++)
                {
                    int ii = i;
                    items.Add(new MenuFlyoutItem(Options[i], null, true, () => OnPick(ii)));
                }
                h.Value = svc.Open(() => anchor.Value, () => MenuFlyout.Build(items, () => h.Value?.Close()), FlyoutPlacement.BottomLeft);
            }

            return new BoxEl
            {
                Direction = 0,
                AlignItems = FlexAlign.Center,
                Padding = new Edges4(12, 7, 12, 7),
                HoverFill = Tok.FillSubtleSecondary,
                PressedFill = Tok.FillSubtleTertiary,
                Role = AutomationRole.ComboBox,
                OnRealized = x => anchor.Value = x,
                OnClick = Toggle,
                Children = new Element[]
                {
                    new TextEl(Display) { Size = 14f, Color = Tok.TextPrimary },
                },
            };
        }
    }
}
