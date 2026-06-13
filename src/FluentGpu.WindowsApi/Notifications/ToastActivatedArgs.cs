using System;
using System.Collections.Generic;

namespace FluentGpu.WindowsApi.Notifications;

/// <summary>
/// The payload of a toast activation — what the user clicked and (for toasts with inputs) what they typed. Raised by
/// <see cref="ToastNotifier.Activated"/> when the Shell calls back through the registered
/// <c>INotificationActivationCallback</c>. Mirrors the <c>AppNotificationActivatedEventArgs</c> WASDK builds in
/// <c>AppNotificationManager::Activate</c> (<c>dev/AppNotifications/AppNotificationManager.cpp:346-365</c>): the raw
/// invoked-argument string, plus a (key→value) map of the user-input fields.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Argument"/> is the raw <c>launch=</c>/button <c>arguments=</c> string the toast carried (the
/// <c>invokedArgs</c> the OS passes to <c>Activate</c>). <see cref="Arguments"/> is that string parsed as
/// <c>key=value;key2=value2</c> with the percent-encoding reversed (<c>%25 %3B %3D</c> → <c>% ; =</c>,
/// <c>AppNotificationBuilderUtility.h:146-167</c>), so a handler reads <c>args.Arguments["action"]</c> rather than
/// re-parsing. <see cref="UserInput"/> is the typed/selected values keyed by the input <c>id</c> from
/// <see cref="ToastBuilder.TextBox(string,string?)"/> / <see cref="ToastBuilder.Selection"/>.
/// </para>
/// <para>Cold path (fires at most a handful of times per session); allocation is fine.</para>
/// </remarks>
public readonly struct ToastActivatedArgs
{
    /// <summary>The raw invoked-argument string (the toast's <c>launch=</c> value, or the clicked button's
    /// <c>arguments=</c> value). Empty when the toast carried no arguments.</summary>
    public string Argument { get; }

    /// <summary>The <see cref="Argument"/> string parsed into key/value pairs (<c>key=value;key2</c>; a bare key maps to
    /// an empty value), with percent-encoding reversed. Never <see langword="null"/>.</summary>
    public IReadOnlyDictionary<string, string> Arguments { get; }

    /// <summary>The user-input fields by input <c>id</c> (text the user typed, or a selected combo-box value). Empty for
    /// a toast without inputs. Never <see langword="null"/>.</summary>
    public IReadOnlyDictionary<string, string> UserInput { get; }

    /// <summary>Construct from a raw invoked-argument string and the user-input map. The argument string is parsed and
    /// decoded eagerly. Used by <see cref="ToastNotifier"/> when dispatching <c>INotificationActivationCallback.Activate</c>.</summary>
    public ToastActivatedArgs(string? argument, IReadOnlyDictionary<string, string> userInput)
    {
        Argument = argument ?? string.Empty;
        Arguments = ParseArguments(Argument);
        UserInput = userInput ?? EmptyMap;
    }

    internal static readonly IReadOnlyDictionary<string, string> EmptyMap =
        new Dictionary<string, string>(0);

    /// <summary>
    /// Parse a <c>key=value;key2=value2;key3</c> argument string into a dictionary, reversing the per-value
    /// percent-encoding the builder applied. A segment with no <c>=</c> is a bare key with an empty value. The split is
    /// done on the RAW string first (so the literal <c>;</c>/<c>=</c> delimiters are honored), then each key and value is
    /// decoded — matching the encode order in <see cref="ToastBuilder.EncodeArgument(string)"/>.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> ParseArguments(string argument)
    {
        if (string.IsNullOrEmpty(argument))
            return EmptyMap;

        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string segment in argument.Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = segment.IndexOf('=');
            if (eq < 0)
                map[Decode(segment)] = string.Empty;
            else
                map[Decode(segment[..eq])] = Decode(segment[(eq + 1)..]);
        }
        return map;
    }

    /// <summary>
    /// Reverse the argument percent-encoding: <c>%25 %3B %3D</c> → <c>% ; =</c> (the inverse of
    /// <see cref="ToastBuilder.EncodeArgument(string)"/>; mirrors WASDK's <c>Decode</c>,
    /// <c>AppNotificationBuilderUtility.h:146-167</c>). Note the XML-escaping is NOT reversed here — the OS hands the
    /// activator the already-XML-decoded string (XML entities live only in the on-wire toast document), so only the
    /// percent triples remain to undo.
    /// </summary>
    internal static string Decode(string value)
    {
        if (string.IsNullOrEmpty(value) || value.IndexOf('%') < 0)
            return value;

        var sb = new System.Text.StringBuilder(value.Length);
        for (int i = 0; i < value.Length;)
        {
            if (i + 3 <= value.Length && value[i] == '%')
            {
                // Three-char window: one of the known triples, else pass the literal '%' through.
                char decoded = value.AsSpan(i, 3) switch
                {
                    "%25" => '%',
                    "%3B" => ';',
                    "%3D" => '=',
                    _ => '\0',
                };
                if (decoded != '\0')
                {
                    sb.Append(decoded);
                    i += 3;
                    continue;
                }
            }
            sb.Append(value[i]);
            i++;
        }
        return sb.ToString();
    }
}
