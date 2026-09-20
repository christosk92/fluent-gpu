using System;

namespace FluentGpu.WindowsApi.Shell;

/// <summary>What the user (or the shell) did to a <see cref="NotifyIcon"/>. Delivered through
/// <see cref="NotifyIcon.Activated"/> on the thread that created the icon — its hidden callback window belongs to that
/// thread's message queue, so a UI-thread icon reports on the UI thread.</summary>
public enum NotifyIconEvent : byte
{
    /// <summary><c>NIN_SELECT</c>: a left click, or Enter on a mouse-selected icon.</summary>
    Select,
    /// <summary><c>NIN_KEYSELECT</c>: Enter or Space on an icon selected from the keyboard (Win+B, arrows).</summary>
    KeySelect,
    /// <summary><c>WM_LBUTTONDBLCLK</c>. A double click is preceded by the <see cref="Select"/> of its first click.</summary>
    DoubleClick,
    /// <summary><c>WM_MBUTTONUP</c>: a middle click.</summary>
    MiddleClick,
    /// <summary><c>WM_CONTEXTMENU</c>: a right click, the context-menu key, or Shift+F10. The anchor is where a menu
    /// belongs — the click point, or the icon's top-left for the keyboard.</summary>
    ContextMenu,
    /// <summary>Explorer restarted and the icon has ALREADY been re-added (with its current icon and tip) — re-push
    /// anything else you derived from the shell; the taskbar's DPI or theme may have changed while it was down.</summary>
    Recreated,
    /// <summary>The taskbar's DPI or light/dark changed (<see cref="NotifyIcon.TaskbarDpi"/>,
    /// <see cref="NotifyIcon.TaskbarUsesLightTheme"/> already hold the new values). An icon set with the auto-sized
    /// <see cref="NotifyIcon.SetIcon(string)"/> has already been reloaded at the new size; pick a light/dark variant here.</summary>
    ShellChanged,
}

/// <summary>One row of a <see cref="NotifyIcon.ShowMenu"/> native menu. <paramref name="Id"/> <c>0</c> is a separator
/// (the text is ignored); a negative id is a row that can never be chosen (a caption such as the now-playing line);
/// a chosen row returns its id, which must be <c>1..65535</c> (the Win32 menu command range).
/// <paramref name="Default"/> draws the row bold — the action a double click performs.</summary>
public readonly record struct NotifyMenuItem(int Id, string Text, bool Enabled = true, bool Checked = false, bool Default = false)
{
    /// <summary>A separator row.</summary>
    public static NotifyMenuItem Separator => new(0, string.Empty);
}

/// <summary>The <c>Shell_NotifyIconW</c> version-4 wire protocol the icon speaks, and its pure decode.</summary>
internal static class NotifyIconProtocol
{
    /// <summary>The icon's callback message (<c>WM_APP + 0x100</c>); its own window is the only receiver.</summary>
    public const uint CallbackMessage = 0x8000 + 0x100;
    /// <summary>Posted to the callback window to destroy it from its own thread (a <c>Dispose</c> from elsewhere).</summary>
    public const uint DestroyMessage = 0x8000 + 0x101;

    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETFOCUS = 3, NIM_SETVERSION = 4;
    public const uint NIF_MESSAGE = 0x01, NIF_ICON = 0x02, NIF_TIP = 0x04, NIF_GUID = 0x20, NIF_SHOWTIP = 0x80;
    public const uint NOTIFYICON_VERSION_4 = 4;

    // Version-4 callback events, LOWORD(lParam) (shellapi.h: NIN_SELECT = WM_USER, NINF_KEY = 1).
    public const uint NIN_SELECT = 0x0400, NIN_KEYSELECT = 0x0401;
    public const uint WM_CONTEXTMENU = 0x007B, WM_LBUTTONDBLCLK = 0x0203, WM_MBUTTONUP = 0x0208;

    /// <summary><c>szTip</c> is 128 <c>WCHAR</c>s including the terminator.</summary>
    public const int MaxTipChars = 127;

    /// <summary>Decode a version-4 callback: <c>LOWORD(lParam)</c> is the event, <c>GET_X/Y_LPARAM(wParam)</c> the anchor
    /// in screen px (signed 16-bit each — a monitor left of or above the primary has negative coordinates). False for the
    /// events the icon does not surface (mouse moves, button downs, balloon and popup notifications).</summary>
    public static bool TryDecode(nuint wParam, nint lParam, out NotifyIconEvent e, out int anchorX, out int anchorY)
    {
        anchorX = unchecked((short)(wParam & 0xFFFF));
        anchorY = unchecked((short)((wParam >> 16) & 0xFFFF));
        switch ((uint)((nuint)lParam & 0xFFFF))
        {
            case NIN_SELECT: e = NotifyIconEvent.Select; return true;
            case NIN_KEYSELECT: e = NotifyIconEvent.KeySelect; return true;
            case WM_LBUTTONDBLCLK: e = NotifyIconEvent.DoubleClick; return true;
            case WM_MBUTTONUP: e = NotifyIconEvent.MiddleClick; return true;
            case WM_CONTEXTMENU: e = NotifyIconEvent.ContextMenu; return true;
            default: e = default; return false;
        }
    }

    /// <summary>The tooltip the shell can hold: at most <see cref="MaxTipChars"/> characters, never ending on the first
    /// half of a surrogate pair.</summary>
    public static string ClampTip(string? tip)
    {
        tip ??= string.Empty;
        if (tip.Length <= MaxTipChars) return tip;
        int n = MaxTipChars;
        if (char.IsHighSurrogate(tip[n - 1])) n--;
        return tip.Substring(0, n);
    }
}

/// <summary>The native context menu's shape, as pure decisions over <see cref="NotifyMenuItem"/> rows.</summary>
internal static class NotifyMenuModel
{
    public const uint MF_STRING = 0x0000, MF_GRAYED = 0x0001, MF_CHECKED = 0x0008, MF_SEPARATOR = 0x0800;
    public const uint TPM_LEFTALIGN = 0x0000, TPM_RIGHTBUTTON = 0x0002, TPM_RIGHTALIGN = 0x0008, TPM_BOTTOMALIGN = 0x0020,
                      TPM_VERTICAL = 0x0040, TPM_NONOTIFY = 0x0080, TPM_RETURNCMD = 0x0100;
    public const int MaxCommandId = 0xFFFF;

    /// <summary>A keyboard-invoked menu is anchored at the icon's top-left, not under the cursor; a mouse-invoked one is
    /// anchored at the click, which is (within this slop) where the cursor still is when the menu opens.</summary>
    public const int KeyboardAnchorSlopPx = 4;

    public static bool IsSeparator(in NotifyMenuItem item) => item.Id == 0;

    /// <summary>Only an enabled row with an id in the command range can be chosen.</summary>
    public static bool IsSelectable(in NotifyMenuItem item) => item.Enabled && item.Id is > 0 and <= MaxCommandId;

    /// <summary><c>AppendMenuW</c> flags: a separator, or a string row greyed when it cannot be chosen and checked when asked.</summary>
    public static uint AppendFlags(in NotifyMenuItem item)
    {
        if (IsSeparator(item)) return MF_SEPARATOR;
        uint flags = MF_STRING;
        if (!IsSelectable(item)) flags |= MF_GRAYED;
        if (item.Checked) flags |= MF_CHECKED;
        return flags;
    }

    /// <summary>The native command id: the row's id when it is in the command range, else 0 (a row that returns nothing).</summary>
    public static nuint CommandId(in NotifyMenuItem item) => item.Id is > 0 and <= MaxCommandId ? (nuint)item.Id : 0;

    /// <summary>Position of the first non-separator row marked <see cref="NotifyMenuItem.Default"/>, or -1.</summary>
    public static int DefaultIndex(ReadOnlySpan<NotifyMenuItem> items)
    {
        for (int i = 0; i < items.Length; i++)
            if (items[i].Default && !IsSeparator(items[i])) return i;
        return -1;
    }

    /// <summary><c>TrackPopupMenuEx</c> flags: return the command instead of posting <c>WM_COMMAND</c>, accept the right
    /// button, open ABOVE the anchor (away from a bottom taskbar), horizontally per the user's menu-drop handedness, and
    /// — when the icon's rect is known — keep clear of it vertically first (so a top taskbar gets the menu below).</summary>
    public static uint TrackFlags(bool dropRightAligned, bool excludeIconRect)
        => TPM_RETURNCMD | TPM_NONOTIFY | TPM_RIGHTBUTTON | TPM_BOTTOMALIGN
         | (dropRightAligned ? TPM_RIGHTALIGN : TPM_LEFTALIGN)
         | (excludeIconRect ? TPM_VERTICAL : 0);

    /// <summary>What <c>ShowMenu</c> returns for a <c>TrackPopupMenuEx</c> result: the id of a selectable row it names, else 0.</summary>
    public static int ChosenId(int trackResult, ReadOnlySpan<NotifyMenuItem> items)
    {
        if (trackResult <= 0) return 0;
        for (int i = 0; i < items.Length; i++)
            if (items[i].Id == trackResult && IsSelectable(items[i])) return trackResult;
        return 0;
    }

    /// <summary>True when the menu was opened from the keyboard: the anchor is not where the cursor is.</summary>
    public static bool IsKeyboardInvocation(int anchorX, int anchorY, int cursorX, int cursorY)
        => Math.Abs(anchorX - cursorX) > KeyboardAnchorSlopPx || Math.Abs(anchorY - cursorY) > KeyboardAnchorSlopPx;

    /// <summary><c>NIM_SETFOCUS</c> hands keyboard focus back to the notification area — only after a keyboard-opened menu
    /// was dismissed (Esc), so Win+B navigation continues; a mouse user dismissing with a click elsewhere keeps focus where
    /// they clicked.</summary>
    public static bool ShouldReturnFocus(int chosenId, bool keyboardInvoked) => chosenId == 0 && keyboardInvoked;
}

/// <summary>The shell facts an icon's appearance depends on. The icon re-reads them on every broadcast that might change
/// them and raises <see cref="NotifyIconEvent.ShellChanged"/> only when one actually did.</summary>
internal readonly record struct NotifyIconShellState(uint TaskbarDpi, bool TaskbarLight)
{
    public static bool Changed(in NotifyIconShellState before, in NotifyIconShellState after) => before != after;

    /// <summary>An auto-sized icon must be reloaded only when the frame size the new DPI selects differs.</summary>
    public static bool IconSizeChanged(in NotifyIconShellState before, in NotifyIconShellState after)
        => NotifyIcon.SmallIconSizeForDpi(before.TaskbarDpi) != NotifyIcon.SmallIconSizeForDpi(after.TaskbarDpi);

    /// <summary>The <c>Personalize\SystemUsesLightTheme</c> decision: light only for a successfully read non-zero DWORD;
    /// absent or unreadable is dark (the taskbar was always dark before the value existed). The same rule
    /// <c>FluentGpu.Pal.Windows.Win32Theme</c> applies — restated because the two assemblies are independent peers.</summary>
    public static bool TaskbarLightFromRegistry(int status, uint data) => status == 0 && data != 0;
}

/// <summary>
/// The dark native menu on a dark taskbar. Win32 popup menus are drawn light unless the process opts into the shell's
/// dark menu theme through two UNDOCUMENTED <c>uxtheme.dll</c> exports, by ordinal only: 135
/// <c>SetPreferredAppMode(PreferredAppMode)</c> and 136 <c>FlushMenuThemes()</c> — what Explorer and every dark tray
/// menu use. Before Windows 10 1903 (build 18362) ordinal 135 was a different function (<c>AllowDarkModeForApp(BOOL)</c>),
/// so older builds never take this path. The mode is process-wide, so the icon switches it only around its own
/// <c>TrackPopupMenuEx</c> and restores the previous mode after.
/// </summary>
internal static class DarkMenuPolicy
{
    public const int SetPreferredAppModeOrdinal = 135;
    public const int FlushMenuThemesOrdinal = 136;
    public const int MinimumBuild = 18362;
    /// <summary><c>PreferredAppMode.ForceDark</c> (Default 0, AllowDark 1, ForceDark 2, ForceLight 3).</summary>
    public const int ForceDark = 2;

    public static bool OrdinalsUsable(int osBuild) => osBuild >= MinimumBuild;

    /// <summary>The menu follows the TASKBAR it pops up from, not the app theme.</summary>
    public static bool WantDark(bool taskbarLight) => !taskbarLight;
}
