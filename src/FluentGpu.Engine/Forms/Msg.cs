using FluentGpu.Localization;

namespace FluentGpu.Forms;

/// <summary>
/// A validation message identity. By default it is a <b>localization key</b> (resolved through <see cref="Loc.Get"/> at
/// the bound text thunk, so a message is i18n + culture-reactive for free and costs no per-keystroke string allocation
/// on a table hit); via <see cref="Msg.Literal"/> it carries a raw literal string instead. <see cref="None"/> (the
/// default) means "no error / the value is valid".
///
/// <para>It deliberately holds the loc-key <see cref="string"/> directly rather than an interned
/// <c>Foundation.StringId</c>: a <see cref="MsgId"/> only ever lives inside a managed <see cref="FluentGpu.Signals.Memo{T}"/>
/// (<see cref="FieldError"/>), never in a native scene column, so the blittable-identity argument for interning does not
/// apply — the key literal is allocated once at rule construction and copied by reference thereafter.</para>
/// </summary>
public readonly record struct MsgId(string? Text, bool IsLiteral)
{
    /// <summary>The valid / no-message sentinel — a rule returns this when its value passes.</summary>
    public static readonly MsgId None = default;

    /// <summary>True when there is no message (the value is valid).</summary>
    public bool IsEmpty => string.IsNullOrEmpty(Text);
}

/// <summary>Constructs and resolves <see cref="MsgId"/> validation messages. Prefer <see cref="Key"/> (localized).</summary>
public static class Msg
{
    /// <summary>A localized message: <paramref name="locKey"/> is resolved through <see cref="Loc.Get"/> at the bound
    /// text node — culture-reactive, no re-render, and zero-allocation on a table hit. Pass loc keys, not English text.</summary>
    public static MsgId Key(string locKey) => new(locKey, false);

    /// <summary>Escape hatch: a raw literal message returned verbatim by <see cref="Resolve"/> (no loc lookup). Prefer
    /// <see cref="Key"/> so the message localizes; use this only for already-localized or developer-facing text.</summary>
    public static MsgId Literal(string text) => new(text, true);

    /// <summary>Resolve a message to its display string. Call ONLY inside a render/bind thunk — it subscribes the bound
    /// node to the culture epoch via <see cref="Loc.Get"/>. Returns "" for <see cref="MsgId.None"/>.</summary>
    public static string Resolve(MsgId id)
        => id.IsEmpty ? string.Empty
         : id.IsLiteral ? id.Text!
         : Loc.Get(id.Text!);
}
