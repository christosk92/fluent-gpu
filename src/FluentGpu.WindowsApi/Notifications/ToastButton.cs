using System.Text;

namespace FluentGpu.WindowsApi.Notifications;

/// <summary>How the Shell activates the app when a toast button is pressed (<c>activationType</c>).</summary>
public enum ToastActivation : byte
{
    /// <summary>Brings the app to the foreground (the default) — the activation handler runs with the button's arguments.</summary>
    Foreground = 0,
    /// <summary>Runs in the background without foregrounding the app (a quick-reply / mark-as-read button). For an
    /// unpackaged COM-activated app this routes through the same activator as foreground; the app simply chooses not to
    /// show UI.</summary>
    Background = 1,
    /// <summary>Launches the button's <c>arguments</c> as a URI through the OS default handler (<c>activationType='protocol'</c>) —
    /// no app activation. Use <see cref="ToastButton.Protocol"/> to set both at once.</summary>
    Protocol = 2,
    /// <summary>A system-handled button (e.g. the built-in <c>dismiss</c> / <c>snooze</c>). Use <see cref="ToastButton.Dismiss"/>.</summary>
    System = 3,
}

/// <summary>The colored button rendering hint (<c>hint-buttonStyle</c>; requires the toast to opt in via
/// <see cref="ToastBuilder.ButtonStyles"/>).</summary>
public enum ToastButtonStyle : byte
{
    /// <summary>The default neutral button.</summary>
    None = 0,
    /// <summary>Green "Success" styling.</summary>
    Success = 1,
    /// <summary>Red "Critical" styling.</summary>
    Critical = 2,
}

/// <summary>
/// A fluent description of one toast action button (<c>&lt;action&gt;</c>) — the configurable form behind
/// <see cref="ToastBuilder.Button(string,System.Action{ToastButton})"/>. Covers the full button surface the
/// <c>ToastGeneric</c> schema exposes: activation type, an icon, a colored style, a tooltip, context-menu placement,
/// and the input a contextual (next-to-textbox) button sits beside. Build it through the fluent setters; the builder
/// emits the XML.
/// </summary>
public sealed class ToastButton
{
    private string _content = string.Empty;
    private string _arguments = string.Empty;     // already XML-encoded by the time it lands here
    private ToastActivation _activation = ToastActivation.Foreground;
    private string _icon = string.Empty;
    private ToastButtonStyle _style = ToastButtonStyle.None;
    private string _tooltip = string.Empty;
    private string _inputId = string.Empty;
    private bool _contextMenu;

    /// <summary>Start a button with the given caption.</summary>
    public ToastButton(string content) => Content(content);

    /// <summary>The button caption (XML-escaped). Replaces any previous caption.</summary>
    public ToastButton Content(string content)
    {
        System.ArgumentNullException.ThrowIfNull(content);
        _content = ToastBuilder.EncodeXml(content);
        return this;
    }

    /// <summary>Set the WHOLE pre-structured argument string (e.g. <c>"action=play;id=42"</c>) carried to the
    /// activation handler. XML-escaped only — the <c>;</c>/<c>=</c> delimiters are preserved. For per-pair
    /// percent-encoding of values that may contain <c>% ; =</c>, use <see cref="Argument"/> instead.</summary>
    public ToastButton Arguments(string arguments)
    {
        System.ArgumentNullException.ThrowIfNull(arguments);
        _arguments = ToastBuilder.EncodeXml(arguments);
        return this;
    }

    /// <summary>Append one <c>key=value</c> (or bare <c>key</c>) pair to the arguments, percent-then-XML encoding each
    /// part so values containing <c>% ; =</c> round-trip through <see cref="ToastActivatedArgs.Arguments"/>.</summary>
    public ToastButton Argument(string key, string? value = null)
    {
        System.ArgumentException.ThrowIfNullOrEmpty(key);
        string pair = string.IsNullOrEmpty(value)
            ? ToastBuilder.EncodeArgument(key)
            : $"{ToastBuilder.EncodeArgument(key)}={ToastBuilder.EncodeArgument(value)}";
        _arguments = _arguments.Length == 0 ? pair : $"{_arguments};{pair}";
        return this;
    }

    /// <summary>Activate in the foreground (the default).</summary>
    public ToastButton Foreground() { _activation = ToastActivation.Foreground; return this; }
    /// <summary>Activate in the background (no foregrounding) — a quick-reply / mark-read button.</summary>
    public ToastButton Background() { _activation = ToastActivation.Background; return this; }

    /// <summary>Make this a protocol-launch button: pressing it opens <paramref name="uri"/> via the OS default handler
    /// (<c>activationType='protocol'</c>) with no app activation.</summary>
    public ToastButton Protocol(string uri)
    {
        System.ArgumentException.ThrowIfNullOrEmpty(uri);
        _activation = ToastActivation.Protocol;
        _arguments = ToastBuilder.EncodeXml(uri);
        return this;
    }

    /// <summary>Make this the built-in dismiss button (<c>activationType='system' arguments='dismiss'</c>) — it closes
    /// the toast without activating the app.</summary>
    public ToastButton Dismiss()
    {
        _activation = ToastActivation.System;
        _arguments = "dismiss";
        return this;
    }

    /// <summary>An icon shown on the button (a local image src — same source rules as the visual images).</summary>
    public ToastButton Icon(string src)
    {
        System.ArgumentException.ThrowIfNullOrEmpty(src);
        _icon = ToastBuilder.EncodeXml(src);
        return this;
    }

    /// <summary>Color the button (<c>hint-buttonStyle</c>; needs <see cref="ToastBuilder.ButtonStyles"/> on the toast).</summary>
    public ToastButton Style(ToastButtonStyle style) { _style = style; return this; }
    /// <summary>Sugar for <c>Style(ToastButtonStyle.Success)</c> (green).</summary>
    public ToastButton Success() => Style(ToastButtonStyle.Success);
    /// <summary>Sugar for <c>Style(ToastButtonStyle.Critical)</c> (red).</summary>
    public ToastButton Critical() => Style(ToastButtonStyle.Critical);

    /// <summary>An accessibility tooltip (<c>hint-toolTip</c>) — useful for an icon-only button.</summary>
    public ToastButton Tooltip(string text)
    {
        System.ArgumentNullException.ThrowIfNull(text);
        _tooltip = ToastBuilder.EncodeXml(text);
        return this;
    }

    /// <summary>Place the button on the same row as a text/selection input (<c>hint-inputId</c>) — e.g. a send arrow
    /// next to a reply box.</summary>
    public ToastButton NextToInput(string inputId)
    {
        System.ArgumentException.ThrowIfNullOrEmpty(inputId);
        _inputId = ToastBuilder.EncodeXml(inputId);
        return this;
    }

    /// <summary>Move the button into the toast's context menu (right-click / "…") instead of the button row
    /// (<c>placement='contextMenu'</c>).</summary>
    public ToastButton InContextMenu() { _contextMenu = true; return this; }

    /// <summary>Emit the <c>&lt;action&gt;</c> XML. Internal — the builder calls it during assembly.</summary>
    internal string Build()
    {
        var sb = new StringBuilder("<action content='").Append(_content).Append('\'');
        sb.Append(" arguments='").Append(_arguments).Append('\'');
        string? at = _activation switch
        {
            ToastActivation.Background => "background",
            ToastActivation.Protocol => "protocol",
            ToastActivation.System => "system",
            _ => null,   // foreground is the schema default — omit
        };
        if (at is not null) sb.Append(" activationType='").Append(at).Append('\'');
        if (_icon.Length > 0) sb.Append(" imageUri='").Append(_icon).Append('\'');
        if (_style != ToastButtonStyle.None)
            sb.Append(" hint-buttonStyle='").Append(_style == ToastButtonStyle.Success ? "Success" : "Critical").Append('\'');
        if (_tooltip.Length > 0) sb.Append(" hint-toolTip='").Append(_tooltip).Append('\'');
        if (_inputId.Length > 0) sb.Append(" hint-inputId='").Append(_inputId).Append('\'');
        if (_contextMenu) sb.Append(" placement='contextMenu'");
        sb.Append("/>");
        return sb.ToString();
    }
}
