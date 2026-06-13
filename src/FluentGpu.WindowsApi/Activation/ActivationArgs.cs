using System;

namespace FluentGpu.WindowsApi.Activation;

/// <summary>
/// How this process was activated, mirroring the values WASDK casts from the platform
/// <c>Windows.ApplicationModel.Activation.ActivationKind</c> (a 1:1 copy: <c>File=3</c>, <c>Protocol=4</c>,
/// <c>StartupTask=39</c> — <c>dev/AppLifecycle/AppInstance.cpp:485-491</c> casts <c>ActivationKind → ExtendedActivationKind</c>,
/// and the kind values 0..1026 "mirror <c>ActivationKind</c> verbatim"; only <c>Push=5000</c>/<c>AppNotification=5001</c>
/// are WASDK additions, docs/plans/windowsapi-implementation-research.md §2.2). FluentGpu keeps the byte-sized subset it
/// actually classifies from the command line for an unpackaged process; the spelled-out numeric values document the
/// shared lineage even though we own both writer and parser.
/// </summary>
public enum ActivationKind : byte
{
    /// <summary>Plain launch (double-click / shortcut) — no protocol, file, or toast on the command line.</summary>
    Launch = 0,

    /// <summary>A file association handed us a path (<c>shell\open\command %1</c> = a file).</summary>
    File = 3,

    /// <summary>A <c>scheme://…</c> deep link (the WAVEE OAuth callback / inter-app launch).</summary>
    Protocol = 4,

    /// <summary>Auto-start at logon (the <c>HKCU\…\CurrentVersion\Run</c> key, <c>ActivationRegistrationManager.h:15</c>).</summary>
    Startup = 39,

    /// <summary>The Shell relaunched us because the user clicked a toast. The argument is the toast's
    /// <c>launch=</c>/button <c>arguments=</c> string (see <see cref="ActivationArgs.ToastActivatedSentinel"/>).</summary>
    ToastActivated = 50,
}

/// <summary>
/// The classified activation of the current process: the <see cref="Kind"/> plus its single string
/// <see cref="Argument"/> (a URI for <see cref="ActivationKind.Protocol"/>, a path for <see cref="ActivationKind.File"/>,
/// the toast args string for <see cref="ActivationKind.ToastActivated"/>, empty for <see cref="ActivationKind.Launch"/>).
/// Cold path — produced once at startup and once per single-instance redirect (see <c>SingleInstanceGate</c>); allocation
/// is fine. FluentGpu uses its own command-line convention (<c>app.exe "scheme://…"</c>) rather than WASDK's
/// <c>----ms-protocol:&lt;uri&gt;</c> envelope (<c>ActivationRegistrationManager.h:10-12</c>) because we own both the
/// registry writer (<see cref="ProtocolRegistrar"/>) and this parser
/// (docs/plans/windowsapi-implementation-research.md §2.2).
/// </summary>
public readonly record struct ActivationArgs(ActivationKind Kind, string Argument)
{
    /// <summary>The Shell-toast relaunch sentinel WASDK writes into <c>LocalServer32</c> and matches on the command line
    /// (<c>c_toastActivatedArgument = "----AppNotificationActivated:"</c>, <c>AppNotificationManager.cpp:218</c>; the
    /// activator strips the prefix and parses what follows). FluentGpu recognizes the same <c>----…Activated:</c> shape so
    /// a cold-launched toast click classifies as <see cref="ActivationKind.ToastActivated"/>.</summary>
    public const string ToastActivatedSentinel = "----AppNotificationActivated:";

    /// <summary>True when this is a <see cref="ActivationKind.Protocol"/> activation whose argument parses as an absolute
    /// URI; <paramref name="uri"/> is the parsed value (or <c>null</c> when this returns false).</summary>
    public bool TryGetUri(out Uri? uri)
    {
        if (Kind == ActivationKind.Protocol && Uri.TryCreate(Argument, UriKind.Absolute, out uri))
            return true;
        uri = null;
        return false;
    }

    /// <summary>The current process's classified activation: <see cref="Environment.GetCommandLineArgs"/> (skipping argv[0],
    /// the exe path) classified against <paramref name="scheme"/>. Convenience over <see cref="Classify"/>.
    /// <para>
    /// OPEN-QUESTION(#4): this is the UNPACKAGED path (command-line only) — the doc-recommended first build. A PACKAGED
    /// (<c>Windows.FullTrustApplication</c>) protocol launch is NOT guaranteed to deliver the URI on the command line
    /// (WASDK prefers the platform <c>GetActivatedEventArgs</c> when packaged, <c>AppInstance.cpp:485-491</c>); a future
    /// MSIX build must call the platform WinRT <c>GetActivatedEventArgs</c> via the cold ABI for
    /// <see cref="ActivationKind.Protocol"/>/<see cref="ActivationKind.File"/>, gated on
    /// <see cref="FluentGpu.WindowsApi.Packaging.PackageIdentity.IsPackaged"/>. Live-validate on the target OS build
    /// before shipping packaged (docs/plans/windowsapi-implementation-research.md §5 #4).
    /// </para></summary>
    public static ActivationArgs FromCurrentProcess(string scheme)
    {
        string[] argv = Environment.GetCommandLineArgs();
        return Classify(argv.AsSpan(argv.Length > 0 ? 1 : 0), scheme);
    }

    /// <summary>
    /// Classify a process command line (argv WITHOUT argv[0]) into a <see cref="ActivationArgs"/>. Precedence:
    /// (1) a toast relaunch — any token beginning <see cref="ToastActivatedSentinel"/> → <see cref="ActivationKind.ToastActivated"/>
    /// with the post-sentinel text as the argument; (2) a <paramref name="scheme"/>-matching absolute URI →
    /// <see cref="ActivationKind.Protocol"/>; (3) an existing-looking file path token → <see cref="ActivationKind.File"/>;
    /// (4) otherwise <see cref="ActivationKind.Launch"/>. Scheme match is case-insensitive (URI schemes are
    /// case-insensitive, RFC 3986 §3.1).
    /// </summary>
    public static ActivationArgs Classify(ReadOnlySpan<string> argv, string scheme)
    {
        // (1) Toast relaunch sentinel — recognized regardless of position, the prefix is stripped.
        foreach (string a in argv)
        {
            if (a.StartsWith(ToastActivatedSentinel, StringComparison.Ordinal))
                return new ActivationArgs(ActivationKind.ToastActivated, a[ToastActivatedSentinel.Length..]);
        }

        // (2) A scheme://… deep link. URI scheme compare is ASCII case-insensitive.
        foreach (string a in argv)
        {
            if (Uri.TryCreate(a, UriKind.Absolute, out Uri? uri) &&
                string.Equals(uri.Scheme, scheme, StringComparison.OrdinalIgnoreCase))
                return new ActivationArgs(ActivationKind.Protocol, a);
        }

        // (3) A file path the shell handed us (file association). Conservative: a rooted path or one that exists.
        foreach (string a in argv)
        {
            if (a.Length == 0 || a[0] == '-' || a[0] == '/') continue;   // skip switches / the toast sentinel form
            if (LooksLikeFilePath(a))
                return new ActivationArgs(ActivationKind.File, a);
        }

        return new ActivationArgs(ActivationKind.Launch, string.Empty);
    }

    private static bool LooksLikeFilePath(string a)
    {
        // Rooted ("C:\…", "\\server\share", "/x") or an existing file/directory on disk. Avoids classifying a bare word.
        if (System.IO.Path.IsPathRooted(a)) return true;
        try { return System.IO.File.Exists(a) || System.IO.Directory.Exists(a); }
        catch { return false; }   // malformed path chars — not a file activation
    }
}
