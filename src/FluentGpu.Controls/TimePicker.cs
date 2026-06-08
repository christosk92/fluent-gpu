using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Scene;
using System;

namespace FluentGpu.Controls;

/// <summary>
/// A WinUI TimePicker: a single bordered row split into three tap targets — Hour | Minute | AM/PM — each separated by a
/// 1px divider. Tapping a field opens a <see cref="MenuFlyout"/> list of its choices (hours 1–12, minutes 00–59, AM/PM);
/// picking a row updates that field's state. Mirrors the DatePicker pattern (a private <see cref="TimePickerField"/>
/// Component that owns the per-field overlay anchor + flyout toggle).
/// </summary>
public sealed class TimePicker : Component
{
    public static Element Create() => Embed.Comp(() => new TimePicker());

    public override Element Render()
    {
        var (hour, setHour) = UseState(9);
        var (minute, setMinute) = UseState(30);
        var (pm, setPm) = UseState(false);

        var hours = new string[12];
        for (int i = 0; i < 12; i++) hours[i] = (i + 1).ToString();

        var minutes = new string[60];
        for (int i = 0; i < 60; i++) minutes[i] = i.ToString("00");

        return new BoxEl
        {
            Direction = 0,
            AlignItems = FlexAlign.Center,
            MinHeight = 32f,
            Corners = Radii.ControlAll,
            BorderWidth = 1f,
            BorderBrush = Tok.ControlElevationBorder,
            Fill = Tok.FillControlDefault,
            ClipToBounds = true,
            Role = AutomationRole.ComboBox,
            Children =
            [
                TimePickerField.Create(hour.ToString(), hours, i => setHour(i + 1)),
                Divider(),
                TimePickerField.Create(minute.ToString("00"), minutes, setMinute),
                Divider(),
                TimePickerField.Create(pm ? "PM" : "AM", ["AM", "PM"], i => setPm(i == 1)),
            ],
        };
    }

    static Element Divider() => new BoxEl
    {
        Width = 1f,
        AlignSelf = FlexAlign.Stretch,
        Fill = Tok.StrokeDividerDefault,
    };
}

/// <summary>One column of a <see cref="TimePicker"/>: a tappable cell showing the current <paramref name="Display"/> value
/// that opens a <see cref="MenuFlyout"/> of <paramref name="Options"/> below it. Captures its own node as the flyout
/// anchor; toggling re-click closes it. File-local — used only by <see cref="TimePicker"/>.</summary>
internal sealed class TimePickerField : Component
{
    public string Display = "";
    public string[] Options = [];
    public Action<int> OnPick = static _ => { };

    public static Element Create(string display, string[] options, Action<int> onPick)
        => Embed.Comp(() => new TimePickerField { Display = display, Options = options, OnPick = onPick });

    public override Element Render()
    {
        var anchor = UseRef<NodeHandle>(default);
        var handle = UseRef<OverlayHandle?>(null);
        var svc = UseContext(Overlay.Service);

        Element Flyout()
        {
            var items = new MenuFlyoutItem[Options.Length];
            for (int i = 0; i < Options.Length; i++)
            {
                int idx = i;
                items[i] = new MenuFlyoutItem(Options[i], Invoke: () => OnPick(idx));
            }
            return MenuFlyout.Build(items, () => handle.Value?.Close());
        }

        void Toggle()
        {
            if (handle.Value is { IsOpen: true } h) { h.Close(); return; }
            handle.Value = svc.Open(() => anchor.Value, Flyout, FlyoutPlacement.BottomLeft);
        }

        return new BoxEl
        {
            Direction = 0,
            AlignSelf = FlexAlign.Stretch,
            AlignItems = FlexAlign.Center,
            Justify = FlexJustify.Center,
            Grow = 1f,
            MinWidth = 54f,
            // WinUI TimePickerHostPadding = 0,3,0,6 (the host text padding). The field is centered (Justify=Center)
            // so horizontal slack is supplied by MinWidth=54; the vertical 3/6 split is kept on the cell. (TextEl has
            // no Padding channel, so this stays on the cell rather than the text run.)
            Padding = new Edges4(0, 3, 0, 6),
            HoverFill = Tok.FillControlSecondary,
            PressedFill = Tok.FillControlTertiary,
            Role = AutomationRole.Button,
            OnRealized = h => anchor.Value = h,
            OnClick = Toggle,
            Children = [new TextEl(Display) { Size = 14f, Color = Tok.TextPrimary }],
        };
    }
}
