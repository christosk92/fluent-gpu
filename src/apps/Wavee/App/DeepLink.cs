using System.Runtime.InteropServices;
using FluentGpu;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>A parsed <c>wavee://</c> verb. Unknown or garbage input never produces one (the parser never throws).</summary>
public readonly record struct DeepLinkVerb(DeepLinkKind Kind, string Route, string Arg, string Context);

/// <summary>The <c>wavee://</c> verbs. Navigation keys are opaque strings the shell owns — see the skill doc.</summary>
public enum DeepLinkKind : byte { Open, Play, Resume, Pause }

/// <summary>
/// <c>wavee://</c> parser + bring-to-front. Consumption (navigate / play / resume) is the shell's job — this type only
/// turns a raw activation string into a <see cref="DeepLinkVerb"/> or restores the main window.
/// </summary>
public static partial class DeepLink
{
    const int SwShow = 5;
    const int SwRestore = 9;

    /// <summary>Parse <paramref name="raw"/> as a <c>wavee://</c> verb. Accepts a bare URI or a command line that
    /// contains one. Percent-encoding is decoded. Returns <c>false</c> for unknown verbs, missing required args, or
    /// garbage — never throws.</summary>
    public static bool TryParse(string? raw, out DeepLinkVerb verb)
    {
        verb = default;
        if (!TryExtractUri(raw, out string text)) return false;
        if (!Uri.TryCreate(text, UriKind.Absolute, out Uri? uri)) return false;
        if (!string.Equals(uri.Scheme, "wavee", StringComparison.OrdinalIgnoreCase)) return false;

        string name = uri.Host;
        if (name.Length == 0)
        {
            string path = uri.AbsolutePath.Trim('/');
            int slash = path.IndexOf('/');
            name = slash < 0 ? path : path[..slash];
        }
        if (name.Length == 0) return false;

        ReadQuery(uri.Query, out string route, out string arg, out string ctx);

        if (name.Equals("open", StringComparison.OrdinalIgnoreCase))
        {
            if (route.Length == 0) return false;
            verb = new DeepLinkVerb(DeepLinkKind.Open, route, arg, "");
            return true;
        }
        if (name.Equals("play", StringComparison.OrdinalIgnoreCase))
        {
            if (ctx.Length == 0) return false;
            verb = new DeepLinkVerb(DeepLinkKind.Play, "", "", ctx);
            return true;
        }
        if (name.Equals("resume", StringComparison.OrdinalIgnoreCase))
        {
            verb = new DeepLinkVerb(DeepLinkKind.Resume, "", "", "");
            return true;
        }
        if (name.Equals("pause", StringComparison.OrdinalIgnoreCase))
        {
            verb = new DeepLinkVerb(DeepLinkKind.Pause, "", "", "");
            return true;
        }
        return false;
    }

    /// <summary>Restore (if minimized) and foreground the FluentApp window. No-op when the HWND is not up yet.
    /// App-side P/Invoke — <c>FluentGpu.Windows</c> has no public wake/activate helper.</summary>
    public static void WakeWindow()
    {
        nint hwnd = FluentApp.WindowHandle;
        if (hwnd == 0) return;
        ShowWindow(hwnd, IsIconic(hwnd) ? SwRestore : SwShow);
        SetForegroundWindow(hwnd);
    }

    static bool TryExtractUri(string? raw, out string uri)
    {
        uri = "";
        if (string.IsNullOrWhiteSpace(raw)) return false;
        ReadOnlySpan<char> s = raw.AsSpan().Trim();
        if (s.Length >= 2 && s[0] == '"' && s[^1] == '"') s = s[1..^1].Trim();
        if (StartsWithWavee(s))
        {
            uri = s.ToString();
            return true;
        }
        int i = IndexOfWavee(raw);
        if (i < 0) return false;
        int end = i + 1;
        while (end < raw.Length && !char.IsWhiteSpace(raw[end]) && raw[end] != '"') end++;
        uri = raw[i..end];
        return uri.Length > 0;
    }

    static bool StartsWithWavee(ReadOnlySpan<char> s)
        => s.StartsWith("wavee:", StringComparison.OrdinalIgnoreCase);

    static int IndexOfWavee(string raw)
    {
        int i = raw.IndexOf("wavee://", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? i : raw.IndexOf("wavee:", StringComparison.OrdinalIgnoreCase);
    }

    static void ReadQuery(string query, out string route, out string arg, out string ctx)
    {
        route = arg = ctx = "";
        if (query.Length == 0) return;
        ReadOnlySpan<char> q = query;
        if (q[0] == '?') q = q[1..];
        while (q.Length > 0)
        {
            int amp = q.IndexOf('&');
            ReadOnlySpan<char> pair = amp < 0 ? q : q[..amp];
            q = amp < 0 ? default : q[(amp + 1)..];
            if (pair.Length == 0) continue;
            int eq = pair.IndexOf('=');
            string key = Uri.UnescapeDataString((eq < 0 ? pair : pair[..eq]).ToString());
            string val = eq < 0 || eq + 1 >= pair.Length ? "" : Uri.UnescapeDataString(pair[(eq + 1)..].ToString());
            if (key.Equals("route", StringComparison.OrdinalIgnoreCase)) route = val;
            else if (key.Equals("arg", StringComparison.OrdinalIgnoreCase)) arg = val;
            else if (key.Equals("ctx", StringComparison.OrdinalIgnoreCase)) ctx = val;
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hWnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hWnd, int nCmdShow);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hWnd);
}

/// <summary>
/// Process-wide intake for <c>wavee://</c> activations. <see cref="Post"/> parses and enqueues (garbage is dropped);
/// the shell drains with <see cref="TryDequeue"/> after reading <see cref="Pending"/>. Navigation / playback is not
/// this type's job.
/// </summary>
public static class DeepLinkChannel
{
    static readonly object Sync = new();
    static readonly Queue<DeepLinkVerb> Queue = new();

    /// <summary>Monotonic ticket — bump on every accepted <see cref="Post"/>. Read <c>.Value</c> to subscribe, then
    /// drain with <see cref="TryDequeue"/>. Same shape as <c>OpenVideoOverrides</c> / <c>_searchFocusRequest</c>.</summary>
    public static readonly Signal<int> Pending = new(0);

    /// <summary>Parse <paramref name="rawArgs"/> and enqueue a verb. No-op on unknown/garbage (never throws).</summary>
    public static void Post(string? rawArgs)
    {
        if (!DeepLink.TryParse(rawArgs, out DeepLinkVerb verb)) return;
        lock (Sync) Queue.Enqueue(verb);
        Pending.Value = Pending.Peek() + 1;
    }

    /// <summary>Pop the next accepted verb. Returns <c>false</c> when the queue is empty.</summary>
    public static bool TryDequeue(out DeepLinkVerb verb)
    {
        lock (Sync) return Queue.TryDequeue(out verb);
    }
}
