using System;
using System.Collections.Generic;
using System.Text;

namespace FluentGpu.WindowsApi.Notifications;

/// <summary>
/// A fluent builder that assembles a <c>ToastGeneric</c> notification XML payload as a string — the pure-managed,
/// zero-interop half of the Notifications pillar (no P/Invoke, no COM, no WinRT; fully portable and AOT-trivial). The
/// produced string is what <see cref="ToastNotifier.Show(string,string?,string?)"/> hands to the WinRT
/// <c>XmlDocument</c>/<c>IToastNotifier</c> path.
/// </summary>
/// <remarks>
/// <para>
/// The element shapes, attribute names, encoding rules, child ordering, and hard limits are cribbed verbatim from
/// WASDK's <c>AppNotificationBuilder</c> so the output is byte-compatible with the platform's <c>ToastGeneric</c>
/// schema (we build against the in-box public toast surface, not the WASDK runtime — see
/// docs/plans/windowsapi-implementation-research.md §2.1). The assembled document is:
/// <code>
/// &lt;toast{timestamp}{duration}{scenario}{launch}{useButtonStyle}&gt;
///   &lt;visual&gt;&lt;binding template='ToastGeneric'&gt;
///     {text…}{attribution}{appLogoOverride}{heroImage}{inlineImage}
///   &lt;/binding&gt;&lt;/visual&gt;
///   {inputs+actions in &lt;actions&gt;…&lt;/actions&gt;}{audio}
/// &lt;/toast&gt;
/// </code>
/// (WASDK assembles <c>&lt;toast …&gt;&lt;visual&gt;…&lt;/visual&gt;{audio}{actions}&lt;/toast&gt;</c> at
/// <c>AppNotificationBuilder.cpp:379-391</c>; this builder emits <c>actions</c> before <c>audio</c>, which the schema
/// also accepts, so the two are presentation-equivalent.)
/// </para>
/// <para>
/// <b>Encoding (replicate exactly or toasts break / inject).</b> Text/attribute bodies are XML-escaped
/// (<c>&amp; " &lt; &gt; '</c> → entities, <c>AppNotificationBuilderUtility.h:24-34</c>). <c>launch</c>/button
/// <c>arguments</c> key-values are percent-encoded (<c>% ; =</c> → <c>%25 %3B %3D</c>) <i>then</i> XML-escaped
/// (<c>:99-122</c>), because <c>;</c>/<c>=</c> delimit the <c>key=value;key2=value2</c> argument string the activation
/// parser reverses (<see cref="ToastActivatedArgs"/>).
/// </para>
/// <para>
/// <b>Hard limits (from <c>AppNotificationBuilderUtility.h:11-15</c>).</b> ≤ 3 <c>&lt;text&gt;</c>, ≤ 5 buttons,
/// ≤ 5 inputs, and a ≤ 5120-byte final payload (<see cref="BuildXml"/> throws past it). The builder enforces the count
/// caps eagerly (on <c>Add*</c>) and the byte cap at build time.
/// </para>
/// <para>
/// <b>Image-source caveat.</b> <c>src</c> is a bare URI passthrough — an unpackaged app cannot use an
/// <c>http(s)://</c> source (the platform silently drops the image); resolve such URLs through
/// <see cref="ToastImageCache"/> to a local <c>ms-appdata:///local/…</c> path before passing them here
/// (docs/plans/windowsapi-implementation-research.md §2.1, "unpackaged web-image trap").
/// </para>
/// <para>
/// Not thread-safe; build a toast on one thread. Cold path — allocation (the <see cref="StringBuilder"/> and the
/// per-element strings) is fine.
/// </para>
/// </remarks>
public sealed class ToastBuilder
{
    // Hard limits (AppNotificationBuilderUtility.h:11-15).
    private const int MaxPayloadBytes = 5120;
    private const int MaxTextElements = 3;
    private const int MaxButtons = 5;
    private const int MaxInputs = 5;

    private readonly List<string> _text = new(MaxTextElements);
    private readonly List<string> _actions = new(MaxButtons);   // <input …/> and <action …/> children of <actions>
    private readonly List<(string Key, string? Value)> _arguments = new();

    private int _buttonCount;
    private int _inputCount;

    private string _appLogoOverride = string.Empty;
    private string _heroImage = string.Empty;
    private string _inlineImage = string.Empty;
    private string _attribution = string.Empty;
    private string _audio = string.Empty;
    private string _scenarioAttr = string.Empty;
    private bool _longDuration;
    private bool _useButtonStyle;

    /// <summary>
    /// Append a body <c>&lt;text&gt;</c> line (XML-escaped). Up to <see cref="MaxTextElements"/> (3) lines; the first is
    /// the title, the rest are body text (<c>AppNotificationBuilder.cpp:72</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">More than 3 text lines were added.</exception>
    public ToastBuilder AddText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (_text.Count >= MaxTextElements)
            throw new InvalidOperationException($"A toast supports at most {MaxTextElements} <text> elements.");
        _text.Add($"<text>{EncodeXml(text)}</text>");
        return this;
    }

    /// <summary>
    /// Set the attribution line (small dimmed text under the body, conventionally the source/app), optionally tagged
    /// with a BCP-47 language (<c>AppNotificationBuilder.cpp:92,100</c>). Replaces any previous attribution.
    /// </summary>
    public ToastBuilder SetAttributionText(string text, string? language = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        _attribution = string.IsNullOrEmpty(language)
            ? $"<text placement='attribution'>{EncodeXml(text)}</text>"
            : $"<text placement='attribution' lang='{EncodeXml(language)}'>{EncodeXml(text)}</text>";
        return this;
    }

    /// <summary>
    /// Set the <c>appLogoOverride</c> image (the small image shown to the left of the text — the album thumbnail for
    /// WAVEE). <paramref name="circle"/> crops it to a circle (<c>hint-crop='circle'</c>,
    /// <c>AppNotificationBuilder.cpp:134-162</c>).
    /// </summary>
    /// <param name="src">A local image source. Unpackaged apps must NOT pass an <c>http(s)://</c> URI here (resolve it
    /// via <see cref="ToastImageCache"/> first); <c>ms-appdata:///local/…</c>, <c>file:///…</c>, and (packaged)
    /// <c>ms-appx:///…</c> all work.</param>
    /// <param name="circle"><see langword="true"/> to crop the image to a circle (<c>hint-crop='circle'</c>).</param>
    public ToastBuilder SetAppLogoOverride(string src, bool circle = false)
    {
        ArgumentException.ThrowIfNullOrEmpty(src);
        _appLogoOverride = circle
            ? $"<image placement='appLogoOverride' src='{EncodeXml(src)}' hint-crop='circle'/>"
            : $"<image placement='appLogoOverride' src='{EncodeXml(src)}'/>";
        return this;
    }

    /// <summary>Set the <c>hero</c> image — a full-width banner above the text (<c>AppNotificationBuilder.cpp:164</c>).
    /// The <paramref name="src"/> source rules are the same as <see cref="SetAppLogoOverride"/>.</summary>
    public ToastBuilder SetHeroImage(string src)
    {
        ArgumentException.ThrowIfNullOrEmpty(src);
        _heroImage = $"<image placement='hero' src='{EncodeXml(src)}'/>";
        return this;
    }

    /// <summary>Set a single inline image — full-width inside the body (<c>AppNotificationBuilder.cpp:104</c>).
    /// The <paramref name="src"/> source rules are the same as <see cref="SetAppLogoOverride"/>.</summary>
    public ToastBuilder SetInlineImage(string src)
    {
        ArgumentException.ThrowIfNullOrEmpty(src);
        _inlineImage = $"<image src='{EncodeXml(src)}'/>";
        return this;
    }

    /// <summary>
    /// Add a top-level <c>launch</c> argument (<c>key</c> or <c>key=value</c>) carried when the toast body (not a
    /// button) is clicked. Keys/values are percent-then-XML encoded; an empty/<c>null</c> value emits a bare <c>key</c>
    /// (<c>AppNotificationBuilder.cpp:285-310</c>). The activation handler receives these parsed in
    /// <see cref="ToastActivatedArgs.Arguments"/>.
    /// </summary>
    public ToastBuilder AddArgument(string key, string? value = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        _arguments.Add((key, value));
        return this;
    }

    /// <summary>
    /// Add an action button. When clicked, the Shell activates the app with <paramref name="argKey"/>=
    /// <paramref name="argValue"/> as the invoked argument string (parsed into
    /// <see cref="ToastActivatedArgs.Arguments"/>). Up to <see cref="MaxButtons"/> (5) buttons
    /// (<c>AppNotificationBuilder.cpp:233-239</c>, button XML <c>AppNotificationButton.cpp:120-129</c>).
    /// </summary>
    /// <param name="content">The button caption (XML-escaped).</param>
    /// <param name="argKey">The argument key the activation reports (percent+XML encoded).</param>
    /// <param name="argValue">The argument value (percent+XML encoded).</param>
    /// <exception cref="InvalidOperationException">More than 5 buttons were added.</exception>
    public ToastBuilder AddButton(string content, string argKey, string argValue)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentException.ThrowIfNullOrEmpty(argKey);
        ArgumentNullException.ThrowIfNull(argValue);
        if (_buttonCount >= MaxButtons)
            throw new InvalidOperationException($"A toast supports at most {MaxButtons} buttons.");

        // <action content='…' arguments='key=value'/> — arguments is the same key=value;… convention as launch.
        string args = string.IsNullOrEmpty(argValue)
            ? EncodeArgument(argKey)
            : $"{EncodeArgument(argKey)}={EncodeArgument(argValue)}";
        _actions.Add($"<action content='{EncodeXml(content)}' arguments='{args}'/>");
        _buttonCount++;
        return this;
    }

    /// <summary>
    /// Add a free-text input box the user can type into before pressing a button; its typed value is reported under
    /// <paramref name="id"/> in <see cref="ToastActivatedArgs.UserInput"/> (<c>AppNotificationBuilder.cpp:215-222</c>).
    /// Up to <see cref="MaxInputs"/> (5) inputs.
    /// </summary>
    /// <param name="id">The input id (XML-escaped) the typed value is reported under.</param>
    /// <param name="placeholder">Optional placeholder text shown when empty.</param>
    /// <exception cref="InvalidOperationException">More than 5 inputs were added.</exception>
    public ToastBuilder AddTextBox(string id, string? placeholder = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(id);
        if (_inputCount >= MaxInputs)
            throw new InvalidOperationException($"A toast supports at most {MaxInputs} inputs.");
        _actions.Add(string.IsNullOrEmpty(placeholder)
            ? $"<input id='{EncodeXml(id)}' type='text'/>"
            : $"<input id='{EncodeXml(id)}' type='text' placeHolderContent='{EncodeXml(placeholder)}'/>");
        _inputCount++;
        return this;
    }

    /// <summary>Set the toast <c>scenario</c> (presentation aggressiveness). <see cref="ToastScenario.Default"/> emits
    /// nothing (<c>AppNotificationBuilder.cpp:267-283</c>).</summary>
    public ToastBuilder SetScenario(ToastScenario scenario)
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

    /// <summary>Use the "long" toast duration (the toast lingers ~25s instead of ~7s) —
    /// <c>duration='long'</c> (<c>AppNotificationBuilder.cpp:262-265</c>).</summary>
    public ToastBuilder SetLongDuration(bool longDuration = true)
    {
        _longDuration = longDuration;
        return this;
    }

    /// <summary>Opt buttons into the colored "useButtonStyle" rendering (<c>useButtonStyle='true'</c> on the root, for
    /// Success/Critical button hints).</summary>
    public ToastBuilder UseButtonStyle(bool use = true)
    {
        _useButtonStyle = use;
        return this;
    }

    /// <summary>Play a built-in notification sound when the toast appears (<c>AppNotificationBuilder.cpp:190-194</c>).
    /// Mutually exclusive with <see cref="MuteAudio"/> (last call wins).</summary>
    public ToastBuilder SetAudioEvent(ToastSound sound, bool loop = false)
    {
        string uri = SoundUri(sound);
        _audio = loop
            ? $"<audio src='{uri}' loop='true'/>"
            : $"<audio src='{uri}'/>";
        return this;
    }

    /// <summary>Silence the toast (<c>&lt;audio silent='true'/&gt;</c>, <c>AppNotificationBuilder.cpp:202-206</c>).
    /// Mutually exclusive with <see cref="SetAudioEvent"/> (last call wins).</summary>
    public ToastBuilder MuteAudio()
    {
        _audio = "<audio silent='true'/>";
        return this;
    }

    /// <summary>
    /// Assemble the final toast XML string. Composes the launch-arguments attribute from
    /// <see cref="AddArgument(string,string?)"/>, orders the children per the <c>ToastGeneric</c> schema, and enforces
    /// the ≤ 5120-byte payload cap (<c>AppNotificationBuilder.cpp:393</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The assembled payload exceeds <see cref="MaxPayloadBytes"/> (5120)
    /// bytes (the platform rejects larger toasts).</exception>
    public string BuildXml()
    {
        var sb = new StringBuilder(512);

        sb.Append("<toast");
        sb.Append(_scenarioAttr);
        if (_longDuration)
            sb.Append(" duration='long'");
        AppendLaunchAttribute(sb);
        if (_useButtonStyle)
            sb.Append(" useButtonStyle='true'");
        sb.Append('>');

        sb.Append("<visual><binding template='ToastGeneric'>");
        foreach (string t in _text)
            sb.Append(t);
        sb.Append(_attribution);
        sb.Append(_appLogoOverride);
        sb.Append(_heroImage);
        sb.Append(_inlineImage);
        sb.Append("</binding></visual>");

        if (_actions.Count > 0)
        {
            sb.Append("<actions>");
            foreach (string a in _actions)
                sb.Append(a);
            sb.Append("</actions>");
        }

        sb.Append(_audio);
        sb.Append("</toast>");

        string xml = sb.ToString();
        // The platform caps the payload at 5120 BYTES (UTF-8/UTF-16 — measure the UTF-8 length, the on-wire form).
        int bytes = Encoding.UTF8.GetByteCount(xml);
        if (bytes > MaxPayloadBytes)
            throw new InvalidOperationException(
                $"Toast payload is {bytes} bytes; the maximum is {MaxPayloadBytes}. Trim text/arguments or use fewer images.");
        return xml;
    }

    /// <summary>Compose <c>launch='key=value;key2;…'</c> from the collected arguments (trailing <c>;</c> dropped, like
    /// <c>AppNotificationBuilder.cpp:285-310</c>), percent+XML encoding each key and value. Emits nothing when empty.</summary>
    private void AppendLaunchAttribute(StringBuilder sb)
    {
        if (_arguments.Count == 0)
            return;

        var args = new StringBuilder();
        foreach ((string key, string? value) in _arguments)
        {
            if (!string.IsNullOrEmpty(value))
                args.Append(EncodeArgument(key)).Append('=').Append(EncodeArgument(value)).Append(';');
            else
                args.Append(EncodeArgument(key)).Append(';');
        }
        if (args.Length > 0)
            args.Length--;   // drop the trailing ';'

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

    /// <summary>XML-escape a body/attribute value: <c>&amp; " &lt; &gt; '</c> → entities
    /// (<c>AppNotificationBuilderUtility.h:24-34,124-142</c>). Returns the input unchanged when nothing needs escaping.</summary>
    internal static string EncodeXml(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;
        if (value.IndexOfAny(s_xmlSpecials) < 0)
            return value;

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
    /// then XML-escape the rest, exactly as <c>EncodeArgument</c> does (<c>AppNotificationBuilderUtility.h:99-122</c>).
    /// The percent step MUST come first so the <c>;</c>/<c>=</c> delimiters survive into the parsed argument string.</summary>
    internal static string EncodeArgument(string value)
    {
        if (string.IsNullOrEmpty(value))
            return string.Empty;

        var sb = new StringBuilder(value.Length + 8);
        foreach (char ch in value)
        {
            switch (ch)
            {
                // Percent encodings take precedence (these chars delimit the argument string).
                case '%': sb.Append("%25"); break;
                case ';': sb.Append("%3B"); break;
                case '=': sb.Append("%3D"); break;
                // Otherwise XML-escape.
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
