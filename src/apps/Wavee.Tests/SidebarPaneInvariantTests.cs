using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using Xunit;

namespace Wavee.Tests;

public class SidebarPaneInvariantTests
{
    static SidebarPaneFrameSnapshot Expanded(float preferred = 320f, float rendered = 320f) => new(
        SidebarDesign.Curated,
        UserCollapsed: false,
        PresentedCompact: false,
        PreferredExpandedWidth: preferred,
        RenderedPaneWidth: rendered,
        ExpandedOpacity: 1f,
        RailOpacity: 0f,
        ExpandedHitTestVisible: true,
        RailHitTestVisible: false);

    static SidebarPaneFrameSnapshot Compact(float rendered = ShellResponsiveLayout.CompactRailW) => new(
        SidebarDesign.LibraryV3,
        UserCollapsed: true,
        PresentedCompact: true,
        PreferredExpandedWidth: 340f,
        RenderedPaneWidth: rendered,
        ExpandedOpacity: 0f,
        RailOpacity: 1f,
        ExpandedHitTestVisible: false,
        RailHitTestVisible: true);

    [Fact]
    public void ExpandedTerminalState_IsValid()
    {
        var state = Expanded();
        Assert.Equal(SidebarPaneInvariantFault.None, SidebarPaneInvariant.Inspect(in state));
    }

    [Fact]
    public void CompactTerminalState_IsValid()
    {
        var state = Compact();
        Assert.True(SidebarPaneInvariant.IsValid(in state));
    }

    [Fact]
    public void ReportedTwentyFourDipSliver_IsRejected()
    {
        var state = Expanded(rendered: 24f);
        var fault = SidebarPaneInvariant.Inspect(in state);
        Assert.True(fault.HasFlag(SidebarPaneInvariantFault.ExpandedWidthOutOfRange));
        Assert.True(fault.HasFlag(SidebarPaneInvariantFault.ExpandedWidthMismatch));
    }

    [Theory]
    [InlineData(55.49f, false)]
    [InlineData(55.5f, true)]
    [InlineData(56.5f, true)]
    [InlineData(56.51f, false)]
    public void CompactWidth_UsesHalfDipTolerance(float rendered, bool valid)
    {
        var state = Compact(rendered);
        Assert.Equal(valid, SidebarPaneInvariant.IsValid(in state));
    }

    [Fact]
    public void WrongLayerOwnership_IsRejected()
    {
        var state = Expanded() with
        {
            ExpandedOpacity = 0f,
            RailOpacity = 1f,
            ExpandedHitTestVisible = false,
            RailHitTestVisible = true,
        };
        var fault = SidebarPaneInvariant.Inspect(in state);
        Assert.True(fault.HasFlag(SidebarPaneInvariantFault.LayerOpacityMismatch));
        Assert.True(fault.HasFlag(SidebarPaneInvariantFault.HitTestOwnerMismatch));
    }

    [Fact]
    public void ExpandedWidthMustMatchTheRememberedPreference()
    {
        var state = Expanded(preferred: 360f, rendered: 320f);
        Assert.True(SidebarPaneInvariant.Inspect(in state)
            .HasFlag(SidebarPaneInvariantFault.ExpandedWidthMismatch));
    }

    [Fact]
    public void NonFiniteGeometry_IsRejectedWithoutFurtherClassification()
    {
        var state = Expanded() with { RenderedPaneWidth = float.NaN };
        Assert.Equal(SidebarPaneInvariantFault.NonFiniteValue, SidebarPaneInvariant.Inspect(in state));
    }

    /// <summary>THE PANE'S CONTEXT MENU MAY NOT HANG OFF THE PANE ROOT. <c>OnContextRequested</c> sets
    /// <c>InteractionInfo.ContextBit</c>, and ContextBit is in <c>InputDispatcher.Hit</c>'s hit-anywhere mask — an
    /// element with a context flyout is a hit-test target in its own right (the WinUI rule). A menu on the root
    /// therefore made the ROOT the press/hover owner for every dead spot in the sidebar (the rail's separator, the gap
    /// between tiles, the pane's padding), which points every engine cascade at a node whose subtree is the whole
    /// sidebar. The fix is the immersive stage's, verbatim (<c>StageIdentity.ContextScope</c>): a ZStack SHELL plus a
    /// CHILDLESS full-bleed SHIELD beneath the content — <c>Hit</c> takes the LAST matching child, so the content still
    /// wins wherever it hits, the shield takes everything else, and a cascade from a childless node reaches nothing.
    /// The shell keeps ContextBit as an ANCESTOR, which is all right-click-anywhere ever needed.
    /// <para>The shield staying CHILDLESS is the whole contract, so it is pinned as one literal.</para></summary>
    [Fact]
    public void ThePaneMenu_HangsOffAChildlessShieldAndNotTheRoot()
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return; }

        string pane = File.ReadAllText(Path.Combine(root, "Features", "Sidebar", "Pane", "SidebarPane.cs"));
        Assert.Contains("new BoxEl { Key = ContextShieldKey }.WithContextMenu(", pane);
        // Exactly two attach points — the shell and the shield — and nothing else in the pane owns a menu.
        Assert.Equal(2, Regex.Matches(pane, @"\.WithContextMenu\(").Count);
        // The shell is a ZStack: the shield has to sit UNDER the content, not beside it.
        Assert.Matches(new Regex(@"root = new BoxEl\s*\{\s*Grow = 1f, Direction = 1, ZStack = true"), pane);
    }

    // ── THE ONE CONTENT LANE ─────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The lane is DERIVED, never typed twice: the pane edge plus a depth-0 row's own indent. If someone
    /// re-tunes <c>IndentFor</c> or the pane pad, the lane moves with them instead of silently disagreeing.</summary>
    [Fact]
    public void ContentLane_IsThePaneEdgePlusTheDepthZeroRowIndent()
    {
        Assert.Equal(SidebarRowGeometry.PaneEdge + SidebarRowGeometry.IndentFor(0), SidebarRowGeometry.ContentLane);
        Assert.Equal(SidebarRowGeometry.PaneEdge + SidebarRowGeometry.RowInsetRight, SidebarRowGeometry.ContentLaneEnd);
        // The landed numbers the screenshots were measured against, pinned so a "harmless" retune is a visible diff.
        Assert.Equal(14f, SidebarRowGeometry.ContentLane);
        Assert.Equal(16f, SidebarRowGeometry.ContentLaneEnd);
    }

    /// <summary>A NESTED row indents from the lane, so a depth-1 child sits exactly one 12-DIP level inside it.</summary>
    [Fact]
    public void NestedRowsIndentFromTheLane()
    {
        Assert.Equal(SidebarRowGeometry.ContentLane + 12f,
                     SidebarRowGeometry.PaneEdge + SidebarRowGeometry.IndentFor(1));
        Assert.Equal(SidebarRowGeometry.IndentFor(4), SidebarRowGeometry.IndentFor(9));   // clamped at 4 levels
    }

    /// <summary>THE RAGGED-LEFT-EDGE GUARD. Every fixed chrome band that is mounted ABOVE the virtualized list is a
    /// SIBLING of the padded list, so <c>SidebarPaneMetrics.PanePad</c> never reaches it — each one used to pad to a
    /// bare literal 8 and land 6 DIP left of every row below it (screenshot 6). These files are engine-bound and are
    /// therefore not source-included here, so the drift guard is a source scan (the same idiom
    /// <see cref="ThePaneMenu_HangsOffAChildlessShieldAndNotTheRoot"/> uses): a band must express its horizontal inset
    /// through the named lane, never through the pre-lane literal it used to type.
    /// <para><paramref name="retiredInset"/> is the exact literal that band carried BEFORE the lane landed; its
    /// reappearance anywhere in the file is the drift this guard exists to catch. The negative is the RETIRED SPELLING
    /// rather than "no literal edges anywhere" on purpose — a literal that is not a band inset (the chip capsule's own
    /// pill padding, a card's internal padding) is deliberate and must stay legal.</para></summary>
    [Theory]
    // the header band AND the toolbar band (both were Padding = (8,0,8,0))
    [InlineData(@"Features\Sidebar\Modes\LibraryV3\LibraryV3Header.cs", "Padding = new Edges4(8f, 0f, 8f, 0f)")]
    // the chrome rule (Margin (8,4,8,4)) and the drill-in breadcrumb (Padding (2,0,8,0))
    [InlineData(@"Features\Sidebar\Modes\LibraryV3\LibraryV3Chrome.cs", "Margin = new Edges4(8f, 4f, 8f, 4f)")]
    [InlineData(@"Features\Sidebar\Modes\LibraryV3\LibraryV3Chrome.cs", "Padding = new Edges4(2f, 0f, 8f, 0f)")]
    // the in-list section header, which sat 2 DIP right of the rows it labels
    [InlineData(@"Features\Sidebar\Shared\SidebarSectionHeader.cs", "Padding = new Edges4(8f, 0f, 8f, 0f)")]
    public void FixedChromeBands_LandOnTheContentLane(string relativePath, string retiredInset)
    {
        string src = ReadAppSource(relativePath);
        if (src is null) return;
        Assert.DoesNotContain(retiredInset, src);
        Assert.Matches(new Regex(@"SidebarPaneMetrics\.(BandInset|RowInset|ContentLane)"), src);
    }

    /// <summary>The chip rail's lane lives INSIDE its horizontal scroller (the chips must scroll out from under it with
    /// AutoEdgeFade rather than be clipped at a padded viewport), so it is pinned positively rather than by a retired
    /// literal — the file legitimately still carries an 8-DIP padding for the capsule pill itself.</summary>
    [Fact]
    public void TheChipRailPadsToTheBandInset()
    {
        string src = ReadAppSource(@"Features\Sidebar\Modes\LibraryV3\LibraryV3Chips.cs");
        if (src is null) return;
        Assert.Contains("Padding = SidebarPaneMetrics.BandInset", src);
    }

    /// <summary>A REORDERABLE BAND'S ROWS MUST STILL FILL THE SLOT. <c>Reorderable.Item</c> wraps its content in a BoxEl
    /// that leaves <c>Direction</c> at its default 0 = ROW, so the row sits on that wrapper's MAIN axis and — with no
    /// <c>Grow</c> — arranges at its own measured CONTENT width. Every unwrapped row fills (the bound slot's component
    /// anchor is a column whose cross axis stretches by default), so without an explicit fill the Pinned / StaticLinks /
    /// CustomGroup rows in all three designs drew visibly narrower hover/selected plates than their neighbours — a short
    /// title painted a stub, a long ellipsised one painted full width.
    /// <para>The engine wrapper is deliberately NOT changed for this (it is shared with TabView); the fix is per call
    /// site. It had been made twice before in this folder — in the customizer's section outline ("FILL THE COLUMN
    /// (round-3 defect 1)") and, as an explicit Width, in the top-bar strip — but Phase 3 deleted both of those
    /// surfaces, so <c>Pane\SidebarPaneSlot.cs</c> is now the pattern's ONLY live owner and BOTH of its wrap sites are
    /// pinned here: the item band, and Phase 2's section-CARD band (whose cards would otherwise draw at their title's
    /// width in the customize canvas).</para></summary>
    [Fact]
    public void ReorderBandRows_FillTheSlot()
    {
        string src = ReadAppSource(@"Features\Sidebar\Pane\SidebarPaneSlot.cs");
        if (src is null) return;
        // The fill must be applied to the content HANDED TO Reorderable.Item, not somewhere else in the file.
        Assert.Matches(new Regex(
            @"Ro\.Item\(\s*index - pair\.Start,\s*content is BoxEl \w+ \? \w+ with \{ Grow = 1f, Shrink = 1f, MinWidth = 0f \}"),
            src);
        // …and the same treatment on the section-card band, which is a SEPARATE wrap site with its own drag kind.
        Assert.Matches(new Regex(
            @"SectionReorder\.Item\(\s*index - cardBand\.Start,\s*content is BoxEl \w+ \? \w+ with \{ Grow = 1f, Shrink = 1f, MinWidth = 0f \}"),
            src);
    }

    // ── THE ONE SELECTION PILL (#22/#23 — two pills lit at once) ─────────────────────────────────────────────────────

    /// <summary>THE PILL'S OPACITY MAY NOT BE AUTHORED. It used to render <c>Opacity = selected ? 1f : 0f</c> — a value
    /// frozen at the render that produced it — while <c>SidebarPane</c>'s moving-pill transaction wrote the SAME node's
    /// opacity channel directly. Every stale-pill report reduces to that split ownership: a force-completed flight
    /// snapping <c>visible: true</c>, or a route→node registration whose slot had since been recycled onto another row,
    /// lit a node whose own state said dark, and nothing re-derived it. The opacity is now ONE bound read of the slot's
    /// live state (<c>SidebarPillState.Opacity</c>, the drop cue's discipline), so it re-evaluates on the row's own
    /// epoch with no re-render. The file is engine-bound and cannot be source-included, so this is a source scan.</summary>
    [Fact]
    public void TheSelectionPill_BindsItsOpacity_AndAuthorsNoLiteral()
    {
        string src = ReadAppSource(@"Features\Sidebar\Shared\SidebarSelectionPill.cs");
        if (src is null) return;
        // Comments are stripped first: the file documents the retired spelling on purpose, and a guard that forbids
        // NAMING the defect would only get the explanation deleted.
        string code = Code(src);
        // The retired spelling, in every form a "harmless" reintroduction would take.
        Assert.DoesNotMatch(new Regex(@"Opacity\s*=\s*[^;,]*\?\s*1f\s*:\s*0f"), code);
        Assert.DoesNotMatch(new Regex(@"Opacity\s*=\s*(selected|state\.Selected|[01](\.0+)?f)\s*[,;]"), code);
        // …and the positive: one mount-stable bound channel, fed by the slot's live probe.
        Assert.Matches(new Regex(@"Opacity\s*=\s*_opacity\s*,"), code);
        Assert.Contains("Prop.Of(() => _state().Opacity)", code);
    }

    /// <summary>THE PANE MAY ONLY ASSERT WHAT THE BOUND READ WOULD ALREADY SAY. A force-complete used to snap the
    /// previous flight's incoming half to a LITERAL <c>visible: true</c> — which is the previously-opened route's node,
    /// and after a recycle whichever row inherited it (the now-playing one, in the reported case). Both halves now
    /// settle at the derived verdict, and the route→node registry is re-validated before the transaction touches a
    /// node at all.</summary>
    [Fact]
    public void TheSelectionTransaction_DerivesVisibility_AndRevalidatesItsRegistry()
    {
        string code = Code(ReadAppSource(@"Features\Sidebar\Pane\SidebarPane.cs"));
        if (code is null) return;
        Assert.DoesNotMatch(new Regex(@"SnapVertical\(anim, _selectionFlight(From|To), visible: (true|false)\)"), code);
        Assert.Equal(2, Regex.Matches(code, @"visible: PillDrawsSelected\(_selectionFlight(From|To)\)").Count);
        // The pill's verdict comes from the ONE rule, never from a second string compare typed here.
        Assert.Contains("SidebarPillState.Lit(route, SelectedRoutePeek)", code);
        // A live node is not enough: the reverse index has to agree the node still draws that route.
        Assert.Contains("_selectionRouteByNode.TryGetValue((int)registration.Node.Raw.Index, out var bound)", code);
    }

    /// <summary>THE PILL IS SELECTION, THE <c>|||</c> GLYPH IS PLAYBACK. A row's <c>selected</c> is resolved by the pane
    /// through <c>SidebarRowResolve</c> and is route-only; the now-playing pair is a separate read. Folding the two
    /// (<c>selected || playing</c>) is the one edit that would make the playing row wear the accent pill, so it is
    /// pinned negatively here — and the probe the bound opacity reads is pinned positively.</summary>
    [Fact]
    public void ARowsSelection_IsRouteOnly_AndNeverFoldsPlayback()
    {
        string code = Code(ReadAppSource(@"Features\Sidebar\Pane\SidebarPaneSlot.cs"));
        if (code is null) return;
        Assert.DoesNotMatch(new Regex(@"bool selected\s*=\s*[^;]*\bplaying\b"), code);
        Assert.DoesNotMatch(new Regex(@"Selected\s*=\s*[^,;]*\bplaying\b"), code);
        // Every selection read in the slot goes through a route-only owner: the pane's per-ROW verdict, or — for a grid
        // CELL, which is not a plan row — the same resolver's per-ENTRY predicate the row-level sweep ORs across.
        foreach (Match m in Regex.Matches(code, @"bool selected\s*=\s*([^;]+);"))
            Assert.Contains(m.Groups[1].Value.Trim(), new[]
            {
                "_o.RowSelectsRoute(index, sel)",
                "SidebarRowResolve.EntrySelects(in entry, sel)",
            });
        // The probe: the slot's snapshot re-derived against the LIVE route and the pane's row verdict, every read.
        Assert.Contains("_pillState.For(selectedRoute, _o.RowSelectsRoute(index, selectedRoute))", code);
    }

    // ── THE DROP CUE READS THE LIVE SLOT INDEX (the two-insertion-lines defect) ──────────────────────────────────────

    /// <summary>EVERY BOUND THUNK ON A PER-ROW CUE MUST READ <c>_scope.Index.Value</c>.
    ///
    /// <para>The reconciler wires a node's bound <c>Prop&lt;T&gt;</c> thunks at MOUNT and never again — Update rewrites
    /// props, not bindings. The insertion line's key is the constant <c>"drop-line"</c> and the Into plate's is
    /// <c>"drop-plate"</c>, so when an <c>ItemsView</c> slot recycles onto another plan row the reconciler pairs those
    /// children by <c>(Key, type)</c> and UPDATES them — while the entity row beside them, keyed by the row's own key,
    /// genuinely remounts. A thunk that captured the plan <c>index</c> therefore kept answering for the row this slot
    /// was FIRST mounted with: after an auto-scrolled drag the user saw TWO insertion lines at once (the armed row's
    /// and a recycled slot's ghost), and the slot holding index 0's stale binding could never draw "before the first
    /// row". The row HEIGHT had the same defect, one step quieter.</para>
    ///
    /// <para>The fix is structural and therefore scannable: every thunk in those two builders re-reads the slot's live
    /// index (which subscribes the binding to the recycle write) and the row epoch beside it (which covers a same-index
    /// re-plan) before it asks the pane anything. The files are engine-bound and cannot be source-included, so this is
    /// a source scan — the same idiom as the pill guards above.</para></summary>
    [Fact]
    public void ThePerRowDropCues_BindAgainstTheLiveSlotIndex()
    {
        string code = Code(ReadAppSource(@"Features\Sidebar\Pane\SidebarPaneSlot.cs"));
        if (code is null) return;

        foreach (string builder in new[] { "Element InsertionLine()", "Element DropPlate()" })
        {
            string body = Balanced(code, builder);
            Assert.NotEqual("", body);
            var thunks = PropOfThunks(body);
            Assert.NotEmpty(thunks);
            foreach (string thunk in thunks)
                Assert.Contains("_scope.Index.Value", thunk, StringComparison.Ordinal);
        }

        // …and the LIVE height, off the same live index: a captured `height` is the same defect one step quieter.
        Assert.Contains("_o.RowExtentOf(i)", code, StringComparison.Ordinal);
        // The retired shapes, in every spelling a "harmless" reintroduction would take.
        Assert.DoesNotMatch(new Regex(@"InsertionLine\(\s*index"), code);
        Assert.DoesNotContain("DropCue = ", code, StringComparison.Ordinal);
    }

    /// <summary>THE ROW NO LONGER OWNS THE "INTO" PLATE. <c>SidebarEntityRow.Create</c> used to bind
    /// <c>Fill</c>/<c>BorderColor</c> off a <c>SidebarRowSpec.DropCue</c> thunk that ALSO folded
    /// <c>enabled &amp;&amp; selected</c> — captured as values at the render that built it. Because bindings are wired
    /// at mount only, a same-keyed re-render (exactly what <c>RefreshSelection</c>'s epoch bump produces) left the old
    /// thunk in place and the resting route plate stayed on the PREVIOUS route until the row scrolled out and
    /// remounted. <c>DropCue</c> is deleted, the row's fills are static values again, and the plate is the slot's own
    /// always-mounted <c>DropPlate()</c> under the row.</summary>
    [Fact]
    public void TheEntityRow_KeepsItsFillsStatic_AndOwnsNoTreeDropCue()
    {
        string code = Code(ReadAppSource(@"Features\Sidebar\Shared\SidebarEntityRow.cs"));
        if (code is null) return;
        Assert.DoesNotContain("DropCue", code, StringComparison.Ordinal);
        Assert.DoesNotContain("Prop.Of(() => dropActive()", code, StringComparison.Ordinal);
        // The resting plate is now ONE precomputed value covering route selection AND the tree multi-selection, so the
        // whole-row `DropActive` cue (the rail folder flyout's rows) composites over it instead of restating it.
        Assert.Contains("Fill = plateOn is null", code, StringComparison.Ordinal);
        Assert.Matches(new Regex(@"ColorF rest = !enabled \? ColorF\.Transparent"), code);
    }

    /// <summary>Every <c>Prop.Of(</c> invocation in <paramref name="body"/>, as its balanced argument span.</summary>
    static List<string> PropOfThunks(string body)
    {
        var found = new List<string>();
        const string token = "Prop.Of(";
        for (int at = body.IndexOf(token, StringComparison.Ordinal); at >= 0;
             at = body.IndexOf(token, at + token.Length, StringComparison.Ordinal))
        {
            int open = at + token.Length - 1;
            int depth = 0;
            for (int i = open; i < body.Length; i++)
            {
                if (body[i] == '(') depth++;
                else if (body[i] == ')' && --depth == 0) { found.Add(body[open..(i + 1)]); break; }
            }
        }
        return found;
    }

    /// <summary>The brace-balanced body of the member whose signature is <paramref name="signature"/> — expression-bodied
    /// members included (the builders here are <c>=&gt; new BoxEl { … }</c>).</summary>
    static string Balanced(string code, string signature)
    {
        int at = code.IndexOf(signature, StringComparison.Ordinal);
        if (at < 0) return "";
        int open = code.IndexOf('{', at + signature.Length);
        if (open < 0) return "";
        int depth = 0;
        for (int i = open; i < code.Length; i++)
        {
            if (code[i] == '{') depth++;
            else if (code[i] == '}' && --depth == 0) return code[open..(i + 1)];
        }
        return "";
    }

    /// <summary>Source with its whole-line comments removed. A drift guard must scan the CODE: the files it guards are
    /// expected to name the retired spelling in prose (that is how the next reader learns why the rule exists), and a
    /// guard that cannot tell the two apart pressures the explanation out of the file.</summary>
    static string Code(string src)
        => src is null ? null! : Regex.Replace(src, @"^[ \t]*//.*$", "", RegexOptions.Multiline);

    /// <summary>Read one app source file, or null (test skipped) on a binary-only run.</summary>
    static string ReadAppSource(string relativePath)
    {
        string root = AppSourceRoot();
        if (root is null) { Assert.Skip("app sources not present (binary-only run)"); return null!; }
        return File.ReadAllText(Path.Combine(root, relativePath.Replace('\\', Path.DirectorySeparatorChar)));
    }

    /// <summary>src/apps/Wavee, located from THIS file's compile-time path (the StageLayoutTests idiom).</summary>
    static string AppSourceRoot([CallerFilePath] string here = "")
    {
        string? tests = Path.GetDirectoryName(here);
        if (tests is null) return null!;
        string app = Path.Combine(Path.GetDirectoryName(tests)!, "Wavee");
        return Directory.Exists(app) ? app : null!;
    }
}
