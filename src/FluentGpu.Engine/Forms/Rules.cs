using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using FluentGpu.Signals;

namespace FluentGpu.Forms;

/// <summary>
/// The built-in validation rules. Each factory interns its message <see cref="MsgId"/> once (captured by value in the
/// returned delegate), so calling a rule on every keystroke allocates nothing. The default <c>locKey</c>s
/// (<c>validation.required</c>, <c>validation.minlen</c>, …) should be present in every loaded culture table so
/// <see cref="Loc.Get"/> never hits its visible-missing <c>[key]</c> fallback on the hot path.
///
/// <para>Messages are no-argument loc keys by design: argument-interpolated text (e.g. "must be ≥ {min}") is resolved
/// via <see cref="Loc.Format"/>, which allocates per call — pass such a message as a custom key whose template already
/// reads well ("Too short."), or accept the cold-path cost. This keeps the per-keystroke validation path zero-alloc.</para>
/// </summary>
public static class Rules
{
    /// <summary>The value must be non-empty / non-whitespace.</summary>
    public static Validator<string> Required(string locKey = "validation.required")
    {
        var msg = Msg.Key(locKey);
        return v => string.IsNullOrWhiteSpace(v) ? msg : MsgId.None;
    }

    /// <summary>The value must be at least <paramref name="n"/> characters long.</summary>
    public static Validator<string> MinLength(int n, string locKey = "validation.minlen")
    {
        var msg = Msg.Key(locKey);
        return v => (v?.Length ?? 0) < n ? msg : MsgId.None;
    }

    /// <summary>The value must be at most <paramref name="n"/> characters long.</summary>
    public static Validator<string> MaxLength(int n, string locKey = "validation.maxlen")
    {
        var msg = Msg.Key(locKey);
        return v => (v?.Length ?? 0) > n ? msg : MsgId.None;
    }

    /// <summary>The value must match <paramref name="rx"/> (build the <see cref="Regex"/> once at the call site — it is
    /// captured cold; under <c>TrimMode=full</c> it runs interpreted, which is fine on the blur/keystroke cold path).</summary>
    public static Validator<string> Matches(Regex rx, string locKey)
    {
        var msg = Msg.Key(locKey);
        return v => v is not null && rx.IsMatch(v) ? MsgId.None : msg;
    }

    /// <summary>A numeric value must fall within <paramref name="lo"/>..<paramref name="hi"/> (inclusive). An empty
    /// (<see cref="double.NaN"/>) value is treated as valid here — pair with a presence rule for required numbers.</summary>
    public static Validator<double> Range(double lo, double hi, string locKey = "validation.range")
    {
        var msg = Msg.Key(locKey);
        return v => double.IsNaN(v) || (v >= lo && v <= hi) ? MsgId.None : msg;
    }

    /// <summary>A custom predicate: the value is valid when <paramref name="ok"/> returns true.</summary>
    public static Validator<T> Predicate<T>(Func<T, bool> ok, string locKey)
    {
        var msg = Msg.Key(locKey);
        return v => ok(v) ? MsgId.None : msg;
    }

    /// <summary>Conditional: apply <paramref name="inner"/> only when <paramref name="cond"/> is true (a "required-if").
    /// <paramref name="cond"/> reads a sibling signal's <c>.Value</c>, so the field re-validates when that signal flips;
    /// when <paramref name="cond"/> is false the field is valid (the error clears and stops blocking submit).</summary>
    public static Validator<T> When<T>(Func<bool> cond, Validator<T> inner)
        => v => cond() ? inner(v) : MsgId.None;

    /// <summary>Cross-field equality: the value must equal <paramref name="other"/>'s current value (password ↔ confirm).
    /// Reading <c>other.Value</c> subscribes the field's error memo to <paramref name="other"/>, so editing either field
    /// re-validates this one.</summary>
    public static Validator<T> Equals<T>(IReadSignal<T> other, string locKey)
    {
        var msg = Msg.Key(locKey);
        var cmp = EqualityComparer<T>.Default;
        return v => cmp.Equals(other.Value, v) ? MsgId.None : msg;
    }

    /// <summary>Evaluate a rule array and return the first failing message (and the total fail count). A plain loop, no
    /// LINQ and no allocation — the rule array is captured once by the field's error memo at mount.</summary>
    /// <param name="allErrors">When false (default) short-circuits at the first failure; when true keeps counting.</param>
    internal static FieldError FirstFailing<T>(Validator<T>[] rules, T value, bool allErrors)
    {
        MsgId first = MsgId.None;
        byte count = 0;
        for (int i = 0; i < rules.Length; i++)
        {
            MsgId m = rules[i](value);
            if (m.IsEmpty) continue;
            if (count == 0) first = m;
            if (count < byte.MaxValue) count++;
            if (!allErrors) break;
        }
        return new FieldError(first, count);
    }
}
