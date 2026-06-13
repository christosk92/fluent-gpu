namespace FluentGpu.Forms;

/// <summary>
/// A pure validation rule over a field value: returns <see cref="MsgId.None"/> when the value is valid, or a message
/// identity describing the first failure. Rules are ordinary delegates (no reflection, no attributes — NativeAOT-clean).
///
/// <para><b>Cross-field &amp; conditional rules are free.</b> A rule that reads a sibling signal's <c>.Value</c> inside
/// its body (see <see cref="Rules.Equals{T}"/> / <see cref="Rules.When{T}"/>) auto-subscribes the field's error
/// <see cref="FluentGpu.Signals.Memo{T}"/> to that sibling — so the dependent field re-validates whenever the sibling
/// moves, with no <c>ErrorsChanged</c> plumbing.</para>
/// </summary>
public delegate MsgId Validator<in T>(T value);

/// <summary>
/// The reactive result of validating a field: the first failing rule's message and how many rules failed. A blittable
/// value held in a <see cref="FluentGpu.Signals.Memo{T}"/>; it is never built into a display string until the bound
/// text node resolves <see cref="First"/> via <see cref="Msg.Resolve"/>.
/// </summary>
public readonly record struct FieldError(MsgId First, byte FailCount)
{
    /// <summary>The valid sentinel (no failing rules).</summary>
    public static readonly FieldError Valid = default;

    /// <summary>True when no rule failed.</summary>
    public bool IsValid => FailCount == 0;
}

/// <summary>
/// When a field begins surfacing errors to the user. Default <see cref="OnTouched"/> (silent until the first blur, then
/// live) — no red on a pristine form, matching Flutter's <c>autovalidateMode.onUserInteraction</c> and WCAG-friendly UX.
/// </summary>
public enum ValidationTiming : byte
{
    /// <summary>Validate on every change from the first keystroke.</summary>
    OnChange,

    /// <summary>Surface errors only after the field has lost focus at least once.</summary>
    OnBlur,

    /// <summary>Silent until the first blur, then live on every change (recommended default).</summary>
    OnTouched,

    /// <summary>Surface errors only after a submit attempt.</summary>
    OnSubmit,

    /// <summary>Silent until the first blur or submit attempt, then live on every change.</summary>
    OnChangeAfterFirstError,
}
