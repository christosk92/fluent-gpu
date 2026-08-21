using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

/// <summary>The shared page frame every real setup page uses: a [<see cref="HeroView"/> column] beside [a content
/// column: a pinned eyebrow+title header over a <see cref="ScrollEl"/> body]. The hero column drops out ENTIRELY
/// under width pressure according to <see cref="SetupLayout"/>.
///
/// <para><see cref="Frame"/> defers to a <see cref="Component"/> (<see cref="SetupPageFrame"/>) purely so the
/// hero-drop can read <c>Viewport.Size</c> LIVE — a window resize re-evaluates it without remounting the page body
/// underneath, the same reason <c>ContentHost.PageFor</c> (Features/Shell/ContentHost.cs) wraps every real page in
/// its own <c>Embed.Comp</c> rather than reading context itself: a bare static function has no hook context of its
/// own to read <c>Viewport.Size</c> from.</para></summary>
static class SetupPageHost
{
    /// <param name="pinnedHeader">False omits the eyebrow+title row entirely — the prototype's two Zune bookends
    /// (Welcome, Done) are <c>.col.solo</c>: they carry their own display headline inside the body and must NOT
    /// reserve a second header above it, which would leave a blank band.</param>
    public static Element Frame(SetupPage page, string eyebrow, string title, Element body, bool pinnedHeader = true)
        => Embed.Comp(new SetupPageFrame.Props(page, eyebrow, title, body, pinnedHeader),
            () => new SetupPageFrame()) with { Key = "setup:frame:" + (int)page };

    internal static float Width => SetupLayout.HeroWidth;
}

/// <summary>The live-responsive half of <see cref="SetupPageHost.Frame"/>. The frame receives its slots through pushed
/// props: a page identity is stable inside one KeepAlive entry, but its body is rebuilt when page-local signals change
/// (notably when Spotify publishes a pairing challenge). Passing that body through the constructor would freeze the
/// first tree forever, because a reused <see cref="Component"/> never re-runs its factory.</summary>
sealed class SetupPageFrame : Component
{
    internal sealed record Props(SetupPage Page, string Eyebrow, string Title, Element Body, bool PinnedHeader);

    public override Element Render()
    {
        var p = UseProps<Props>();
        var viewport = UseContextSignal(Viewport.Size);
        float plateW = SetupLayout.PlateWidth(viewport.Value.Width);
        var tierSig = UseSignal(SetupLayout.NominalTierFor(plateW));
        UseEffect(() =>
        {
            var current = tierSig.Peek();
            var next = SetupLayout.TierFor(plateW, current);
            if (next != current) tierSig.Value = next;
        }, plateW);
        var tier = tierSig.Value;
        bool showHero = SetupLayout.ShowsHero(tier) && HeroView.Exists(p.Page);

        Element header = new BoxEl
        {
            Direction = 1, Gap = Spacing.XS, Shrink = 0f, MinWidth = 0f,
            Children =
            [
                WaveeType.Eyebrow(p.Eyebrow) with
                    { Color = Tok.TextTertiary, MinWidth = 0f, MaxLines = 1, Trim = TextTrim.CharacterEllipsis },
                WaveeType.PageHero(p.Title) with
                    { FontFamily = "Segoe UI Variable Display", MinWidth = 0f, Wrap = TextWrap.Wrap, MaxLines = 2, Trim = TextTrim.WordEllipsis },
            ],
        };

        Element content = new BoxEl
        {
            Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f, Gap = Spacing.M,
            // A headerless page is a Zune bookend, and those CENTER their block vertically (the prototype's
            // `.col.solo` + `justify-content:center`). A ScrollEl viewport sizes itself to its content, so a child
            // asking for Grow=1f/Justify=Center inside one has nothing to grow into and silently pins to the top —
            // which is exactly what it did. Bookends are authored to fit the plate, so they take the column directly.
            Children = p.PinnedHeader
                ? [header, ScrollView(p.Body) with { Shrink = 1f, MinWidth = 0f, MinHeight = 0f }]
                : [p.Body],
        };

        bool clearBack = !showHero && p.Page is >= SetupPage.SignIn and <= SetupPage.Notifications;
        var padding = new Edges4(
            Spacing.XXL,
            clearBack ? Spacing.XXXL + Spacing.XXL : Spacing.XXL,
            Spacing.XXL,
            Spacing.M);

        if (!showHero)
            return new BoxEl
            {
                Key = "setup:layout:" + (int)tier,
                Direction = 1, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
                Padding = padding, Children = [content],
            };

        return new BoxEl
        {
            Key = "setup:layout:" + (int)tier,
            Direction = 0, Grow = 1f, Shrink = 1f, MinWidth = 0f, MinHeight = 0f,
            Gap = Spacing.XXL, Padding = padding,
            Children =
            [
                HeroView.For(p.Page),
                content,
            ],
        };
    }
}
