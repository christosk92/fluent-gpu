using System.Collections.Generic;
using FluentGpu.Animation;
using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using FluentGpu.Signals;

namespace Wavee;

/// <summary>One row of an "applying" step list — a near-copy of <c>LoginView.LoginStepRow</c> generalized off a
/// plain int <paramref name="mine"/>/<paramref name="stage"/> pair rather than the login takeover's own
/// <c>LoginStep</c>/<c>LoginSnapshot</c>, so the SAME row shape drives the Done page's four-row "Applying"
/// checklist. Reads SIGNALS rather than taking the stage as a prop (props freeze at mount) — this row's own
/// <paramref name="mine"/>/<paramref name="label"/> are constants, which is exactly what a frozen prop is for.
///
/// <para>Marks: pending = a dim bullet; current = <c>ProgressRing.Indeterminate</c>; done = a
/// checkmark with the same ~320ms <c>ScaleX</c>/<c>ScaleY</c> pop keyframes <c>LoginStepRow</c> fires from a
/// <c>UseEffect</c> keyed on <c>done</c>;
/// <paramref name="failed"/> (current step only) swaps the mark for a critical X — a step in this list is not
/// expected to fail in practice (every one either completes or lands "done on arrival"), but the row carries the
/// same failure vocabulary <c>LoginStepRow</c> does rather than silently dropping it.</para></summary>
sealed class SetupStepRow : Component
{
    readonly Signal<int> _stage;
    readonly int _mine;
    readonly string _label;
    readonly Signal<bool> _failed;

    public SetupStepRow(Signal<int> stage, int mine, string label, Signal<bool> failed)
    { _stage = stage; _mine = mine; _label = label; _failed = failed; }

    public override Element Render()
    {
        int cur = _stage.Value;         // subscribe → re-render as the apply step advances
        bool failed = _failed.Value;    // subscribe
        bool current = cur == _mine;
        bool failedNow = current && failed;
        bool done = cur > _mine;

        var iconRef = UseRef<NodeHandle>(default);
        UseEffect(() =>
        {
            if (!done || Motion.ReducedMotion) return;
            var anim = Context.Anim;
            var scene = Context.Scene;
            if (anim is null || scene is null || iconRef.Value.IsNull || !scene.IsLive(iconRef.Value)) return;
            var pop = new Keyframe[] { new(0f, 0.3f, Easing.EaseOut), new(0.55f, 1.18f, Easing.EaseOut), new(1f, 1f, Easing.EaseInOut) };
            anim.Keyframes(iconRef.Value, AnimChannel.ScaleX, pop, 320f, loop: false);
            anim.Keyframes(iconRef.Value, AnimChannel.ScaleY, pop, 320f, loop: false);
        }, done);

        Element mark = current && !failedNow
            ? ProgressRing.Indeterminate(16f)
            : new TextEl(failedNow ? Icons.Cancel : done ? Icons.Accept : Icons.RadioBullet)
            {
                Size = failedNow || done ? 15f : 11f,
                FontFamily = Theme.IconFont,
                Color = failedNow ? Tok.SystemFillCritical : done ? Tok.AccentDefault : Tok.TextTertiary,
            };

        return new BoxEl
        {
            Direction = 0, Gap = Spacing.S, AlignItems = FlexAlign.Center, Height = 26f,
            Enter = new EnterExit(Dx: -6f, Opacity: 0f, Active: true), Transition = MotionTok.ControlNormal,
            Children =
            [
                new BoxEl
                {
                    Width = 18f, Height = 18f, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
                    OnRealized = h => iconRef.Value = h, Children = [mark],
                },
                new TextEl(_label)
                {
                    Size = 12f, LineHeight = 16f,
                    Weight = current ? (ushort)600 : (ushort)400,
                    Color = current ? Tok.TextPrimary : done ? Tok.TextSecondary : Tok.TextTertiary,
                },
            ],
        };
    }
}

/// <summary>The <c>Stagger = 55f</c> column of <see cref="SetupStepRow"/>s — the Done page's "Applying" checklist.</summary>
static class SetupStepList
{
    public static Element Column(Signal<int> stage, Signal<bool> failed, IReadOnlyList<(int Stage, string Label)> steps)
    {
        var kids = new List<Element>(steps.Count);
        foreach (var step in steps)
            kids.Add(Embed.Comp(() => new SetupStepRow(stage, step.Stage, step.Label, failed)) with { Key = "step:" + step.Stage });
        return new BoxEl
        {
            Direction = 1, Gap = Spacing.XS, AlignSelf = FlexAlign.Stretch,
            Stagger = 55f,
            Children = kids.ToArray(),
        };
    }
}
