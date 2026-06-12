using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Threading;
using FluentGpu.WindowsApi.Activation;
using FluentGpu.WindowsApi.Credentials;
using FluentGpu.WindowsApi.Notifications;
using FluentGpu.WindowsApi.Packaging;
using Microsoft.Win32;

namespace FluentGpu;

/// <summary>
/// Headless, console-style validation harness for the four <c>FluentGpu.WindowsApi</c> pillars
/// (<c>Packaging</c> / <c>Credentials</c> / <c>Activation</c> / <c>Notifications</c>). Invoked via
/// <c>--windowsapi-smoke</c> from <c>Program.Main</c> BEFORE the window/GPU stack spins up — it runs as a plain
/// console probe, prints <c>[PASS]</c>/<c>[FAIL]</c> lines in the VerticalSlice style, and returns the failure count
/// as the process exit code (0 = all clean). The companion <c>--windowsapi-smoke-child</c> mode is the second-instance
/// process the single-instance-redirect test spawns (see <see cref="RunChild(string)"/>).
/// </summary>
/// <remarks>
/// <para>
/// This is the <c>--windowsapi-smoke</c> gallery harness the WindowsApi implementation plan places in the gallery app
/// (not the VerticalSlice). It exercises real OS round-trips: it writes and reads the live Credential Locker and the
/// HKCU registry, sends a real cross-process <c>WM_COPYDATA</c>, and submits a real toast (which will visibly pop on a
/// dev box, then auto-expire). Every mutation is reversed so the box is left with no residue.
/// </para>
/// <para>
/// User-interaction assertions are out of scope: a toast <i>click</i> drives the
/// <c>INotificationActivationCallback</c> cold-launch leg which needs a human (and the published AOT exe) — the harness
/// prints a <c>[MANUAL]</c> line for it rather than asserting.
/// </para>
/// </remarks>
// The whole harness only ever runs on Windows (it is the WindowsApp's --windowsapi-smoke mode), and the toast pillar
// it exercises is gated to Windows 10 1507+. Annotating the class makes the registry (windows) and WinRT-toast
// (windows10.0.10240.0) call sites analyzer-clean (CA1416) without per-call suppression; the two Program.Main entry
// points guard with OperatingSystem.IsWindowsVersionAtLeast before calling in.
[SupportedOSPlatform("windows10.0.10240.0")]
internal static partial class WindowsApiSmoke
{
    private static int s_total;
    private static int s_failures;

    /// <summary>The well-known target used by the Credentials round-trip; namespaced so the wildcard
    /// <see cref="CredentialStore.Enumerate(string?)"/> can find it and so it never collides with real app data.</summary>
    private const string CredTarget = "FluentGpu.Smoke.Test";

    /// <summary>The throwaway URI scheme registered/unregistered by the Activation registry round-trip.</summary>
    private const string SmokeScheme = "fluentgpu-smoke";

    /// <summary>The mutex name shared by the parent and the spawned child for the single-instance election.
    /// <c>Local\</c> keeps it per-session (the parent and child share a session).</summary>
    private const string SmokeInstanceId = @"Local\FluentGpu.Smoke.SingleInstance";

    /// <summary>The Win32 class of the parent's message-only receiver window — the child's <c>FindWindowW</c> target.
    /// Stands in for the live host's <c>"FluentGpuWindow"</c>; the receiver mirrors the PAL's <c>WM_COPYDATA</c> case.</summary>
    private const string SmokeReceiverClass = "FluentGpuSmokeReceiver";

    /// <summary>The payload the child forwards to the parent (asserted on receipt, within the 10s timeout).</summary>
    private const string SmokeRedirectPayload = "fluentgpu-smoke://activate?from=child&token=42";

    // ── PASS/FAIL plumbing (VerticalSlice Program.cs:1852 style) ─────────────────────────────────────────────────────

    private static void Check(string name, bool ok, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}{(detail is null ? "" : $"  ({detail})")}");
        s_total++;
        if (!ok) s_failures++;
    }

    private static void Manual(string name, string detail) =>
        Console.WriteLine($"  [MANUAL] {name}  ({detail})");

    private static void Section(string title) => Console.WriteLine($"\n{title}");

    /// <summary>
    /// Run the full four-pillar smoke. Returns the number of failed checks (0 = clean) — used directly as the process
    /// exit code by <c>Program.Main</c>.
    /// </summary>
    public static int Run()
    {
        Console.WriteLine("FluentGpu.WindowsApi — smoke harness (headless; real OS round-trips)\n");
        s_total = 0;
        s_failures = 0;

        try
        {
            PackagingSuite();
            CredentialsSuite();
            ActivationSuite();
            NotificationsSuite();
        }
        catch (Exception ex)
        {
            // A throw from outside an individual Check is itself a failure — surface it rather than crashing the harness.
            Check($"unexpected exception: {ex.GetType().Name}", false, ex.Message);
        }

        Console.WriteLine();
        if (s_failures == 0)
        {
            Console.WriteLine($"WINDOWSAPI SMOKE PASS — {s_total} checks, all four pillars exercised end-to-end.");
            return 0;
        }
        Console.WriteLine($"WINDOWSAPI SMOKE: {s_failures}/{s_total} CHECK(S) FAILED.");
        return s_failures;
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // (1) Packaging — runtime identity queries. Running under `dotnet run`/the bare exe, this process is UNPACKAGED.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void PackagingSuite()
    {
        Section("[1] Packaging — PackageIdentity (unpackaged context)");

        bool packaged = PackageIdentity.IsPackaged;
        Check("1.1 IsPackaged == false (unpackaged process)", packaged == false, $"IsPackaged={packaged}");

        // Every identity getter must return cleanly (null when unpackaged), never throw. Read them all in one probe.
        string? fullName = PackageIdentity.PackageFullName;
        string? familyName = PackageIdentity.PackageFamilyName;
        string? aumid = PackageIdentity.ApplicationUserModelId;
        string? installLoc = PackageIdentity.InstalledLocation;
        Version? version = PackageIdentity.Version;

        Check("1.2 PackageFullName null when unpackaged", fullName is null, $"value={Show(fullName)}");
        Check("1.3 PackageFamilyName null when unpackaged", familyName is null, $"value={Show(familyName)}");
        Check("1.4 ApplicationUserModelId null when unpackaged", aumid is null, $"value={Show(aumid)}");
        Check("1.5 InstalledLocation null when unpackaged", installLoc is null, $"value={Show(installLoc)}");
        Check("1.6 Version null when unpackaged", version is null, $"value={Show(version?.ToString())}");
        // The probe ran without throwing — the getters above already proved that, but assert it explicitly for the report.
        Check("1.7 identity probe returned cleanly (no throw)", true);
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // (2) Credentials — Store/TryRetrieve/Enumerate/Delete round-trip against the live Credential Locker. No residue.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void CredentialsSuite()
    {
        Section("[2] Credentials — CredentialStore round-trip (target \"" + CredTarget + "\")");

        const string userName = "smoke-user";
        byte[] payload = Encoding.UTF8.GetBytes("smoke-secret-é❤-0123456789"); // non-ASCII to prove byte-exactness

        // Pre-clean any residue from a prior aborted run so the round-trip starts from a known-empty state.
        try { CredentialStore.Delete(CredTarget); } catch { /* best-effort */ }

        bool stored = true;
        try { CredentialStore.Store(CredTarget, userName, payload, CredentialScope.LocalMachine); }
        catch (Exception ex) { stored = false; Check("2.1 Store generic credential", false, ex.Message); }
        if (stored) Check("2.1 Store generic credential", true);

        // TryRetrieve round-trip: hit, username equality, payload byte-equality.
        bool got = CredentialStore.TryRetrieve(CredTarget, out string gotUser, out byte[] gotSecret);
        Check("2.2 TryRetrieve finds the stored credential", got);
        Check("2.3 username round-trips", got && gotUser == userName, $"got=\"{gotUser}\"");
        Check("2.4 secret payload is byte-equal", got && BytesEqual(payload, gotSecret),
            got ? $"{gotSecret.Length}B in / {payload.Length}B expected" : "no read");

        // Enumerate must surface it under a trailing-wildcard filter.
        bool foundInEnum = false;
        DateTime lastWritten = default;
        foreach (StoredCredential c in CredentialStore.Enumerate("FluentGpu.Smoke.*"))
        {
            if (c.TargetName == CredTarget)
            {
                foundInEnum = true;
                lastWritten = c.LastWritten;
                break;
            }
        }
        Check("2.5 Enumerate(\"FluentGpu.Smoke.*\") finds it", foundInEnum,
            foundInEnum ? $"lastWritten={lastWritten:u}" : "not in enumeration");

        // Delete removes it; a second delete reports false (already gone).
        bool deleted = CredentialStore.Delete(CredTarget);
        Check("2.6 Delete removes the credential", deleted);

        bool missAfter = !CredentialStore.TryRetrieve(CredTarget, out _, out _);
        Check("2.7 TryRetrieve misses after Delete", missAfter);

        bool secondDelete = CredentialStore.Delete(CredTarget);
        Check("2.8 second Delete returns false (no residue)", secondDelete == false, $"returned={secondDelete}");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // (3) Activation — ActivationArgs.Classify, ProtocolRegistrar registry round-trip, SingleInstance redirect.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void ActivationSuite()
    {
        Section("[3] Activation — ActivationArgs / ProtocolRegistrar / SingleInstanceGate");

        // ── (a) ActivationArgs.Classify against synthetic command lines (launch / protocol / toast forms). ──
        ActivationArgs launch = ActivationArgs.Classify(Array.Empty<string>(), SmokeScheme);
        Check("3.1 Classify(no args) → Launch", launch.Kind == ActivationKind.Launch, $"kind={launch.Kind}");

        string protoUri = SmokeScheme + "://callback?code=abc123";
        ActivationArgs proto = ActivationArgs.Classify(new[] { protoUri }, SmokeScheme);
        bool protoUriOk = proto.TryGetUri(out Uri? parsed) && parsed!.Scheme == SmokeScheme;
        Check("3.2 Classify(scheme URI) → Protocol", proto.Kind == ActivationKind.Protocol && proto.Argument == protoUri,
            $"kind={proto.Kind} arg={proto.Argument}");
        Check("3.3 Protocol activation exposes the parsed URI", protoUriOk, $"uri={parsed}");

        string toastArg = "action=play;trackId=xyz";
        ActivationArgs toast = ActivationArgs.Classify(
            new[] { ActivationArgs.ToastActivatedSentinel + toastArg }, SmokeScheme);
        Check("3.4 Classify(toast sentinel) → ToastActivated", toast.Kind == ActivationKind.ToastActivated,
            $"kind={toast.Kind}");
        Check("3.5 toast activation strips the sentinel prefix", toast.Argument == toastArg, $"arg={toast.Argument}");

        // ── (b) ProtocolRegistrar: register → verify HKCU keys exist → unregister → verify gone. ──
        string exePath = Environment.ProcessPath ?? "unknown.exe";
        string schemeKeyPath = @"Software\Classes\" + SmokeScheme;

        // Start from a clean slate (a prior aborted run could have left the key).
        try { ProtocolRegistrar.UnregisterProtocol(SmokeScheme); } catch { /* best-effort */ }

        bool registered = true;
        try { ProtocolRegistrar.RegisterProtocol(SmokeScheme, exePath, "FluentGpu Smoke Protocol"); }
        catch (Exception ex) { registered = false; Check("3.6 RegisterProtocol writes HKCU keys", false, ex.Message); }

        if (registered)
        {
            bool keyPresent;
            bool hasUrlProtocolMarker = false;
            bool commandHasExe = false;
            using (RegistryKey? schemeKey = Registry.CurrentUser.OpenSubKey(schemeKeyPath))
            {
                keyPresent = schemeKey is not null;
                if (schemeKey is not null)
                    hasUrlProtocolMarker = schemeKey.GetValue("URL Protocol") is not null;
            }
            using (RegistryKey? cmdKey = Registry.CurrentUser.OpenSubKey(schemeKeyPath + @"\shell\open\command"))
            {
                if (cmdKey?.GetValue(null) is string cmd)
                    commandHasExe = cmd.Contains(exePath, StringComparison.OrdinalIgnoreCase) && cmd.Contains("%1");
            }
            Check("3.6 RegisterProtocol writes HKCU\\Software\\Classes\\" + SmokeScheme, keyPresent);
            Check("3.7 \"URL Protocol\" marker value present", hasUrlProtocolMarker);
            Check("3.8 shell\\open\\command = \"<exe>\" \"%1\"", commandHasExe);
        }

        ProtocolRegistrar.UnregisterProtocol(SmokeScheme);
        bool keyGone;
        using (RegistryKey? afterKey = Registry.CurrentUser.OpenSubKey(schemeKeyPath))
            keyGone = afterKey is null;
        Check("3.9 UnregisterProtocol removes the subtree (no residue)", keyGone);

        // ── (c) SingleInstance: parent becomes primary, spawns a child that must detect non-first and redirect. ──
        SingleInstanceRedirectCheck(exePath);
    }

    /// <summary>
    /// End-to-end single-instance redirect: this (parent) process acquires the gate (becomes primary) and stands up a
    /// message-only receiver window mirroring the PAL's <c>WM_COPYDATA</c> case; it then launches the SAME exe with
    /// <c>--windowsapi-smoke-child &lt;payload&gt;</c>. The child calls <see cref="SingleInstanceGate.TryAcquire"/>,
    /// detects it is not first, forwards the payload via <c>WM_COPYDATA</c>, and exits 0. The parent asserts (within 10s)
    /// that the redirect arrived with the exact payload and the child exited 0.
    /// </summary>
    private static void SingleInstanceRedirectCheck(string exePath)
    {
        // Receiver state shared with the WndProc.
        string? received = null;
        using var redirectArrived = new ManualResetEventSlim(false);

        // The parent is the primary: acquire the gate first so the child sees ERROR_ALREADY_EXISTS. Empty payload here
        // (we are not redirecting; we are the target).
        using var parentGate = new SingleInstanceGate();
        bool parentIsPrimary = parentGate.TryAcquire(SmokeInstanceId, SmokeReceiverClass, string.Empty);
        Check("3.10 parent SingleInstanceGate.TryAcquire → primary", parentIsPrimary, $"IsPrimary={parentGate.IsPrimary}");
        if (!parentIsPrimary)
        {
            // A stale primary from a prior run owns the name — cannot run the redirect leg deterministically.
            Check("3.11 single-instance redirect (skipped — name already owned)", false,
                "another instance holds " + SmokeInstanceId);
            return;
        }

        // Stand up the message-only receiver on a dedicated pump thread (so the parent can block-wait on the event).
        using var receiver = new MessageReceiver(SmokeReceiverClass, payload =>
        {
            received = payload;
            redirectArrived.Set();
        });
        if (!receiver.Start(out string? recvError))
        {
            Check("3.11 receiver window created", false, recvError ?? "CreateWindowEx failed");
            return;
        }
        Check("3.11 receiver window created (mirrors PAL WM_COPYDATA case)", true, $"hwnd=0x{receiver.Hwnd:X}");

        // Launch the child: the same exe, child mode, carrying the payload it must forward back.
        int childExit = -1;
        bool childStarted = false;
        try
        {
            var psi = new ProcessStartInfo(exePath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            psi.ArgumentList.Add("--windowsapi-smoke-child");
            psi.ArgumentList.Add(SmokeRedirectPayload);

            using Process? child = Process.Start(psi);
            childStarted = child is not null;
            if (child is not null)
            {
                // Pump our own thread is the receiver's; here we just wait for the redirect + child exit (≤10s total).
                bool arrived = redirectArrived.Wait(TimeSpan.FromSeconds(10));
                Check("3.12 redirect WM_COPYDATA arrived within 10s", arrived);
                Check("3.13 redirected payload matches", arrived && received == SmokeRedirectPayload,
                    arrived ? $"got=\"{received}\"" : "timed out");

                if (!child.WaitForExit(TimeSpan.FromSeconds(10)))
                {
                    try { child.Kill(); } catch { /* best-effort */ }
                    childExit = -2;
                }
                else
                {
                    childExit = child.ExitCode;
                }
            }
        }
        catch (Exception ex)
        {
            Check("3.12 child process launch", false, ex.Message);
        }

        Check("3.14 child started", childStarted, $"exe={exePath}");
        Check("3.15 child exited 0 (detected non-first, redirected, exited)", childExit == 0, $"exitCode={childExit}");
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // (4) Notifications — register AUMID (registry/unpackaged) → build XML → Show → cleanup → unregister.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    private static void NotificationsSuite()
    {
        Section("[4] Notifications — ToastBuilder / AumidRegistration / ToastNotifier.Show");

        // ── (a) ToastBuilder produces well-formed, schema-shaped XML (pure managed — no interop). ──
        string xml = new ToastBuilder()
            .AddText("FluentGpu smoke")
            .AddText("This toast is from the --windowsapi-smoke harness; it will auto-expire.")
            .SetAppLogoOverride("file:///C:/does-not-matter.png", circle: true)
            .AddArgument("action", "smoke")
            .AddButton("Dismiss", "action", "dismiss")
            .BuildXml();

        bool xmlShaped = xml.StartsWith("<toast", StringComparison.Ordinal)
            && xml.EndsWith("</toast>", StringComparison.Ordinal)
            && xml.Contains("<binding template='ToastGeneric'>")
            && xml.Contains("launch='action=smoke'")
            && xml.Contains("hint-crop='circle'");
        Check("4.1 ToastBuilder.BuildXml() produces ToastGeneric XML", xmlShaped, $"{Encoding.UTF8.GetByteCount(xml)}B");
        Check("4.2 XML well-formed (parses)", TryParseXml(xml, out string? parseErr), parseErr);

        // ── (b) AUMID derivation (unpackaged registry mode). ──
        Guid activatorClsid = new("7F1A9C2E-5B3D-4E8A-9C11-FE00DEADBEEF"); // stable smoke CLSID
        ToastNotifier toast = ToastNotifier.Default;

        Check("4.3 IsSupported (== !elevated)", ToastNotifier.IsSupported,
            ToastNotifier.IsSupported ? "not elevated" : "process is ELEVATED — toasts disabled by the OS");

        // Subscribe BEFORE Register so the class object registers REGCLS_MULTIPLEUSE (in-proc repeated callbacks).
        toast.Activated += a => Console.WriteLine($"  [event] toast Activated: arg=\"{a.Argument}\"");

        bool registered = true;
        string aumid = string.Empty;
        try
        {
            toast.Register(activatorClsid, "FluentGpu Smoke", iconPath: null);
            aumid = toast.Aumid;
        }
        catch (Exception ex)
        {
            registered = false;
            Check("4.4 ToastNotifier.Register (AUMID + LocalServer32 + CoRegisterClassObject)", false, ex.Message);
        }

        if (registered)
        {
            Check("4.4 ToastNotifier.Register succeeds", true, $"aumid={aumid}");
            Check("4.5 derived AUMID is non-empty", !string.IsNullOrEmpty(aumid), $"aumid={aumid}");

            // Verify the unpackaged registry registration landed: LocalServer32 under the CLSID, AUMID asset key.
            string clsidKey = $@"Software\Classes\CLSID\{{{activatorClsid.ToString().ToUpperInvariant()}}}\LocalServer32";
            bool localServerWritten;
            using (RegistryKey? k = Registry.CurrentUser.OpenSubKey(clsidKey))
                localServerWritten = k?.GetValue(null) is string cmd
                    && cmd.Contains(AumidRegistration.ToastActivatedArgument, StringComparison.Ordinal);
            Check("4.6 LocalServer32 registered with the activation sentinel", localServerWritten, clsidKey);

            // ── (c) Show: runs the spike-proven WinRT chain. S_OK == platform accepted (NOT a guarantee of a banner:
            //         a user who turned this app's toasts off still gets S_OK here — Setting disambiguates). ──
            bool shown = false;
            string? showErr = null;
            try
            {
                shown = toast.Show(xml);
            }
            catch (Exception ex)
            {
                showErr = ex.Message;
            }
            {
                ToastDeliverySetting setting = shown ? toast.Setting : ToastDeliverySetting.Unknown;
                Check("4.7 ToastNotifier.Show() accepted by the platform", shown,
                    showErr ?? $"setting={setting} (S_OK does not guarantee a visible banner — see ToastDeliverySetting)");
                if (shown)
                    Console.WriteLine("  [note] a toast may have visibly appeared on this dev box; it auto-expires.");
            }

            // Toast-click activation is a human + AOT-cold-launch concern — not asserted here.
            Manual("4.8 toast CLICK activation (INotificationActivationCallback)",
                "requires a user click + the published AOT exe cold-launch; not auto-assertable");

            // ── (d) Cleanup: Unregister deletes the CLSID + AUMID registry subtrees. ──
            try { toast.Unregister(); }
            catch (Exception ex) { Check("4.9 ToastNotifier.Unregister", false, ex.Message); }

            bool clsidGone;
            using (RegistryKey? k = Registry.CurrentUser.OpenSubKey(
                $@"Software\Classes\CLSID\{{{activatorClsid.ToString().ToUpperInvariant()}}}"))
                clsidGone = k is null;
            Check("4.9 Unregister cleans the activator CLSID registry (no residue)", clsidGone);
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────
    // Child mode: the spawned second instance for the single-instance-redirect test.
    // ─────────────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The <c>--windowsapi-smoke-child &lt;payload&gt;</c> entry point: a second instance that must detect the parent
    /// already owns the gate, forward <paramref name="payload"/> to the parent's receiver window via the gate's
    /// <c>WM_COPYDATA</c> redirect, and exit. Exit code: <c>0</c> = correctly detected non-first and redirected;
    /// <c>2</c> = wrongly became primary (the parent's mutex was not observed); <c>3</c> = bad arguments.
    /// </summary>
    public static int RunChild(string payload)
    {
        if (string.IsNullOrEmpty(payload))
            return 3;

        using var gate = new SingleInstanceGate();
        // TryAcquire returns false for a secondary instance AFTER it has already forwarded the payload via WM_COPYDATA.
        bool primary = gate.TryAcquire(SmokeInstanceId, SmokeReceiverClass, payload);
        // Correct outcome for the child: NOT primary (the parent owns the name) ⇒ the redirect was sent ⇒ exit 0.
        return primary ? 2 : 0;
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────────────

    private static string Show(string? s) => s is null ? "null" : $"\"{s}\"";

    private static bool BytesEqual(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (int i = 0; i < a.Length; i++)
            if (a[i] != b[i]) return false;
        return true;
    }

    private static bool TryParseXml(string xml, out string? error)
    {
        try
        {
            var doc = new System.Xml.XmlDocument();
            doc.LoadXml(xml);
            error = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// A message-only (<c>HWND_MESSAGE</c>) Win32 window that receives the single-instance redirect and reconstructs the
    /// payload exactly as the live PAL does (<c>FluentGpu.Windows/Pal/Win32Platform.cs</c> <c>WM_COPYDATA</c> case): it
    /// matches <see cref="SingleInstanceGate.ActivationCopyDataCookie"/> on <c>COPYDATASTRUCT.dwData</c> and copies
    /// <c>cbData/sizeof(char)</c> UTF-16 chars out of <c>lpData</c>. It runs its own message pump on a dedicated thread so
    /// the smoke's main thread can block-wait on the arrival event.
    /// </summary>
    private sealed unsafe partial class MessageReceiver : IDisposable
    {
        private const uint WM_COPYDATA = 0x004A;
        private const uint WM_CLOSE = 0x0010;
        private static readonly nint HWND_MESSAGE = -3;

        [StructLayout(LayoutKind.Sequential)]
        private struct COPYDATASTRUCT { public nuint dwData; public uint cbData; public nint lpData; }

        [StructLayout(LayoutKind.Sequential)]
        private struct WNDCLASSW
        {
            public uint style;
            public nint lpfnWndProc;
            public int cbClsExtra;
            public int cbWndExtra;
            public nint hInstance;
            public nint hIcon;
            public nint hCursor;
            public nint hbrBackground;
            public nint lpszMenuName;
            public nint lpszClassName;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MSG
        {
            public nint hwnd;
            public uint message;
            public nuint wParam;
            public nint lParam;
            public uint time;
            public int ptX;
            public int ptY;
        }

        private delegate nint WndProcDelegate(nint hWnd, uint msg, nuint wParam, nint lParam);

        [LibraryImport("user32.dll", EntryPoint = "RegisterClassW", SetLastError = true)]
        private static partial ushort RegisterClassW(in WNDCLASSW wc);

        [LibraryImport("user32.dll", EntryPoint = "UnregisterClassW", StringMarshalling = StringMarshalling.Utf16)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool UnregisterClassW(string lpClassName, nint hInstance);

        [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        private static partial nint CreateWindowExW(
            uint dwExStyle, string lpClassName, string? lpWindowName, uint dwStyle,
            int x, int y, int w, int h, nint hWndParent, nint hMenu, nint hInstance, nint lpParam);

        [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
        private static partial nint DefWindowProcW(nint hWnd, uint msg, nuint wParam, nint lParam);

        [LibraryImport("user32.dll", EntryPoint = "DestroyWindow")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool DestroyWindow(nint hWnd);

        [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
        private static partial int GetMessageW(out MSG lpMsg, nint hWnd, uint min, uint max);

        [LibraryImport("user32.dll", EntryPoint = "TranslateMessage")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool TranslateMessage(in MSG lpMsg);

        [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
        private static partial nint DispatchMessageW(in MSG lpMsg);

        [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static partial bool PostMessageW(nint hWnd, uint msg, nuint wParam, nint lParam);

        private readonly string _className;
        private readonly Action<string> _onRedirect;
        private readonly WndProcDelegate _wndProc;   // kept alive for the window's lifetime (GC pin)
        private readonly nint _wndProcPtr;
        private Thread? _pumpThread;
        private nint _hwnd;
        private readonly ManualResetEventSlim _ready = new(false);
        private string? _startError;

        public nint Hwnd => _hwnd;

        public MessageReceiver(string className, Action<string> onRedirect)
        {
            _className = className;
            _onRedirect = onRedirect;
            _wndProc = WndProc;
            _wndProcPtr = Marshal.GetFunctionPointerForDelegate(_wndProc);
        }

        public bool Start(out string? error)
        {
            _pumpThread = new Thread(Pump) { IsBackground = true, Name = "WindowsApiSmoke.Receiver" };
            _pumpThread.Start();
            _ready.Wait(TimeSpan.FromSeconds(5));
            error = _startError;
            return _hwnd != 0;
        }

        private void Pump()
        {
            var wc = new WNDCLASSW { lpfnWndProc = _wndProcPtr, lpszClassName = Marshal.StringToHGlobalUni(_className) };
            try
            {
                ushort atom = RegisterClassW(in wc);
                if (atom == 0)
                {
                    _startError = $"RegisterClassW failed: 0x{Marshal.GetLastPInvokeError():X8}";
                    _ready.Set();
                    return;
                }

                // HWND_MESSAGE makes this a message-only window (no UI), still a valid FindWindowW target by class.
                _hwnd = CreateWindowExW(0, _className, null, 0, 0, 0, 0, 0, HWND_MESSAGE, 0, 0, 0);
                if (_hwnd == 0)
                    _startError = $"CreateWindowExW failed: 0x{Marshal.GetLastPInvokeError():X8}";

                _ready.Set();
                if (_hwnd == 0)
                    return;

                // Standard get/translate/dispatch pump until WM_CLOSE (posted by Dispose) breaks GetMessage.
                while (GetMessageW(out MSG msg, 0, 0, 0) > 0)
                {
                    TranslateMessage(in msg);
                    DispatchMessageW(in msg);
                }
            }
            finally
            {
                if (wc.lpszClassName != 0)
                {
                    UnregisterClassW(_className, 0);
                    Marshal.FreeHGlobal(wc.lpszClassName);
                }
            }
        }

        private nint WndProc(nint hWnd, uint msg, nuint wParam, nint lParam)
        {
            if (msg == WM_COPYDATA)
            {
                var cds = (COPYDATASTRUCT*)lParam;
                if (cds is not null && cds->dwData == SingleInstanceGate.ActivationCopyDataCookie)
                {
                    string payload = cds->cbData >= sizeof(char)
                        ? new string((char*)cds->lpData, 0, (int)(cds->cbData / sizeof(char)))
                        : string.Empty;
                    try { _onRedirect(payload); } catch { /* never let a handler exception cross the WndProc */ }
                    return 1;   // TRUE = handled (WM_COPYDATA convention)
                }
                return 0;
            }
            if (msg == WM_CLOSE)
            {
                DestroyWindow(hWnd);
                return 0;
            }
            return DefWindowProcW(hWnd, msg, wParam, lParam);
        }

        public void Dispose()
        {
            nint h = _hwnd;
            if (h != 0)
            {
                PostMessageW(h, WM_CLOSE, 0, 0);
                _pumpThread?.Join(TimeSpan.FromSeconds(2));
                _hwnd = 0;
            }
            _ready.Dispose();
        }
    }
}
