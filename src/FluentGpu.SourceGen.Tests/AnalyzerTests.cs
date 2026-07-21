using FluentGpu.SourceGen.Analyzers;
using Xunit;

namespace FluentGpu.SourceGen.Tests;

/// <summary>Per-rule positive + negative coverage for FGRP001–FGRP007, plus the FGRP001/FGRP002 behavior lock-ins
/// (genuine bug fires; the false-positive shapes we fixed do NOT).</summary>
public sealed class AnalyzerTests
{
    private const string Usings =
        "using FluentGpu.Dsl; using FluentGpu.Hooks; using FluentGpu.Signals; using System;\n";

    // ── FGRP001 — frozen Element content slot ────────────────────────────────────────────────────────────────────
    [Fact]
    public void FGRP001_Fires_On_Element_Field_From_Captured_Local()
    {
        var diags = Harness.Analyze(new FrozenPropsAnalyzer(), Usings + """
            sealed class Wrapper : Component { public Element Slot = new BoxEl(); public override Element Render() => Slot; }
            sealed class Host : Component {
                public override Element Render() {
                    Element body = new BoxEl();
                    return Embed.Comp(() => new Wrapper { Slot = body });
                }
            }
            """);
        Assert.Equal(1, Harness.Count(diags, "FGRP001"));
        Assert.Contains("body", Harness.SpanText(diags[0]));
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning, diags[0].Severity);  // promoted in G4f
    }

    [Fact]
    public void FGRP001_Silent_On_Literal_Content()
    {
        var diags = Harness.Analyze(new FrozenPropsAnalyzer(), Usings + """
            sealed class Wrapper : Component { public Element Slot = new BoxEl(); public override Element Render() => Slot; }
            sealed class Host : Component {
                public override Element Render() => Embed.Comp(() => new Wrapper { Slot = new BoxEl() });
            }
            """);
        Assert.Equal(0, Harness.Count(diags, "FGRP001"));
    }

    [Fact] // lock-in for the analyzer FP fix: a Func<Element> thunk field is a STABLE reference, not frozen content.
    public void FGRP001_Silent_On_FuncOfElement_Field()
    {
        var diags = Harness.Analyze(new FrozenPropsAnalyzer(), Usings + """
            sealed class Wrapper : Component { public Func<Element> Slot = () => new BoxEl(); public override Element Render() => Slot(); }
            sealed class Host : Component {
                public override Element Render() {
                    Func<Element> f = () => new BoxEl();
                    return Embed.Comp(() => new Wrapper { Slot = f });
                }
            }
            """);
        Assert.Equal(0, Harness.Count(diags, "FGRP001"));
    }

    // ── FGRP002 — mount-time signal snapshot capture ─────────────────────────────────────────────────────────────
    [Fact]
    public void FGRP002_Fires_On_Signal_Value_Snapshot_Local()
    {
        var diags = Harness.Analyze(new MountOwnedBindingAnalyzer(), Usings + """
            sealed class C : Component {
                Signal<int> sig = new(0);
                public override Element Render() {
                    int snap = sig.Value;
                    return new TextEl("") { Text = Prop.Of(() => "n=" + snap) };
                }
            }
            """);
        Assert.Equal(1, Harness.Count(diags, "FGRP002"));
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Warning, diags[0].Severity);  // promoted in G4f
    }

    [Fact] // lock-in: a thunk that reads .Value directly subscribes — no snapshot bug.
    public void FGRP002_Silent_On_Reactive_Thunk()
    {
        var diags = Harness.Analyze(new MountOwnedBindingAnalyzer(), Usings + """
            sealed class C : Component {
                Signal<int> sig = new(0);
                public override Element Render() => new TextEl("") { Text = Prop.Of(() => "n=" + sig.Value) };
            }
            """);
        Assert.Equal(0, Harness.Count(diags, "FGRP002"));
    }

    [Fact] // lock-in for the FP fix: `this` / instance-field capture is mount-stable, not a snapshot.
    public void FGRP002_Silent_On_This_Capture()
    {
        var diags = Harness.Analyze(new MountOwnedBindingAnalyzer(), Usings + """
            sealed class C : Component {
                int _x = 5;
                public override Element Render() => new TextEl("") { Text = Prop.Of(() => "n=" + _x) };
            }
            """);
        Assert.Equal(0, Harness.Count(diags, "FGRP002"));
    }

    [Fact] // lock-in for the FP fix: a plain non-signal local is a mount-stable config value, not a snapshot.
    public void FGRP002_Silent_On_NonSignal_Local()
    {
        var diags = Harness.Analyze(new MountOwnedBindingAnalyzer(), Usings + """
            sealed class C : Component {
                public override Element Render() {
                    string name = "x";
                    return new TextEl("") { Text = Prop.Of(() => "n=" + name) };
                }
            }
            """);
        Assert.Equal(0, Harness.Count(diags, "FGRP002"));
    }

    // ── FGRP003 — peek-only bind thunk ───────────────────────────────────────────────────────────────────────────
    [Fact]
    public void FGRP003_Fires_On_Peek_Only_Thunk()
    {
        var diags = Harness.Analyze(new PeekOnlyBindThunkAnalyzer(), Usings + """
            sealed class C : Component {
                Signal<int> sig = new(0);
                public override Element Render() => new TextEl("") { Text = Prop.Of(() => "n=" + sig.Peek()) };
            }
            """);
        Assert.Equal(1, Harness.Count(diags, "FGRP003"));
        Assert.Contains("Peek", Harness.SpanText(diags[0]));
    }

    [Fact]
    public void FGRP003_Silent_When_Thunk_Reads_Value()
    {
        var diags = Harness.Analyze(new PeekOnlyBindThunkAnalyzer(), Usings + """
            sealed class C : Component {
                Signal<int> sig = new(0);
                public override Element Render() => new TextEl("") { Text = Prop.Of(() => sig.Peek() > 0 ? "hi" : "n=" + sig.Value) };
            }
            """);
        Assert.Equal(0, Harness.Count(diags, "FGRP003"));
    }

    // ── FGRP004 — discarded element expression ───────────────────────────────────────────────────────────────────
    [Fact]
    public void FGRP004_Fires_On_Discarded_Modifier()
    {
        var diags = Harness.Analyze(new DiscardedElementAnalyzer(), Usings + """
            static class Ext { public static Element Mod(this Element e) => e; }
            sealed class C : Component {
                public override Element Render() {
                    Element box = new BoxEl();
                    box.Mod();
                    return box;
                }
            }
            """);
        Assert.Equal(1, Harness.Count(diags, "FGRP004"));
        Assert.Contains("box.Mod()", Harness.SpanText(diags[0]));
    }

    [Fact]
    public void FGRP004_Silent_When_Result_Used()
    {
        var diags = Harness.Analyze(new DiscardedElementAnalyzer(), Usings + """
            static class Ext { public static Element Mod(this Element e) => e; }
            sealed class C : Component {
                public override Element Render() {
                    Element box = new BoxEl();
                    var kept = box.Mod();
                    return kept;
                }
            }
            """);
        Assert.Equal(0, Harness.Count(diags, "FGRP004"));
    }

    [Fact] // lock-in for the FP fix: a render/side-effect call on a NON-element receiver is a legitimate discard.
    public void FGRP004_Silent_On_Render_Call()
    {
        var diags = Harness.Analyze(new DiscardedElementAnalyzer(), Usings + """
            sealed class Probe : Component { public override Element Render() => new BoxEl(); }
            sealed class C : Component {
                public override Element Render() {
                    var p = new Probe();
                    p.Render();
                    return new BoxEl();
                }
            }
            """);
        Assert.Equal(0, Harness.Count(diags, "FGRP004"));
    }

    // ── FGRP005 — positional hook in a loop (Info) ───────────────────────────────────────────────────────────────
    [Fact]
    public void FGRP005_Fires_On_Hook_In_Loop()
    {
        var diags = Harness.Analyze(new LoopHookOrdinalAnalyzer(), Usings + """
            sealed class C : Component {
                public override Element Render() {
                    for (int i = 0; i < 3; i++) {
                        var s = UseSignal(0);
                    }
                    return new BoxEl();
                }
            }
            """);
        Assert.Equal(1, Harness.Count(diags, "FGRP005"));
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Info, diags[0].Severity);
    }

    [Fact] // conditional hooks are legal on the keyed substrate — must NOT be flagged.
    public void FGRP005_Silent_On_Hook_In_Conditional()
    {
        var diags = Harness.Analyze(new LoopHookOrdinalAnalyzer(), Usings + """
            sealed class C : Component {
                public bool Cond;
                public override Element Render() {
                    if (Cond) { var s = UseSignal(0); }
                    return new BoxEl();
                }
            }
            """);
        Assert.Equal(0, Harness.Count(diags, "FGRP005"));
    }

    // ── FGRP006 — fresh reference to DepKey.FromRef ──────────────────────────────────────────────────────────────
    [Fact]
    public void FGRP006_Fires_On_Fresh_Object()
    {
        var diags = Harness.Analyze(new FromRefFreshReferenceAnalyzer(), Usings + """
            sealed class C : Component {
                public override Element Render() {
                    UseEffect(() => { }, DepKey.FromRef(new object()));
                    return new BoxEl();
                }
            }
            """);
        Assert.Equal(1, Harness.Count(diags, "FGRP006"));
        Assert.Contains("new object()", Harness.SpanText(diags[0]));
    }

    [Fact]
    public void FGRP006_Silent_On_Stable_Reference()
    {
        var diags = Harness.Analyze(new FromRefFreshReferenceAnalyzer(), Usings + """
            sealed class C : Component {
                object _stable = new object();
                public override Element Render() {
                    UseEffect(() => { }, DepKey.FromRef(_stable));
                    return new BoxEl();
                }
            }
            """);
        Assert.Equal(0, Harness.Count(diags, "FGRP006"));
    }

    // ── FGRP007 — Prop<T> channel without an initializer ─────────────────────────────────────────────────────────
    [Fact]
    public void FGRP007_Fires_On_Uninitialized_Channel()
    {
        var diags = Harness.Analyze(new UninitializedPropChannelAnalyzer(), Usings + """
            sealed record MyEl : Element {
                public override ushort ElementTypeId => 999;
                public Prop<int> Foo { get; init; }
            }
            """);
        Assert.Equal(1, Harness.Count(diags, "FGRP007"));
        Assert.Contains("Foo", Harness.SpanText(diags[0]));
    }

    [Fact]
    public void FGRP007_Silent_When_Initialized()
    {
        var diags = Harness.Analyze(new UninitializedPropChannelAnalyzer(), Usings + """
            sealed record MyEl : Element {
                public override ushort ElementTypeId => 999;
                public Prop<int> Foo { get; init; } = 0;
            }
            """);
        Assert.Equal(0, Harness.Count(diags, "FGRP007"));
    }

    [Fact] // a Prop<T> on a non-Element type is not a channel.
    public void FGRP007_Silent_On_NonElement_Type()
    {
        var diags = Harness.Analyze(new UninitializedPropChannelAnalyzer(), Usings + """
            sealed record NotAnElement {
                public Prop<int> Foo { get; init; }
            }
            """);
        Assert.Equal(0, Harness.Count(diags, "FGRP007"));
    }
}
