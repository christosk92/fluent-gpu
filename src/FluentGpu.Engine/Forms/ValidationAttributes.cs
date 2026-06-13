using System;

namespace FluentGpu.Forms;

/// <summary>
/// Marks a <c>partial</c> type whose properties/fields carry validation attributes. The <c>ValidatorGenerator</c>
/// (FluentGpu.Validation.SourceGen) emits, into a generated partial, a <c>static readonly Validator&lt;T&gt;[]</c> named
/// after each annotated member — so an app declares its rules once on a model and consumes them with
/// <c>UseField(signal, MyRules.Email)</c> instead of hand-writing <c>Rules.Required(), Rules.Matches(...)</c>.
///
/// <para>This is an OPTIONAL accelerator: it lowers to the IDENTICAL <see cref="Validator{T}"/> runtime contract the
/// hand-written delegate path uses — no reflection, no DataAnnotations, AOT-clean. The attributes here are deliberately
/// distinct from <c>System.ComponentModel.DataAnnotations</c> (which is reflection-driven and AOT-hostile).</para>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, Inherited = false)]
public sealed class ValidatableAttribute : Attribute { }

/// <summary>The string value must be non-empty / non-whitespace (→ <c>Rules.Required</c>).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class RequiredAttribute : Attribute
{
    /// <summary>The localization key for the failure message (default <c>validation.required</c>).</summary>
    public string LocKey = "validation.required";
}

/// <summary>The string value must be at least <see cref="Length"/> characters (→ <c>Rules.MinLength</c>).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class MinLengthAttribute : Attribute
{
    public MinLengthAttribute(int length) => Length = length;
    public int Length;
    public string LocKey = "validation.minlen";
}

/// <summary>The string value must be at most <see cref="Length"/> characters (→ <c>Rules.MaxLength</c>).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class MaxLengthAttribute : Attribute
{
    public MaxLengthAttribute(int length) => Length = length;
    public int Length;
    public string LocKey = "validation.maxlen";
}

/// <summary>The numeric (double) value must fall within [<see cref="Min"/>, <see cref="Max"/>] (→ <c>Rules.Range</c>).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class RangeAttribute : Attribute
{
    public RangeAttribute(double min, double max) { Min = min; Max = max; }
    public double Min;
    public double Max;
    public string LocKey = "validation.range";
}

/// <summary>The string value must match <see cref="Pattern"/> (→ <c>Rules.Matches</c>; the Regex is built once, cold).</summary>
[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field)]
public sealed class RegexMatchAttribute : Attribute
{
    public RegexMatchAttribute(string pattern, string locKey) { Pattern = pattern; LocKey = locKey; }
    public string Pattern;
    public string LocKey;
}
