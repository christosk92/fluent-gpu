using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using FluentGpu.Foundation;
using FluentGpu.Pal;
using FluentGpu.Pal.Windows;
using Xunit;

namespace FluentGpu.Windows.Tests.Scout;

/// <summary>
/// Alt+character chords (an AccessKey, Alt+Enter) must not reach the system-menu tracker: TranslateMessage turns the
/// consumed WM_SYSKEYDOWN into WM_SYSCHAR, and DefWindowProc would answer it with SC_KEYMENU. On a window with no menu
/// bar the tracker finds no mnemonic and plays the default beep. Alt+Space still opens the system menu. A thread WH_CBT
/// hook records (and vetoes, so no menu loop starts) every SC_KEYMENU the real window proc produces.
/// </summary>
public sealed unsafe partial class AltCharSysMenuTests
{
    private const int WH_CBT = 5, HCBT_SYSCOMMAND = 8;
    private const uint WM_SYSCHAR = 0x0106;
    private const nint SC_KEYMENU = 0xF100;
    private const nint AltDownLParam = (1 << 29) | 1;   // KF_ALTDOWN in HIWORD(lParam), repeat count 1

    [ThreadStatic] private static List<nint>? t_keyMenuChars;

    [Fact]
    public void AltCharacterIsConsumed_AltSpaceStillReachesTheSystemMenu()
    {
        List<nint> chars = [];
        Exception? failure = null;
        // A dedicated thread: the window's WM_DESTROY posts WM_QUIT, which must not land on a shared test-runner thread.
        var t = new Thread(() =>
        {
            try { chars = Run(); }
            catch (Exception e) { failure = e; }
        });
        t.Start();
        t.Join();
        if (failure is not null) throw failure;
        Assert.Equal(new nint[] { ' ' }, chars);
    }

    private static List<nint> Run()
    {
        var seen = new List<nint>();
        t_keyMenuChars = seen;
        var win = new Win32Window(new WindowDesc("altchar", new Size2(200, 150), 1f, SkipDropAndTouchpad: true));
        nint hook = 0;
        try
        {
            nint hwnd = win.Handle.Value;
            hook = SetWindowsHookExW(WH_CBT, (nint)(delegate* unmanaged<int, nint, nint, nint>)&CbtProc, 0, GetCurrentThreadId());
            Assert.NotEqual((nint)0, hook);
            SendMessageW(hwnd, WM_SYSCHAR, 'f', AltDownLParam);    // AccessKey Alt+F
            SendMessageW(hwnd, WM_SYSCHAR, '\r', AltDownLParam);   // Alt+Enter
            SendMessageW(hwnd, WM_SYSCHAR, ' ', AltDownLParam);    // Alt+Space: the system menu
            return seen;
        }
        finally
        {
            if (hook != 0) UnhookWindowsHookEx(hook);
            win.Dispose();
            t_keyMenuChars = null;
        }
    }

    [UnmanagedCallersOnly]
    private static nint CbtProc(int code, nint wParam, nint lParam)
    {
        if (code == HCBT_SYSCOMMAND && (wParam & 0xFFF0) == SC_KEYMENU)
        {
            t_keyMenuChars?.Add(lParam);
            return 1;   // veto: never run a system-menu loop inside a test
        }
        return CallNextHookEx(0, code, wParam, lParam);
    }

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW")]
    private static partial nint SetWindowsHookExW(int idHook, nint lpfn, nint hmod, uint threadId);

    [LibraryImport("user32.dll", EntryPoint = "UnhookWindowsHookEx")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnhookWindowsHookEx(nint hook);

    [LibraryImport("user32.dll", EntryPoint = "CallNextHookEx")]
    private static partial nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageW")]
    private static partial nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "GetCurrentThreadId")]
    private static partial uint GetCurrentThreadId();
}
