using System;
using FluentGpu.Pal.Windows;
using FluentGpu.WindowsApi.Shell;
using Xunit;

namespace FluentGpu.Windows.Tests;

/// <summary>
/// The pure decisions behind <see cref="NotifyIcon"/>: the version-4 callback decode, the icon frame a taskbar DPI
/// selects, the native menu's shape, when focus returns to the notification area, when a shell broadcast is a real
/// change, and the dark-menu policy. No window, no shell call.
/// </summary>
public sealed class NotifyIconDecodeTests
{
    private static nuint Anchor(int x, int y) => (nuint)(((uint)(ushort)(short)y << 16) | (ushort)(short)x);
    private static nint Event(uint code, uint iconId = 1) => (nint)((iconId << 16) | code);

    [Theory]
    [InlineData(0x0400u, NotifyIconEvent.Select)]
    [InlineData(0x0401u, NotifyIconEvent.KeySelect)]
    [InlineData(0x0203u, NotifyIconEvent.DoubleClick)]
    [InlineData(0x0208u, NotifyIconEvent.MiddleClick)]
    [InlineData(0x007Bu, NotifyIconEvent.ContextMenu)]
    public void Version4_events_decode_from_the_low_word_of_lParam(uint code, NotifyIconEvent expected)
    {
        Assert.True(NotifyIconProtocol.TryDecode(Anchor(10, 20), Event(code), out NotifyIconEvent e, out _, out _));
        Assert.Equal(expected, e);
    }

    [Theory]
    [InlineData(0x0200u)] // WM_MOUSEMOVE
    [InlineData(0x0201u)] // WM_LBUTTONDOWN
    [InlineData(0x0202u)] // WM_LBUTTONUP
    [InlineData(0x0205u)] // WM_RBUTTONUP (the context menu comes as WM_CONTEXTMENU under version 4)
    [InlineData(0x0406u)] // NIN_POPUPOPEN
    [InlineData(0x0407u)] // NIN_POPUPCLOSE
    [InlineData(0x0402u)] // NIN_BALLOONSHOW
    public void Events_the_icon_does_not_surface_are_ignored(uint code)
        => Assert.False(NotifyIconProtocol.TryDecode(Anchor(0, 0), Event(code), out _, out _, out _));

    [Fact]
    public void The_anchor_is_signed_screen_px_from_wParam_and_the_icon_id_is_ignored()
    {
        Assert.True(NotifyIconProtocol.TryDecode(Anchor(-1280, -40), Event(0x007B, iconId: 7), out _, out int x, out int y));
        Assert.Equal(-1280, x);
        Assert.Equal(-40, y);
    }

    [Fact]
    public void A_tip_is_cut_to_127_chars_and_never_splits_a_surrogate_pair()
    {
        Assert.Equal("Wavee", NotifyIconProtocol.ClampTip("Wavee"));
        Assert.Equal(string.Empty, NotifyIconProtocol.ClampTip(null));
        Assert.Equal(127, NotifyIconProtocol.ClampTip(new string('a', 200)).Length);

        string emojiAtTheCut = new string('a', 126) + "\U0001F3B5" + "tail";   // the pair would occupy 126..127
        string cut = NotifyIconProtocol.ClampTip(emojiAtTheCut);
        Assert.Equal(126, cut.Length);
        Assert.False(char.IsHighSurrogate(cut[^1]));
    }
}

public sealed class NotifyIconFrameTests
{
    [Theory]
    [InlineData(96u, 16)]
    [InlineData(120u, 20)]
    [InlineData(144u, 24)]
    [InlineData(168u, 28)]
    [InlineData(192u, 32)]
    [InlineData(240u, 40)]
    [InlineData(288u, 48)]
    [InlineData(0u, 16)]
    public void The_frame_is_SM_CXSMICON_at_the_taskbar_dpi(uint dpi, int px)
        => Assert.Equal(px, NotifyIcon.SmallIconSizeForDpi(dpi));

    [Fact]
    public void Only_a_frame_size_change_reloads_an_auto_sized_icon()
    {
        var at100 = new NotifyIconShellState(96, TaskbarLight: false);
        Assert.True(NotifyIconShellState.IconSizeChanged(at100, at100 with { TaskbarDpi = 144 }));
        Assert.False(NotifyIconShellState.IconSizeChanged(at100, at100 with { TaskbarLight = true }));
        Assert.False(NotifyIconShellState.IconSizeChanged(at100, at100 with { TaskbarDpi = 97 }));   // still the 16 px frame
    }

    [Fact]
    public void A_broadcast_is_a_shell_change_only_when_a_fact_moved()
    {
        var state = new NotifyIconShellState(120, TaskbarLight: false);
        Assert.False(NotifyIconShellState.Changed(state, state));
        Assert.True(NotifyIconShellState.Changed(state, state with { TaskbarLight = true }));
        Assert.True(NotifyIconShellState.Changed(state, state with { TaskbarDpi = 144 }));
    }
}

public sealed class NotifyMenuModelTests
{
    private static readonly NotifyMenuItem[] Menu =
    [
        new(-1, "Midnight City — M83", Enabled: false),
        NotifyMenuItem.Separator,
        new(1, "Pause"),
        new(2, "Next", Enabled: false),
        new(4, "Save to Liked Songs", Checked: true),
        NotifyMenuItem.Separator,
        new(6, "Open Wavee", Default: true),
        new(8, "Quit Wavee"),
    ];

    [Fact]
    public void Flags_follow_separator_enablement_and_check_state()
    {
        Assert.Equal(NotifyMenuModel.MF_STRING | NotifyMenuModel.MF_GRAYED, NotifyMenuModel.AppendFlags(Menu[0]));
        Assert.Equal(NotifyMenuModel.MF_SEPARATOR, NotifyMenuModel.AppendFlags(Menu[1]));
        Assert.Equal(NotifyMenuModel.MF_STRING, NotifyMenuModel.AppendFlags(Menu[2]));
        Assert.Equal(NotifyMenuModel.MF_STRING | NotifyMenuModel.MF_GRAYED, NotifyMenuModel.AppendFlags(Menu[3]));
        Assert.Equal(NotifyMenuModel.MF_STRING | NotifyMenuModel.MF_CHECKED, NotifyMenuModel.AppendFlags(Menu[4]));
    }

    [Fact]
    public void A_negative_or_out_of_range_id_is_never_selectable_and_sends_no_command()
    {
        Assert.False(NotifyMenuModel.IsSelectable(new NotifyMenuItem(-1, "caption")));
        Assert.Equal((nuint)0, NotifyMenuModel.CommandId(new NotifyMenuItem(-1, "caption")));
        var tooLarge = new NotifyMenuItem(0x1_0000, "too large");
        Assert.False(NotifyMenuModel.IsSelectable(tooLarge));
        Assert.Equal(NotifyMenuModel.MF_STRING | NotifyMenuModel.MF_GRAYED, NotifyMenuModel.AppendFlags(tooLarge));
        Assert.Equal((nuint)6, NotifyMenuModel.CommandId(Menu[6]));
    }

    [Fact]
    public void The_default_row_is_the_first_marked_one_by_position()
    {
        Assert.Equal(6, NotifyMenuModel.DefaultIndex(Menu));
        Assert.Equal(-1, NotifyMenuModel.DefaultIndex([new NotifyMenuItem(0, "", Default: true), new NotifyMenuItem(1, "a")]));
    }

    [Fact]
    public void Only_a_selectable_rows_id_comes_back_from_the_menu()
    {
        Assert.Equal(6, NotifyMenuModel.ChosenId(6, Menu));
        Assert.Equal(0, NotifyMenuModel.ChosenId(0, Menu));    // dismissed
        Assert.Equal(0, NotifyMenuModel.ChosenId(2, Menu));    // a greyed row cannot have been chosen
        Assert.Equal(0, NotifyMenuModel.ChosenId(99, Menu));   // not in this menu
    }

    [Fact]
    public void The_menu_opens_above_the_anchor_per_handedness_and_clears_the_icon_when_its_rect_is_known()
    {
        uint basic = NotifyMenuModel.TPM_RETURNCMD | NotifyMenuModel.TPM_NONOTIFY | NotifyMenuModel.TPM_RIGHTBUTTON
                   | NotifyMenuModel.TPM_BOTTOMALIGN;
        Assert.Equal(basic | NotifyMenuModel.TPM_LEFTALIGN, NotifyMenuModel.TrackFlags(dropRightAligned: false, excludeIconRect: false));
        Assert.Equal(basic | NotifyMenuModel.TPM_RIGHTALIGN, NotifyMenuModel.TrackFlags(dropRightAligned: true, excludeIconRect: false));
        Assert.Equal(basic | NotifyMenuModel.TPM_VERTICAL, NotifyMenuModel.TrackFlags(dropRightAligned: false, excludeIconRect: true));
    }

    [Fact]
    public void Focus_returns_to_the_tray_only_after_a_cancelled_keyboard_menu()
    {
        bool keyboard = NotifyMenuModel.IsKeyboardInvocation(anchorX: 1800, anchorY: 1040, cursorX: 400, cursorY: 300);
        bool mouse = NotifyMenuModel.IsKeyboardInvocation(anchorX: 1800, anchorY: 1040, cursorX: 1802, cursorY: 1039);
        Assert.True(keyboard);
        Assert.False(mouse);

        Assert.True(NotifyMenuModel.ShouldReturnFocus(chosenId: 0, keyboardInvoked: true));
        Assert.False(NotifyMenuModel.ShouldReturnFocus(chosenId: 0, keyboardInvoked: false));
        Assert.False(NotifyMenuModel.ShouldReturnFocus(chosenId: 6, keyboardInvoked: true));
    }
}

public sealed class TaskbarThemeReadTests
{
    [Theory]
    [InlineData(0, 1u, true)]
    [InlineData(0, 0u, false)]
    [InlineData(2, 1u, false)]    // ERROR_FILE_NOT_FOUND: the value predates the light taskbar → dark
    [InlineData(5, 0u, false)]    // ERROR_ACCESS_DENIED
    public void Light_only_for_a_read_non_zero_dword(int status, uint data, bool light)
    {
        Assert.Equal(light, Win32Theme.PersonalizeValueIsLight(status, data));
        Assert.Equal(light, NotifyIconShellState.TaskbarLightFromRegistry(status, data));   // the peer assembly agrees
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public void The_menu_follows_the_taskbar(bool taskbarLight, bool dark)
        => Assert.Equal(dark, DarkMenuPolicy.WantDark(taskbarLight));

    [Theory]
    [InlineData(17763, false)]   // 1809: ordinal 135 is AllowDarkModeForApp(BOOL), a different signature
    [InlineData(18362, true)]    // 1903: SetPreferredAppMode(PreferredAppMode)
    [InlineData(26100, true)]
    public void The_undocumented_ordinals_are_only_used_from_1903(int build, bool usable)
        => Assert.Equal(usable, DarkMenuPolicy.OrdinalsUsable(build));
}
