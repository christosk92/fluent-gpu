using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace FluentGpu.WindowsApi.Notifications;

/// <summary>
/// A fluent builder for a <c>ToastGeneric</c> notification — the pure-managed, zero-interop half of the Notifications
/// pillar (no P/Invoke, no COM, no WinRT; fully portable and AOT-trivial). You describe the toast with chained calls
/// and never have to think about XML: hand the builder straight to <see cref="ShowVia"/> /
/// <see cref="ToastNotifier.Show(ToastBuilder)"/> and it carries its own tag/group. <see cref="BuildXml"/> remains the
/// raw escape hatch when you want the string yourself.
/// </summary>
/// <example>
/// <code>
/// Toast.Create()
///      .Title("Download complete")
///      .Body("song.flac is ready")
///      .AppLogo(localArtPath, circle: true)
///      .Button("Open", b => b.Argument("action", "open").Success())
///      .Button("Dismiss", b => b.Dismiss())
///      .Tag("dl-42").Group("downloads")
///      .ShowVia(notifier);
/// </code>
/// </example>
/// <remarks>
/// <para>
/// The element shapes, attribute names, encoding rules, child ordering, and hard limits follow WASDK's
/// <c>AppNotificationBuilder</c> so the output is byte-compatible with the in-box <c>ToastGeneric</c> schema. The
/// assembled document is:
/// <code>
/// &lt;toast{timestamp}{duration}{scenario}{launch}{useButtonStyle}&gt;
///   {header}
///   &lt;visual&gt;&lt;binding template='ToastGeneric'&gt;
///     {text…}{progress}{attribution}{appLogoOverride}{heroImage}{inlineImage}
///   &lt;/binding&gt;&lt;/visual&gt;
///   &lt;actions&gt;{inputs}{buttons}&lt;/actions&gt;{audio}
/// &lt;/toast&gt;
/// </code>
/// </para>
/// <para>
/// <b>Encoding (replicate exactly or toasts break / inject).</b> Text/attribute bodies are XML-escaped
/// (<c>&amp; " &lt; &gt; '</c> → entities). <c>launch</c>/button <c>arguments</c> key-values are percent-encoded
/// (<c>% ; =</c> → <c>%25 %3B %3D</c>) THEN XML-escaped, because <c>;</c>/<c>=</c> delimit the argument string the
/// activation parser reverses (<see cref="ToastActivatedArgs"/>).
/// </para>
/// <para>
/// <b>Hard limits.</b> ≤ 3 <c>&lt;text&gt;</c>, ≤ 5 buttons, ≤ 5 inputs, and a ≤ 5120-byte final payload
/// (<see cref="BuildXml"/> throws past it). Count caps are enforced eagerly; the byte cap at build time.
/// </para>
/// <para>
/// <b>Image-source caveat.</b> <c>src</c> is a bare URI passthrough — an unpackaged app cannot use an
/// <c>http(s)://</c> source (the platform silently drops the image); resolve such URLs through
/// <see cref="ToastImageCache"/> to a local <c>ms-appdata:///local/…</c> path first.
/// </para>
/// <para>Not thread-safe; build a toast on one thread. Cold path — allocation is fine.</para>
/// </remarks>
public sealed class ToastBuilder
{
    // Hard limits (AppNotificationBuilderUtility.h:11-15).
    private const int MaxPayloadBytes = 5120;
    private const int MaxTextElements = 3;
    private const int MaxButtons = 5;
    private const int MaxInputs = 5;

    private readonly List<string> _text = new(MaxTextElements);
    private readonly List<string> _inputs = new();    // <input …/> children (schema: inputs precede buttons)
    private readonly List<string> _buttons = new(MaxButtons);
    private readonly List<(string Key, string? Value)> _arguments = new();
    private string _launchOverride = string.Empty;    // a whole launch string set via Launch(); wins over _arguments

    private int _inputCount;

    private string _header = string.Empty;
    private string _appLogoOverride = string.Empty;
    private string _heroImage = string.Empty;
    private string _inlineImage = string.Empty;
    private string _progress = string.Empty;
    private string _attribution = string.Empty;
    private string _audio = string.Empty;
    private string _scenarioAttr = string.Empty;
    private string _timestampAttr = string.Empty;
    private bool _longDuration;
    private bool _useButtonStyle;

    private string? _tag;
    private string? _group;

    /// <summary>The data-binding placeholder names a data-bound <see cref="Progress"/> emits — the keys a
    /// <see cref="ToastProgress"/> writes into the toast's NotificationData.</summary>
    internal const string BindValue = "progressValue", BindStatus = "progressStatus", BindTitle = "progressTitle", BindValueString = "progressValueString";

    /// <summary>Start a fresh toast (sugar for <c>new ToastBuilder()</c>).</summary>
    public static ToastBuilder Create() => new();

    // ── Text ────────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Append a body <c>&lt;text&gt;</c> line (XML-escaped). The first line is the title, the rest are body
    /// text. Up to <see cref="MaxTextElements"/> (3) lines.</summary>
    /// <exception cref="InvalidOperationException">More than 3 text lines were added.</exception>
    public ToastBuilder Text(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_text.Count >= MaxTextElements)
            throw new InvalidOperationException($"A toast supports at most {MaxTextElements} <text> elements.");
        _text.Add($"<text>{EncodeXml(text)}</text>");
        return this;
    }

    /// <summary>The headline line (the first <c>&lt;text&gt;</c> — bold). Sugar over <see cref="Text"/>.</summary>
    public ToastBuilder Title(string text) => Text(text);
    /// <summary>A body line under the title. Sugar over <see cref="Text"/>.</summary>
    public ToastBuilder Body(string text) => Text(text);

    /// <summary>Set the attribution line (small dimmed text under the body, conventionally the source/app), optionally
    /// tagged with a BCP-47 language. Replaces any previous attribution.</summary>
    public ToastBuilder Attribution(string text, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        _attribution = string.IsNullOrEmpty(language)
            ? $"<text placement='attribution'>{EncodeXml(text)}</text>"
            : $"<text placement='attribution' lang='{EncodeXml(language)}'>{EncodeXml(text)}</text>";
        return this;
    }

    /// <summary>Add a notification header (<c>&lt;header&gt;</c>) — a category title above the toast that groups related
    /// toasts in the Action Center. Clicking it activates the app with <paramref name="arguments"/>. One per toast.</summary>
    public ToastBuilder Header(string id, string title, string arguments = "")
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(title);
        _header = $"<header id='{EncodeXml(id)}' title='{EncodeXml(title)}' arguments='{EncodeXml(arguments)}'/>";
        return this;
    }

    // ── Images ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Set the <c>appLogoOverride</c> image (the small image to the left of the text — the album thumbnail for
    /// WAVEE). <paramref name="circle"/> crops it to a circle.</summary>
    /// <param name="src">A local image source (<c>ms-appdata:///local/…</c>, <c>file:///…</c>, packaged
    /// <c>ms-appx:///…</c>). Unpackaged apps must NOT pass an <c>http(s)://</c> URI — resolve via
    /// <see cref="ToastImageCache"/> first.</param>
    /// <param name="circle"><see langword="true"/> to crop to a circle (<c>hint-crop='circle'</c>).</param>
    public ToastBuilder AppLogo(string src, bool circle = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(src);
        _appLogoOverride = circle
            ? $"<image placement='appLogoOverride' src='{EncodeXml(src)}' hint-crop='circle'/>"
            : $"<image placement='appLogoOverride' src='{EncodeXml(src)}'/>";
        return this;
    }

    /// <summary>Set the <c>hero</c> image — a full-width banner above the text. Source rules as <see cref="AppLogo"/>.</summary>
    public ToastBuilder Hero(string src)
    {
        ArgumentException.ThrowIfNullOrEmpty(src);
        _heroImage = $"<image placement='hero' src='{EncodeXml(src)}'/>";
        return this;
    }

    /// <summary>Set a single inline image — full-width inside the body. Source rules as <see cref="AppLogo"/>.</summary>
    public ToastBuilder Inline(string src)
    {
        ArgumentException.ThrowIfNullOrEmpty(src);
        _inlineImage = $"<image src='{EncodeXml(src)}'/>";
        return this;
    }

    // ── Launch arguments ────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Set the WHOLE top-level <c>launch</c> argument string at once (e.g. <c>"open=song;id=42"</c>) — carried
    /// when the toast body (not a button) is clicked, and reported in <see cref="ToastActivatedArgs.Arguments"/>.
    /// XML-escaped only (the <c>;</c>/<c>=</c> delimiters are preserved). Overrides any <see cref="Argument"/> calls.</summary>
    public ToastBuilder Launch(string arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        _launchOverride = arguments;
        return this;
    }

    /// <summary>Append one top-level <c>launch</c> argument (<c>key</c> or <c>key=value</c>), percent-then-XML encoded.
    /// Ignored if <see cref="Launch"/> set a whole string.</summary>
    public ToastBuilder Argument(string key, string? value = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        _arguments.Add((key, value));
        return this;
    }

    // ── Buttons ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Add a simple foreground action button carrying <paramref name="arguments"/> (a whole structured string
    /// like <c>"action=play"</c>, XML-escaped only). Up to <see cref="MaxButtons"/> (5) buttons.</summary>
    /// <exception cref="InvalidOperationException">More than 5 buttons were added.</exception>
    public ToastBuilder Button(string content, string arguments)
        => Button(new ToastButton(content).Arguments(arguments));

    /// <summary>Add a fully-configured button (activation type, icon, style, tooltip, context-menu placement, …) via a
    /// fluent <see cref="ToastButton"/> callback.</summary>
    public ToastBuilder Button(string content, Action<ToastButton> configure)
    {
        ArgumentNullException.ThrowIfNull(configure);
        var b = new ToastButton(content);
        configure(b);
        return Button(b);
    }

    /// <summary>Add a pre-built <see cref="ToastButton"/>.</summary>
    public ToastBuilder Button(ToastButton button)
    {
        ArgumentNullException.ThrowIfNull(button);
        if (_buttons.Count >= MaxButtons)
            throw new InvalidOperationException($"A toast supports at most {MaxButtons} buttons.");
        _buttons.Add(button.Build());
        return this;
    }

    /// <summary>Add the built-in dismiss button (<c>activationType='system'</c>) — sugar for
    /// <c>Button(c =&gt; c.Dismiss())</c>.</summary>
    public ToastBuilder DismissButton(string content = "Dismiss")
        => Button(new ToastButton(content).Dismiss());

    // ── Inputs ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Add a free-text input box; its typed value is reported under <paramref name="id"/> in
    /// <see cref="ToastActivatedArgs.UserInput"/>. Up to <see cref="MaxInputs"/> (5) inputs.</summary>
    /// <exception cref="InvalidOperationException">More than 5 inputs were added.</exception>
    public ToastBuilder TextBox(string id, string? placeholder = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        GuardInputCap();
        _inputs.Add(string.IsNullOrEmpty(placeholder)
            ? $"<input id='{EncodeXml(id)}' type='text'/>"
            : $"<input id='{EncodeXml(id)}' type='text' placeHolderContent='{EncodeXml(placeholder)}'/>");
        _inputCount++;
        return this;
    }

    /// <summary>Add a drop-down selection input; the chosen option's id is reported under <paramref name="id"/> in
    /// <see cref="ToastActivatedArgs.UserInput"/>. Counts toward the <see cref="MaxInputs"/> (5) input cap.</summary>
    /// <param name="id">The input id the chosen value is reported under.</param>
    /// <param name="defaultId">The option id selected initially (null = no preselection).</param>
    /// <param name="options">The <c>(id, text)</c> choices, in display order.</param>
    public ToastBuilder Selection(string id, string? defaultId, params (string Id, string Text)[] options)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        ArgumentNullException.ThrowIfNull(options);
        GuardInputCap();

        var sb = new StringBuilder("<input id='").Append(EncodeXml(id)).Append("' type='selection'");
        if (!string.IsNullOrEmpty(defaultId)) sb.Append(" defaultInput='").Append(EncodeXml(defaultId)).Append('\'');
        sb.Append('>');
        foreach (var (optId, optText) in options)
            sb.Append("<selection id='").Append(EncodeXml(optId)).Append("' content='").Append(EncodeXml(optText)).Append("'/>");
        sb.Append("</input>");
        _inputs.Add(sb.ToString());
        _inputCount++;
        return this;
    }

    private void GuardInputCap()
    {
        if (_inputCount >= MaxInputs)
            throw new InvalidOperationException($"A toast supports at most {MaxInputs} inputs.");
    }

    // ── Progress ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Add a determinate (or indeterminate) <c>&lt;progress&gt;</c> bar (one per toast). By default it is DATA-BOUND
    /// (emits <c>{progressValue}</c>/<c>{progressStatus}</c>/… placeholders) so the toast can be updated in place via the
    /// notifier's data path without re-showing. Pass <paramref name="dataBound"/>=false to bake literal values in.
    /// </summary>
    /// <param name="value">0.0..1.0 determinate fraction, or <see langword="null"/> for an indeterminate (marquee) bar.</param>
    /// <param name="status">The caption under the bar (e.g. "Downloading…").</param>
    /// <param name="title">Optional bold label above the bar.</param>
    /// <param name="valueStringOverride">Optional text replacing the default "NN%" readout.</param>
    public ToastBuilder Progress(double? value = 0.0, string status = "", string? title = null, string? valueStringOverride = null, bool dataBound = true)
    {
        var sb = new StringBuilder("<progress");
        if (dataBound)
        {
            if (title is not null) sb.Append($" title='{{{BindTitle}}}'");
            sb.Append($" value='{{{BindValue}}}'");
            if (valueStringOverride is not null) sb.Append($" valueStringOverride='{{{BindValueString}}}'");
            sb.Append($" status='{{{BindStatus}}}'");
        }
        else
        {
            if (!string.IsNullOrEmpty(title)) sb.Append($" title='{EncodeXml(title)}'");
            sb.Append(" value='").Append(value is double v ? v.ToString("0.###", CultureInfo.InvariantCulture) : "indeterminate").Append('\'');
            if (!string.IsNullOrEmpty(valueStringOverride)) sb.Append($" valueStringOverride='{EncodeXml(valueStringOverride)}'");
            sb.Append($" status='{EncodeXml(status)}'");
        }
        sb.Append("/>");
        _progress = sb.ToString();
        return this;
    }

    // ── Presentation ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Set the toast <c>scenario</c> (presentation aggressiveness). <see cref="ToastScenario.Default"/> emits
    /// nothing.</summary>
    public ToastBuilder Scenario(ToastScenario scenario)
    {
        _scenarioAttr = scenario switch
        {
            ToastScenario.Reminder => " scenario='reminder'",
            ToastScenario.Alarm => " scenario='alarm'",
            ToastScenario.IncomingCall => " scenario='incomingCall'",
            ToastScenario.Urgent => " scenario='urgent'",
            _ => string.Empty,
        };
        return this;
    }

    /// <summary>Use the "long" toast duration (~25s instead of ~7s) — <c>duration='long'</c>.</summary>
    public ToastBuilder LongDuration(bool longDuration = true) { _longDuration = longDuration; return this; }

    /// <summary>Opt buttons into the colored rendering (<c>useButtonStyle='true'</c>) so <see cref="ToastButton.Success"/>/
    /// <see cref="ToastButton.Critical"/> take effect.</summary>
    public ToastBuilder ButtonStyles(bool use = true) { _useButtonStyle = use; return this; }

    /// <summary>Override the toast's displayed timestamp (<c>displayTimestamp</c>, ISO-8601 UTC) instead of the OS
    /// receipt time — e.g. for a message that arrived earlier.</summary>
    public ToastBuilder Timestamp(DateTimeOffset when)
    {
        _timestampAttr = $" displayTimestamp='{when.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)}'";
        return this;
    }

    // ── Audio ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Play a built-in notification sound when the toast appears. Mutually exclusive with
    /// <see cref="Silent"/> (last call wins).</summary>
    public ToastBuilder Sound(ToastSound sound, bool loop = false) => Sound(SoundUri(sound), loop);

    /// <summary>Play a custom <c>ms-winsoundevent:…</c> (or app-package) audio URI. Mutually exclusive with
    /// <see cref="Silent"/> (last call wins).</summary>
    public ToastBuilder Sound(string uri, bool loop = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(uri);
        _audio = loop ? $"<audio src='{EncodeXml(uri)}' loop='true'/>" : $"<audio src='{EncodeXml(uri)}'/>";
        return this;
    }

    /// <summary>Silence the toast (<c>&lt;audio silent='true'/&gt;</c>). Mutually exclusive with <see cref="Sound(ToastSound,bool)"/>.</summary>
    public ToastBuilder Silent() { _audio = "<audio silent='true'/>"; return this; }

    // ── Tag / group (carried with the toast; read by ToastNotifier.Show(ToastBuilder)) ──────────────────────────────

    /// <summary>Tag the toast (for later update/remove-by-tag). Carried into <see cref="ToastNotifier.Show(ToastBuilder)"/>;
    /// no need to pass it separately.</summary>
    public ToastBuilder Tag(string tag) { _tag = tag; return this; }
    /// <summary>Group the toast (for collection-level remove). Carried into <see cref="ToastNotifier.Show(ToastBuilder)"/>.</summary>
    public ToastBuilder Group(string group) { _group = group; return this; }

    /// <summary>The tag carried on the builder (null if unset) — read by <see cref="ToastNotifier.Show(ToastBuilder)"/>.</summary>
    public string? TagValue => _tag;
    /// <summary>The group carried on the builder (null if unset) — read by <see cref="ToastNotifier.Show(ToastBuilder)"/>.</summary>
    public string? GroupValue => _group;

    // ── Terminal ────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Show this toast through <paramref name="notifier"/> with no XML in sight — builds, applies the carried
    /// <see cref="Tag"/>/<see cref="Group"/>, and shows. Returns the platform's success.</summary>
    public bool ShowVia(ToastNotifier notifier)
    {
        ArgumentNullException.ThrowIfNull(notifier);
        return notifier.Show(this);
    }

    /// <summary>
    /// Assemble the final toast XML string (the raw escape hatch — most callers use <see cref="ShowVia"/> instead).
    /// Orders the children per the <c>ToastGeneric</c> schema and enforces the ≤ 5120-byte payload cap.
    /// </summary>
    /// <exception cref="InvalidOperationException">The assembled payload exceeds <see cref="MaxPayloadBytes"/> (5120) bytes.</exception>
    public string BuildXml()
    {
        var sb = new StringBuilder(512);

        sb.Append("<toast");
        sb.Append(_timestampAttr);
        sb.Append(_scenarioAttr);
        if (_longDuration) sb.Append(" duration='long'");
        AppendLaunchAttribute(sb);
        if (_useButtonStyle) sb.Append(" useButtonStyle='true'");
        sb.Append('>');

        sb.Append(_header);

        sb.Append("<visual><binding template='ToastGeneric'>");
        foreach (string t in _text) sb.Append(t);
        sb.Append(_progress);   // <progress> follows the text lines in the ToastGeneric binding
        sb.Append(_attribution);
        sb.Append(_appLogoOverride);
        sb.Append(_heroImage);
        sb.Append(_inlineImage);
        sb.Append("</binding></visual>");

        if (_inputs.Count > 0 || _buttons.Count > 0)
        {
            sb.Append("<actions>");
            foreach (string i in _inputs) sb.Append(i);     // schema: inputs precede buttons
            foreach (string b in _buttons) sb.Append(b);
            sb.Append("</actions>");
        }

        sb.Append(_audio);
        sb.Append("</toast>");

        string xml = sb.ToString();
        int bytes = Encoding.UTF8.GetByteCount(xml);   // the platform caps the payload at 5120 BYTES (UTF-8 on-wire)
        if (bytes > MaxPayloadBytes)
            throw new InvalidOperationException(
                $"Toast payload is {bytes} bytes; the maximum is {MaxPayloadBytes}. Trim text/arguments or use fewer images.");
        return xml;
    }

    /// <summary>Compose <c>launch='key=value;key2;…'</c>. A whole-string <see cref="Launch"/> wins (XML-escaped only);
    /// otherwise each <see cref="Argument"/> pair is percent+XML encoded. Emits nothing when empty.</summary>
    private void AppendLaunchAttribute(StringBuilder sb)
    {
        if (_launchOverride.Length > 0)
        {
            sb.Append(" launch='").Append(EncodeXml(_launchOverride)).Append('\'');
            return;
        }
        if (_arguments.Count == 0) return;

        var args = new StringBuilder();
        foreach ((string key, string? value) in _arguments)
        {
            if (!string.IsNullOrEmpty(value))
                args.Append(EncodeArgument(key)).Append('=').Append(EncodeArgument(value)).Append(';');
            else
                args.Append(EncodeArgument(key)).Append(';');
        }
        if (args.Length > 0) args.Length--;   // drop the trailing ';'
        sb.Append(" launch='").Append(args).Append('\'');
    }

    private static string SoundUri(ToastSound sound) => sound switch
    {
        ToastSound.IM => "ms-winsoundevent:Notification.IM",
        ToastSound.Mail => "ms-winsoundevent:Notification.Mail",
        ToastSound.Reminder => "ms-winsoundevent:Notification.Reminder",
        ToastSound.SMS => "ms-winsoundevent:Notification.SMS",
        ToastSound.LoopingAlarm => "ms-winsoundevent:Notification.Looping.Alarm",
        ToastSound.LoopingCall => "ms-winsoundevent:Notification.Looping.Call",
        _ => "ms-winsoundevent:Notification.Default",
    };

    /// <summary>XML-escape a body/attribute value: <c>&amp; " &lt; &gt; '</c> → entities. Returns the input unchanged
    /// when nothing needs escaping.</summary>
    internal static string EncodeXml(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;
        if (value.IndexOfAny(s_xmlSpecials) < 0) return value;

        var sb = new StringBuilder(value.Length + 8);
        foreach (char ch in value)
        {
            switch (ch)
            {
                case '&': sb.Append("&amp;"); break;
                case '"': sb.Append("&quot;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '\'': sb.Append("&apos;"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Encode a <c>launch</c>/<c>arguments</c> key or value: percent-encode <c>% ; =</c> (→ <c>%25 %3B %3D</c>),
    /// then XML-escape the rest. The percent step MUST come first so the <c>;</c>/<c>=</c> delimiters survive.</summary>
    internal static string EncodeArgument(string value)
    {
        if (string.IsNullOrEmpty(value)) return string.Empty;

        var sb = new StringBuilder(value.Length + 8);
        foreach (char ch in value)
        {
            switch (ch)
            {
                case '%': sb.Append("%25"); break;
                case ';': sb.Append("%3B"); break;
                case '=': sb.Append("%3D"); break;
                case '&': sb.Append("&amp;"); break;
                case '"': sb.Append("&quot;"); break;
                case '<': sb.Append("&lt;"); break;
                case '>': sb.Append("&gt;"); break;
                case '\'': sb.Append("&apos;"); break;
                default: sb.Append(ch); break;
            }
        }
        return sb.ToString();
    }

    private static readonly char[] s_xmlSpecials = ['&', '"', '<', '>', '\''];
}

/// <summary>Sugar entry point: <c>Toast.Create()</c> reads better than <c>new ToastBuilder()</c> at a call site.</summary>
public static class Toast
{
    /// <summary>Start a fresh toast builder.</summary>
    public static ToastBuilder Create() => new();
}
