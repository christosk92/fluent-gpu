using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using static FluentGpu.Dsl.Ui;

namespace Wavee;

// A reusable horizontal rail: a titled strip of cards you page through with end CHEVRONS, the strip eased into place
// (UseAnimatedValue) and the overflowing edges dissolved with the engine's EdgeFade. Used by Home (and any shelf).
sealed class Rail : Component
{
    readonly string _title;
    readonly Element[] _cards;
    readonly float _cardW, _gap, _height;

    public Rail(string title, Element[] cards, float cardWidth, float gap, float height)
    {
        _title = title; _cards = cards; _cardW = cardWidth; _gap = gap; _height = height;
    }

    public override Element Render()
    {
        var (page, setPage) = UseState(0);

        float stride = _cardW + _gap;
        float contentW = _cards.Length * stride;
        float pageStride = 3 * stride;                       // page by ~3 cards
        int maxPage = Math.Max(0, (int)Math.Ceiling(_cards.Length / 3.0) - 1);
        if (page > maxPage) page = maxPage;

        float target = Math.Min(page * pageStride, Math.Max(0f, contentW - pageStride));
        float x = UseAnimatedValue(target, 320f, Easing.SmoothOut);   // smooth glide between pages

        bool canPrev = page > 0, canNext = page < maxPage;
        EdgeMask mask = (canPrev, canNext) switch
        {
            (true, true) => EdgeMask.Horizontal,
            (true, false) => EdgeMask.Left,
            (false, true) => EdgeMask.Right,
            _ => EdgeMask.None,
        };

        var strip = new BoxEl { Direction = 0, Gap = _gap, OffsetX = -x, Children = _cards };

        var viewport = new BoxEl
        {
            Grow = 1, Height = _height, ClipToBounds = true,
            EdgeFade = mask == EdgeMask.None ? null : new EdgeFadeSpec(mask, 36f),
            Children = [strip],
        };

        var header = new BoxEl
        {
            Direction = 0, AlignItems = FlexAlign.Center, Gap = WaveeSpace.S,
            Children =
            [
                WaveeType.RailHeader(_title),
                new BoxEl { Grow = 1 },
                Chevron(Mdl.ChevronLeft, canPrev, () => setPage(page - 1)),
                Chevron(Mdl.ChevronRight, canNext, () => setPage(page + 1)),
            ],
        };

        return new BoxEl { Direction = 1, Gap = WaveeSpace.M, Children = [header, viewport] };
    }

    static Element Chevron(string glyph, bool enabled, Action onClick) => new BoxEl
    {
        Width = 32, Height = 32, AlignItems = FlexAlign.Center, Justify = FlexJustify.Center,
        Corners = CornerRadius4.All(16f), Fill = Tok.FillControlDefault,
        HoverFill = enabled ? Tok.FillControlSecondary : Tok.FillControlDefault,
        Opacity = enabled ? 1f : 0.35f, OnClick = enabled ? onClick : null,
        Children = [Icon(glyph, 13f, Tok.TextSecondary)],
    };
}
