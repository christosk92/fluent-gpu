using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Hooks;

namespace Wavee;

/// <summary>Which direction the NEXT page swap travels. The shell's navigation verbs (Go / Back / Forward / tab
/// activation) write this signal BEFORE the route signal in the same flush, so the reconciler can Peek it (an untracked
/// read — a motion-only write must never re-run the keep-alive boundary) and get the direction that belongs to the
/// route it is about to activate.</summary>
enum NavTransitionKind : byte { Forward, Back, Neutral }

/// <summary>The IDENTITY of a keep-alive page slot: which browser tab, which route. The nav DIRECTION is deliberately
/// NOT part of it — direction decides how a swap animates, not which page is cached. Folding it in made a motion-only
/// write on the already-active key look like an activation change, which re-seeded the entrance and re-faded the whole
/// page with no content change at all.</summary>
readonly record struct PageSlot(int TabId, Route Route);

/// <summary>The page-swap policy of the content card: slot identity (<see cref="SlotKey"/>) and the motion recipe a
/// direction maps to (<see cref="RecipeFor"/>). Split out of <c>ContentHost</c> because it is pure — no pages, no
/// controls, no GPU — so it can be pinned by tests.</summary>
static class PageNavMotion
{
    /// <summary>Every destination page gets its own slot inside the active tab, so ALL forward/back navigation uses the
    /// same page-slide language (Fluent Frame SlideNavigationTransitionInfo / Zune panorama: the page moves, the content
    /// does not then cascade). Search remains ONE live workspace because its query changes in place as the omnibar is
    /// edited — its Arg is deliberately excluded from the key.</summary>
    public static string SlotKey(PageSlot s)
    {
        if (s.Route.Name == "search") return s.TabId + "\u001Fsearch";
        return s.TabId + "\u001F" + s.Route.Name + "\u001F" + (s.Route.Arg ?? "");
    }

    /// <summary>The recipe for a page swap, WITH its Exit half. Both halves are load-bearing: the reconciler's
    /// <c>BeginKeepAliveExit</c> only overlaps the outgoing page (ZStack on the boundary, hit-test invisible, parked
    /// once its tracks settle) when <c>Exit.Active</c> is true — with a stripped Exit the outgoing page is detached in
    /// the same frame and the card flashes EMPTY before the incoming page arrives.</summary>
    public static LayoutTransition RecipeFor(NavTransitionKind motion) => motion switch
    {
        NavTransitionKind.Back => MotionRecipes.PageSlideBack,
        NavTransitionKind.Neutral => MotionRecipes.PageFade,
        _ => MotionRecipes.PageSlideForward,
    };
}
