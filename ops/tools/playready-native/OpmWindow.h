// OpmWindow.h — the hidden "virtual video window" the media engine's output protection (OPM) is tied to (F264).
//
// WHY. The runtime's one media engine is windowless (EnableWindowlessSwapchainMode): it presents into a DirectComposition
// swap chain and owns no window, so a licence's output-protection rules (HDCP, image constriction, a downscaled picture)
// have nothing to attest against and cannot follow the monitor the video is really on. Chromium's MediaFoundationRenderer
// gives the engine a hidden, layered, never-shown top-level window as MF_MEDIA_ENGINE_OPM_HWND and keeps it over the video's
// output rect with SetWindowPos; Firefox ships without one. This is the Chromium half, as hardening: no measurement says
// Spotify's licences carry output-protection rules, so nothing here may fail or slow a video (a window that cannot be
// created leaves the engine exactly as it was).
//
// THREADING. A window needs a message pump or a cross-thread SetWindowPos / broadcast would wait on it forever, and the
// runtime thread is a work queue that never pumps. The window therefore lives on its OWN tiny thread that does nothing but
// pump; Place posts the move to it (SWP_ASYNCWINDOWPOS), so the UI thread that calls Place never waits for anyone. The
// engine reads the HWND once, at creation, so the window must exist before the engine does (Start returns when it does).
// The pump thread also follows the host: a window that moves WITHOUT a video pump (a drag, a same-DPI monitor hop, maximize /
// restore) never reaches Place, so an out-of-context WinEvent hook (EVENT_OBJECT_LOCATIONCHANGE, delivered asynchronously
// on the pump thread, the UI thread never waits) re-derives the screen rect from the host Place last recorded and moves the
// window when it changed. Without it the OPM would keep attesting against the monitor the video was on before the move.
//
// No Media Foundation, COM or WinRT in here: the geometry is a pure function (OpmScreenRect) tests/FeedTests.cpp pins.
#pragma once

#ifndef NOMINMAX
#define NOMINMAX
#endif
#include <windows.h>

#include <algorithm>
#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <memory>
#include <mutex>
#include <thread>

// The only window code in this DLL: build.cmd links WindowsApp.lib, which is the app-model subset of the OS, so the desktop
// window functions are named explicitly (the same way PrInternal.h names ole32 / propsys).
#pragma comment(lib, "user32.lib")
#pragma comment(lib, "gdi32.lib")

namespace fgpr {

/// The virtual window's screen rectangle for a video at client-area rect (left, top, right, bottom) of a host window whose
/// client origin is `clientOriginOnScreen`: the rect moved into screen space, never smaller than 1x1 (a zero-sized window
/// is a window the OPM cannot place on a monitor).
inline RECT OpmScreenRect(POINT clientOriginOnScreen, int32_t left, int32_t top, int32_t right, int32_t bottom)
{
    RECT r;
    r.left = clientOriginOnScreen.x + left;
    r.top = clientOriginOnScreen.y + top;
    r.right = r.left + std::max<int32_t>(1, right - left);
    r.bottom = r.top + std::max<int32_t>(1, bottom - top);
    return r;
}

class OpmWindow
{
public:
    OpmWindow() = default;
    OpmWindow(const OpmWindow&) = delete;
    OpmWindow& operator=(const OpmWindow&) = delete;
    ~OpmWindow() { Stop(); }

    /// Create the window on its own pump thread and wait (bounded, 2 s) for it. S_OK when the window exists; otherwise the
    /// failing HRESULT and no window (the caller carries on without OPM_HWND). Runtime thread, once, before any Place.
    HRESULT Start()
    {
        if (m_state) return S_OK;
        auto st = std::make_shared<State>();
        try { m_thread = std::thread(&OpmWindow::ThreadMain, st); }
        catch (...) { return E_OUTOFMEMORY; }
        m_state = st;   // set after the thread exists and never reset: Place reads it from other threads
        std::unique_lock<std::mutex> lk(st->mx);
        if (!st->cv.wait_for(lk, std::chrono::seconds(2), [&] { return st->created; })) return HRESULT_FROM_WIN32(ERROR_TIMEOUT);
        return st->hr;
    }

    /// The window, or null when none was created or it was stopped.
    HWND Handle() const
    {
        if (!m_state) return nullptr;
        std::lock_guard<std::mutex> g(m_state->mx);
        return m_state->hwnd;
    }

    /// Move the window over a video at client-area rect (left, top, right, bottom) of `host`, and remember both so the window
    /// follows the host when it moves (see THREADING). Any thread, never blocks on the pump thread. S_OK when the move was
    /// posted, S_FALSE when there is no window (never created / stopped), a failing HRESULT for a host that is not a window.
    HRESULT Place(HWND host, int32_t left, int32_t top, int32_t right, int32_t bottom, RECT* placed = nullptr) const
    {
        HWND w = Handle();
        if (!w) return S_FALSE;
        POINT origin{ 0, 0 };
        if (!host || !IsWindow(host) || !ClientToScreen(host, &origin)) return HRESULT_FROM_WIN32(ERROR_INVALID_WINDOW_HANDLE);
        const RECT r = OpmScreenRect(origin, left, top, right, bottom);
        if (placed) *placed = r;
        {
            std::lock_guard<std::mutex> g(m_state->mx);
            m_state->host = host;
            m_state->cl = left; m_state->ct = top; m_state->cr = right; m_state->cb = bottom;
            m_state->hasHost = true;
        }
        // HWND_BOTTOM + no activation: the window is never shown, but it must never be able to take z-order or focus either.
        if (!SetWindowPos(w, HWND_BOTTOM, r.left, r.top, r.right - r.left, r.bottom - r.top,
                          SWP_NOACTIVATE | SWP_NOOWNERZORDER | SWP_ASYNCWINDOWPOS))
            return HRESULT_FROM_WIN32(GetLastError());
        {
            std::lock_guard<std::mutex> g(m_state->mx);
            m_state->lastScreen = r;   // what the follow hook compares against, so it moves the window only on a real change
        }
        return S_OK;
    }

    /// True the first time it is called: the one-time log gate for the first placement.
    bool FirstPlacement() const { return m_state && !m_state->placedLogged.exchange(true, std::memory_order_acq_rel); }

    /// Destroy the window and end the pump thread (bounded, 1 s; a thread that does not end is detached and finishes on its
    /// own: it touches only its shared State). Idempotent; runtime thread (teardown).
    void Stop()
    {
        if (!m_state) return;
        HWND w = nullptr;
        {
            std::lock_guard<std::mutex> g(m_state->mx);
            w = m_state->hwnd;
            m_state->hwnd = nullptr;
            m_state->stopped = true;   // a window created after a timed-out Start destroys itself
        }
        if (w) PostMessageW(w, WM_CLOSE, 0, 0);
        if (m_thread.joinable())
        {
            if (WaitForSingleObject((HANDLE)m_thread.native_handle(), 1000) == WAIT_OBJECT_0) m_thread.join();
            else m_thread.detach();
        }
    }

private:
    struct State
    {
        std::mutex mx;
        std::condition_variable cv;
        HWND hwnd = nullptr;
        HRESULT hr = S_OK;
        bool created = false;
        bool stopped = false;
        // The host Place last recorded, the video's client rect in it, and the screen rect the window was last moved to.
        HWND host = nullptr;
        int32_t cl = 0, ct = 0, cr = 0, cb = 0;
        bool hasHost = false;
        RECT lastScreen{ 0, 0, 0, 0 };
        std::atomic<bool> placedLogged{ false };
    };

    /// The pump thread's State: a WINEVENTPROC has no user-data parameter, and the hook is installed by (and only ever called
    /// on) that thread.
    static State*& ThreadState()
    {
        static thread_local State* st = nullptr;
        return st;
    }

    /// EVENT_OBJECT_LOCATIONCHANGE, on the pump thread: when the host window Place recorded moved, move the OPM window to
    /// where the same client rect now is on screen. It is the window's own thread, so the move needs no async flag.
    static void CALLBACK OnLocationChange(HWINEVENTHOOK, DWORD, HWND hwnd, LONG idObject, LONG idChild, DWORD, DWORD)
    {
        if (idObject != OBJID_WINDOW || idChild != CHILDID_SELF) return;
        State* st = ThreadState();
        if (!st) return;
        HWND own = nullptr;
        int32_t l = 0, t = 0, r = 0, b = 0;
        {
            std::lock_guard<std::mutex> g(st->mx);
            if (!st->hasHost || !st->hwnd || hwnd != st->host) return;
            own = st->hwnd;
            l = st->cl; t = st->ct; r = st->cr; b = st->cb;
        }
        POINT origin{ 0, 0 };
        if (!IsWindow(hwnd) || !ClientToScreen(hwnd, &origin)) return;   // a host that is gone is nothing to follow
        const RECT rc = OpmScreenRect(origin, l, t, r, b);
        {
            std::lock_guard<std::mutex> g(st->mx);
            if (EqualRect(&rc, &st->lastScreen)) return;
            st->lastScreen = rc;
        }
        SetWindowPos(own, HWND_BOTTOM, rc.left, rc.top, rc.right - rc.left, rc.bottom - rc.top, SWP_NOACTIVATE | SWP_NOOWNERZORDER);
    }

    static constexpr const wchar_t* kClassName = L"FluentGpuPlayReadyOpmWindow";

    static LRESULT CALLBACK WndProc(HWND h, UINT m, WPARAM wp, LPARAM lp)
    {
        if (m == WM_DESTROY) { PostQuitMessage(0); return 0; }
        return DefWindowProcW(h, m, wp, lp);
    }

    static void ThreadMain(std::shared_ptr<State> st)
    {
        HRESULT hr = S_OK;
        HWND w = nullptr;
        HINSTANCE inst = nullptr;
        GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT,
                           reinterpret_cast<LPCWSTR>(&OpmWindow::WndProc), &inst);

        // The class is never unregistered: a runtime rebuilt while the previous one's pump thread is still finishing would
        // otherwise race the unregister against its own CreateWindow, and one registered class costs nothing.
        WNDCLASSEXW wc{};
        wc.cbSize = sizeof(wc);
        wc.style = CS_OWNDC;
        wc.lpfnWndProc = &OpmWindow::WndProc;
        wc.hInstance = inst;
        wc.hbrBackground = static_cast<HBRUSH>(GetStockObject(BLACK_BRUSH));
        wc.lpszClassName = kClassName;
        if (!RegisterClassExW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) hr = HRESULT_FROM_WIN32(GetLastError());
        if (SUCCEEDED(hr))
        {
            // Chromium's flags (media_foundation_renderer.cc InitializeVirtualVideoWindow): layered + transparent + no
            // redirection bitmap, a disabled popup that is never shown.
            w = CreateWindowExW(WS_EX_NOPARENTNOTIFY | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_NOREDIRECTIONBITMAP, kClassName, L"",
                                WS_POPUP | WS_DISABLED | WS_CLIPSIBLINGS, 0, 0, 1, 1, nullptr, nullptr, inst, nullptr);
            if (!w) hr = HRESULT_FROM_WIN32(GetLastError());
        }
        // The follow hook (out of context: delivered to this thread's message loop, never inline in the mover's thread). Installed
        // before Start returns; a hook that cannot be installed only loses the follow, never the window.
        HWINEVENTHOOK hook = nullptr;
        if (w)
        {
            ThreadState() = st.get();
            hook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE, nullptr, &OpmWindow::OnLocationChange,
                                   GetCurrentProcessId(), 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNTHREAD);
        }
        bool destroyNow = false;
        {
            std::lock_guard<std::mutex> g(st->mx);
            st->hr = hr;
            if (w && st->stopped) destroyNow = true;
            else st->hwnd = w;
            st->created = true;
        }
        st->cv.notify_all();
        if (!w) return;
        if (destroyNow) DestroyWindow(w);   // posts WM_QUIT through WM_DESTROY, so the loop below ends at once

        MSG msg;
        while (GetMessageW(&msg, nullptr, 0, 0) > 0)
        {
            TranslateMessage(&msg);
            DispatchMessageW(&msg);
        }
        if (hook) UnhookWinEvent(hook);
        ThreadState() = nullptr;
    }

    std::shared_ptr<State> m_state;
    std::thread m_thread;
};

}   // namespace fgpr
