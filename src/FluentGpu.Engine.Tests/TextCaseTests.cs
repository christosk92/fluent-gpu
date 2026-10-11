using System;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Hosting;
using FluentGpu.Hosting.Threading;
using FluentGpu.Pal;
using FluentGpu.Pal.Headless;
using FluentGpu.Reconciler;
using FluentGpu.Rhi.Headless;
using FluentGpu.Scene;
using FluentGpu.Text;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// E19: <see cref="TextEl.Case"/> / <see cref="TextCase"/>. The transform is applied culture-invariantly at SHAPING (into a
/// reused buffer, never a per-render string), is length preserving, and is part of the measure key. Serial: it builds a
/// headless host and changes the process culture.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class TextCaseTests
{
    [Fact]
    public void Lower_and_upper_are_culture_invariant()
    {
        var saved = CultureInfo.CurrentCulture;
        try
        {
            // The culture where ToLower("I") is a dotless i. The test host may run globalization-invariant (no cultures): the
            // mapping must be invariant either way, so the assertions below run with whatever culture is available.
            try { CultureInfo.CurrentCulture = new CultureInfo("tr-TR"); }
            catch (CultureNotFoundException) { }
            Assert.Equal("title", TextCaseMap.Apply("TITLE", TextCase.Lower));
            Assert.Equal("TITLE", TextCaseMap.Apply("title", TextCase.Upper));

            var scratch = new TextCaseScratch();
            Assert.Equal("title", scratch.Get("TITLE".AsSpan(), TextCase.Lower).ToString());
            Assert.Equal("TITLE", scratch.Get("title".AsSpan(), TextCase.Upper).ToString());
        }
        finally { CultureInfo.CurrentCulture = saved; }
    }

    [Theory]
    [InlineData("Hello, World")]
    [InlineData("Straße Ångström")]
    [InlineData("İstanbul ı")]
    [InlineData("")]
    public void Case_never_changes_the_utf16_length(string s)
    {
        var scratch = new TextCaseScratch();
        Assert.Equal(s.Length, scratch.Get(s.AsSpan(), TextCase.Lower).Length);
        Assert.Equal(s.Length, scratch.Get(s.AsSpan(), TextCase.Upper).Length);
    }

    [Fact]
    public void AsWritten_returns_the_written_text_itself()
    {
        var scratch = new TextCaseScratch();
        ReadOnlySpan<char> text = "Written As Is".AsSpan();
        var got = scratch.Get(text, TextCase.AsWritten);
        Assert.True(Unsafe.AreSame(ref MemoryMarshal.GetReference(text), ref MemoryMarshal.GetReference(got)));
        Assert.Equal(text.Length, got.Length);
        Assert.Same("x", TextCaseMap.Apply("x", TextCase.AsWritten));
    }

    [Fact]
    public void Case_is_part_of_the_measure_key()
    {
        var strings = new StringTable();
        var id = strings.Intern("Label");
        var family = strings.Intern("Segoe UI");
        var asWritten = new TextStyle(family, 14f, 400);
        var upper = asWritten with { Case = TextCase.Upper };
        var lower = asWritten with { Case = TextCase.Lower };
        Assert.NotEqual(asWritten, upper);
        Assert.NotEqual(upper, lower);
        Assert.Equal(upper, asWritten with { Case = TextCase.Upper });

        var entry = new TextMeasureEntry { Valid = true, Text = id, Style = upper, MaxW = 100f, Size = new Size2(40f, 17f) };
        Assert.True(entry.Matches(id, in upper, 100f));
        Assert.False(entry.Matches(id, in asWritten, 100f));
        Assert.False(entry.Matches(id, in lower, 100f));

        var cache = new TextMeasureCache();
        cache.Store(entry);
        Assert.True(cache.TryGet(id, in upper, 100f, out _));
        Assert.False(cache.TryGet(id, in asWritten, 100f, out _));
    }

    [Fact]
    public void A_body_font_epoch_change_misses_the_measure_cache()
    {
        var strings = new StringTable();
        var id = strings.Intern("Label");
        var style = new TextStyle(default, 14f, 400);
        var cache = new TextMeasureCache();
        cache.Store(new TextMeasureEntry { Valid = true, Text = id, Style = style, MaxW = 100f, FontEpoch = 3 });
        Assert.True(cache.TryGet(id, in style, 100f, out _, fontEpoch: 3));
        Assert.False(cache.TryGet(id, in style, 100f, out _, fontEpoch: 4));
    }

    [Fact]
    public void Shaping_the_case_transform_allocates_nothing_in_steady_state()
    {
        var scratch = new TextCaseScratch();
        string text = "The quick brown fox jumps over the lazy dog";
        for (int i = 0; i < 64; i++) { scratch.Get(text.AsSpan(), TextCase.Upper); scratch.Get(text.AsSpan(), TextCase.Lower); }   // warm: buffer sized, code jitted

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 10_000; i++)
        {
            _ = scratch.Get(text.AsSpan(), TextCase.Upper).Length;
            _ = scratch.Get(text.AsSpan(), TextCase.Lower).Length;
            _ = scratch.Get(text.AsSpan(), TextCase.AsWritten).Length;
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void The_reconciler_writes_the_case_into_the_layout_style()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var scene = new SceneStore();
        var recon = new TreeReconciler(scene, new StringTable());
        recon.ReconcileRoot(new TextEl("heading") { Case = TextCase.Upper }, null);
        Assert.Equal(TextCase.Upper, scene.Layout(scene.Root).TextStyle.Case);

        var plain = new SceneStore();
        var recon2 = new TreeReconciler(plain, new StringTable());
        recon2.ReconcileRoot(new TextEl("heading"), null);
        Assert.Equal(TextCase.AsWritten, plain.Layout(plain.Root).TextStyle.Case);
    }

    [Fact]
    public void The_recorded_glyph_run_carries_the_case_and_the_text_stays_as_written()
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        using var app = new HeadlessPlatformApp();
        var strings = new StringTable();
        var window = new HeadlessWindow(new WindowDesc("text-case", new Size2(320, 120), 1f));
        window.Show();
        var device = new HeadlessGpuDevice();
        using var host = new AppHost(app, window, device, new HeadlessFontSystem(strings), strings, new CaseRoot());
        for (int i = 0; i < 3; i++) host.Paint(0);

        Assert.NotEmpty(device.LastGlyphs);
        var run = device.LastGlyphs[0];
        Assert.Equal((int)TextCase.Upper, run.Case);
        Assert.Equal("Now Playing", strings.Resolve(run.Text));   // the interned string is never rewritten
    }

    private sealed class CaseRoot : Component
    {
        public override Element Render() => new TextEl("Now Playing") { Case = TextCase.Upper };
    }
}
