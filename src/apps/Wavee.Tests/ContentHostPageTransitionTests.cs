using System.Linq;
using FluentGpu.Controls;
using FluentGpu.Foundation;
using FluentGpu.Hooks;
using Xunit;

namespace Wavee.Tests;

// The content card's page-swap policy (ContentHost's PURE half — PageNavMotion). Two regressions live here:
//
//  1. The card CUT TO EMPTY on every navigation. ContentHost used to hand the reconciler `recipe with { Exit = default }`,
//     so KeepAlive took the no-exit branch: the outgoing page was detached in the same frame the incoming one was seeded
//     at opacity 0. The whole 250 ms was therefore "empty card, then the new page fades up". A recipe that keeps its Exit
//     makes the reconciler mark the boundary a ZStack, keep the old root drawing (hit-test invisible) and park it once
//     the tracks settle — the pages overlap, which is the entire point of a page transition.
//
//  2. A motion-only write re-faded the page. The keep-alive token used to be (TabId, Route, Motion) while SlotKey
//     ignored Motion, so writing `_navMotion` alone (tab activation / open / close all write Neutral) changed the token
//     on the ACTIVE key — which the reconciler reads as an activation change and re-seeds the entrance, i.e. a full-page
//     re-fade with no content change whatsoever.
public class ContentHostPageTransitionTests
{
    // ── 2. slot identity ────────────────────────────────────────────────────────────────────────────────────────────
    [Fact]
    public void PageSlot_CarriesNoNavDirection()
    {
        var members = typeof(PageSlot).GetProperties().Select(p => p.PropertyType)
            .Concat(typeof(PageSlot).GetFields().Select(f => f.FieldType));
        Assert.DoesNotContain(typeof(NavTransitionKind), members);
    }

    [Fact]
    public void SameTabAndRoute_IsTheSameToken_WhateverDirectionReachedIt()
    {
        // The reconciler compares TOKENS on the active key; equal tokens = no activation change = no re-seeded entrance.
        var a = new PageSlot(3, new Route("album:spotify:album:x", "Kid A"));
        var b = new PageSlot(3, new Route("album:spotify:album:x", "Kid A"));
        Assert.Equal(a, b);
        Assert.Equal(PageNavMotion.SlotKey(a), PageNavMotion.SlotKey(b));
    }

    [Fact]
    public void DifferentTabOrRoute_IsADifferentSlot()
    {
        var home = new PageSlot(1, new Route("home"));
        Assert.NotEqual(home, new PageSlot(2, new Route("home")));                 // per-tab: no shared page state
        Assert.NotEqual(home, new PageSlot(1, new Route("settings")));
        Assert.NotEqual(PageNavMotion.SlotKey(home), PageNavMotion.SlotKey(new PageSlot(2, new Route("home"))));
        Assert.NotEqual(PageNavMotion.SlotKey(home), PageNavMotion.SlotKey(new PageSlot(1, new Route("settings"))));
    }

    [Fact]
    public void SearchIsOneWorkspacePerTab_ItsQueryDoesNotForkTheSlot()
    {
        Assert.Equal(PageNavMotion.SlotKey(new PageSlot(1, new Route("search", "radiohead"))),
                     PageNavMotion.SlotKey(new PageSlot(1, new Route("search", "aphex twin"))));
        Assert.NotEqual(PageNavMotion.SlotKey(new PageSlot(1, new Route("search", "radiohead"))),
                        PageNavMotion.SlotKey(new PageSlot(2, new Route("search", "radiohead"))));
    }

    [Fact]
    public void EveryOtherRouteArgOwnsItsOwnSlot()
    {
        Assert.NotEqual(PageNavMotion.SlotKey(new PageSlot(1, new Route("pl:a", "A"))),
                        PageNavMotion.SlotKey(new PageSlot(1, new Route("pl:b", "B"))));
    }

    // ── 1. the recipes keep BOTH halves ─────────────────────────────────────────────────────────────────────────────
    // NavTransitionKind is internal, so this cannot be an [InlineData] Theory (a public test method may not take a
    // less-accessible parameter) — the loop is the same coverage.
    [Fact]
    public void EveryDirection_HasAnActiveEnterAndAnActiveExit()
    {
        foreach (var motion in new[] { NavTransitionKind.Forward, NavTransitionKind.Back, NavTransitionKind.Neutral })
        {
            var recipe = PageNavMotion.RecipeFor(motion);
            Assert.True(recipe.Enter.Active, $"{motion}: the incoming page must animate in");
            Assert.True(recipe.Exit.Active, $"{motion}: a stripped Exit detaches the outgoing page in the same frame — the card flashes empty");
            Assert.NotEqual(default, recipe.Exit);
            Assert.Equal(0f, recipe.Exit.Opacity);          // it fades out rather than vanishing
            Assert.True(recipe.Dynamics.DurationMs > 0f || recipe.Dynamics.Kind == DynamicsKind.Spring, $"{motion}: no dynamics");
        }
    }

    [Fact]
    public void DirectionsMapToTheirRecipes_AndTheSlidesAreMirrors()
    {
        Assert.Equal(MotionRecipes.PageSlideForward, PageNavMotion.RecipeFor(NavTransitionKind.Forward));
        Assert.Equal(MotionRecipes.PageSlideBack, PageNavMotion.RecipeFor(NavTransitionKind.Back));
        Assert.Equal(MotionRecipes.PageFade, PageNavMotion.RecipeFor(NavTransitionKind.Neutral));

        var fwd = PageNavMotion.RecipeFor(NavTransitionKind.Forward);
        var back = PageNavMotion.RecipeFor(NavTransitionKind.Back);
        // forward: the new page arrives from +X while the old one leaves toward −X (and back is the exact reverse)
        Assert.True(fwd.Enter.Dx > 0f && fwd.Exit.Dx < 0f);
        Assert.True(back.Enter.Dx < 0f && back.Exit.Dx > 0f);
        Assert.Equal(fwd.Enter.Dx, -fwd.Exit.Dx);
        Assert.Equal(fwd.Enter.Dx, -back.Enter.Dx);
        // neutral (tab activation / open / close) is a pure cross-fade: no direction to imply
        var neutral = PageNavMotion.RecipeFor(NavTransitionKind.Neutral);
        Assert.Equal(0f, neutral.Enter.Dx);
        Assert.Equal(0f, neutral.Exit.Dx);
    }

    [Fact]
    public void NoRecipeBlursThePageRoot()
    {
        // a page-sized blur group is a canvas offscreen RT + a 2-pass Gaussian per frame — outside the frame budget
        foreach (var motion in new[] { NavTransitionKind.Forward, NavTransitionKind.Back, NavTransitionKind.Neutral })
        {
            var recipe = PageNavMotion.RecipeFor(motion);
            Assert.Equal(0f, recipe.Enter.Blur);
            Assert.Equal(0f, recipe.Exit.Blur);
        }
    }
}
