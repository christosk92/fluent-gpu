using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hosting.Threading;
using FluentGpu.Layout;
using FluentGpu.Reconciler;
using FluentGpu.Scene;
using FluentGpu.Text.Headless;
using Xunit;

namespace FluentGpu.Engine.Tests;

/// <summary>
/// <c>TextEl.MinSize</c> auto-fit on a single-line <c>NoWrap</c> run (a hero name: 68 on an 80 line, floor 40, one line,
/// ellipsis). It used to act only on wrapping runs, so a NoWrap name ellipsised at full size. Now a NoWrap run shrinks
/// until its one line fits the column, floors at MinSize and only then trims; a run that fits keeps its size; the authored
/// line box stays put. Driven through the shipping reconciler and <see cref="FlexLayout"/> on the headless font system
/// (advance = 0.62 em at weight 600), reading the measure cache's FitSize (what the recorder draws at) and the laid box.
/// </summary>
[Collection(SerialTestCollection.Name)]
public sealed class TextFitNoWrapTests
{
    private const float Size = 68f, Line = 80f, Floor = 40f;
    private const string Long = "League of Legends";   // 17 chars: 716.72 wide at 68, 421.6 at 40

    private static TextEl Name(string text, float width, TextTrim trim = TextTrim.CharacterEllipsis) => new(text)
    {
        Size = Size, LineHeight = Line, MinSize = Floor, Weight = 600, Width = width,
        Wrap = TextWrap.NoWrap, MaxLines = 1, Trim = trim,
    };

    private static (SceneStore Scene, NodeHandle Text) Lay(TextEl text)
    {
        ThreadGuard.BindCurrent(ThreadGuard.ThreadRole.Ui);
        var strings = new StringTable();
        var scene = new SceneStore();
        new TreeReconciler(scene, strings).ReconcileRoot(new BoxEl { Direction = 1, AlignItems = FlexAlign.Start, Children = [text] }, null);
        new FlexLayout(scene, new HeadlessFontSystem(strings)).Run(scene.Root, new Size2(1000f, 1000f));
        return (scene, scene.FirstChild(scene.Root));
    }

    private static float FitOf(SceneStore scene, NodeHandle node)
        => scene.MeasureCacheRef(node).ResolveForWidth(scene.Bounds(node).W).FitSize;

    [Fact]
    public void A_long_name_shrinks_until_its_one_line_fits()
    {
        var (scene, node) = Lay(Name(Long, 500f));
        float fit = FitOf(scene, node);
        Assert.InRange(fit, Floor, Size - 1f);
        float lineW = Long.Length * fit * 0.62f;
        Assert.True(lineW <= 500f, $"the fitted line ({lineW}) is wider than its column");
        Assert.True(lineW >= 499f, $"the fitted line ({lineW}) leaves the column under-filled: the solve is not tight");
        // Only the em shrinks; the authored LineHeight stays (as on the wrapping path), so under MaxHeight stacking the box
        // is max(the natural line at the fitted size, 80): here the 80 line box.
        Assert.Equal(Line, scene.Bounds(node).H);
    }

    [Fact]
    public void A_name_too_long_for_the_floor_stays_at_the_floor_and_trims()
    {
        var (scene, node) = Lay(Name(Long, 300f));
        Assert.Equal(Floor, FitOf(scene, node));
        Assert.Equal(300f, scene.Bounds(node).W);   // the trimmed run is cut to its column
        Assert.Equal(Line, scene.Bounds(node).H);
    }

    [Fact]
    public void A_short_name_keeps_its_full_size()
    {
        var (scene, node) = Lay(Name("Hits", 500f));
        var (plainScene, plain) = Lay(Name("Hits", 500f) with { MinSize = float.NaN });
        Assert.Equal(0f, FitOf(scene, node));   // 0 = no fit: the recorder shapes at the authored 68
        Assert.Equal(plainScene.Bounds(plain), scene.Bounds(node));   // the box of the same run without a floor
    }

    [Fact]
    public void An_untrimmed_NoWrap_run_still_gets_a_column_to_fit()
    {
        var (scene, node) = Lay(Name(Long, 500f, TextTrim.None));
        Assert.InRange(FitOf(scene, node), Floor, Size - 1f);
    }

    [Fact]
    public void The_fitted_size_follows_the_column_without_steps()
    {
        float prev = float.NaN;
        for (float w = 640f; w >= 440f; w -= 10f)
        {
            var (scene, node) = Lay(Name(Long, w));
            float fit = FitOf(scene, node);
            float want = (w - 0.5f) / (Long.Length * 0.62f);
            Assert.InRange(fit, want - 0.05f, want + 0.05f);
            if (!float.IsNaN(prev)) Assert.True(fit < prev);
            prev = fit;
        }
    }

    [Fact]
    public void A_floor_that_can_never_act_is_reported_in_debug()
    {
        TextFitContract.Reset();
        Lay(new TextEl("wraps forever") { Size = 20f, MinSize = 12f, Wrap = TextWrap.Wrap, Width = 80f });   // MaxLines = 0
        Lay(new TextEl("above") { Size = 20f, MinSize = 28f, Width = 80f });
        Lay(Name(Long, 500f));   // a live floor: no report
#if DEBUG
        Assert.Equal(2, TextFitContract.Violations);
#else
        Assert.Equal(0, TextFitContract.Violations);
#endif
        TextFitContract.Reset();
    }
}
