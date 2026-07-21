using System;
using FluentGpu.Dsl;

namespace FluentGpu.GalleryKit;

/// <summary>
/// How a gallery page is captured by the shot sweep (<c>scripts/gallery-shot-sweep.ps1</c>): <see cref="Deterministic"/>
/// pages render to a stable pixel image after a few settle frames; <see cref="Animated"/> pages need a fixed
/// <c>--frames</c> count (the closed-form spring is dt-deterministic, so a fixed frame count is reproducible);
/// <see cref="Skip"/> pages are excluded from the sweep (live media, external state).
/// </summary>
public enum ShotMode : byte { Deterministic, Animated, Skip }

/// <summary>
/// Tags a <see cref="FluentGpu.Hooks.Component"/> subclass as a gallery page. The <c>GalleryRegistryGenerator</c>
/// collects every <c>[GalleryPage]</c>-tagged component in the assembly and emits <c>GalleryRegistry.Pages</c>
/// (a <see cref="GalleryPageInfo"/> table) + <c>GalleryRegistry.Create(key)</c> — compile-time, zero reflection.
/// The compile IS the sync check: a duplicate <see cref="Key"/> is a compile error (FGG010). It is the richer
/// showcase-metadata sibling of <c>FluentGpu.Controls.RouteAttribute</c> — the shell derives its nav tree, search
/// index, and All-controls grid from the emitted table.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false, Inherited = false)]
public sealed class GalleryPageAttribute(string key, string title, string category) : Attribute
{
    /// <summary>The stable page key (the nav/route key; also the shot id and deep-link arg).</summary>
    public string Key { get; } = key;
    /// <summary>The display title shown in the nav tree, search, and page header.</summary>
    public string Title { get; } = title;
    /// <summary>The section/category this page groups under (mapped to an IA section by the shell's GallerySections table).</summary>
    public string Category { get; } = category;
    /// <summary>Optional nav-tree glyph (a Segoe Fluent Icons codepoint, e.g. <c>Icons.Grid</c>).</summary>
    public string Icon { get; set; } = "";
    /// <summary>Extra search aliases beyond the title (the AutoSuggestBox / command palette matches these too).</summary>
    public string[] Keywords { get; set; } = [];
    /// <summary>Sort order within the category (lower first; ties break on title).</summary>
    public int Order { get; set; } = 1000;
    /// <summary>How the shot sweep captures this page (see <see cref="GalleryKit.ShotMode"/>).</summary>
    public ShotMode ShotMode { get; set; } = ShotMode.Deterministic;
    /// <summary>Hidden pages resolve/deep-link and construct in the audit, but do not appear in the nav tree or search.</summary>
    public bool Hidden { get; set; }
}

/// <summary>
/// One entry in the generated <c>GalleryRegistry.Pages</c> table: a page's <see cref="GalleryPageAttribute"/> metadata
/// plus the <see cref="Create"/> factory that mounts it. The shell derives everything (nav tree, search, All grid,
/// shot sweep) from this table — the one source of truth, replacing the old hand-synced nav-item / catalog / switch trio.
/// </summary>
public sealed record GalleryPageInfo(string Key, string Title, string Category, Func<Element> Create)
{
    public string Icon { get; init; } = "";
    public string[] Keywords { get; init; } = [];
    public int Order { get; init; } = 1000;
    public ShotMode ShotMode { get; init; } = ShotMode.Deterministic;
    public bool Hidden { get; init; }
}

/// <summary>
/// A never-drift code sample: the displayed <see cref="Code"/> is the METHOD BODY TEXT of the <c>[Sample]</c>-attributed
/// factory, extracted verbatim at compile time by the <c>SampleExtractorGenerator</c> — so the shown code is the compiled
/// code by construction. <see cref="Factory"/> mounts the live example (receiving the card's <see cref="Knobs"/>).
/// </summary>
public sealed record Sample(string Title, string? Description, string Code, Func<Knobs, Element> Factory);

/// <summary>
/// Marks a <c>static partial</c>-class method — either <c>(Knobs k) =&gt; Element</c> or <c>() =&gt; Element</c> — as a
/// gallery sample. The <c>SampleExtractorGenerator</c> emits a <c>static readonly Sample {Method}Sample</c> constant into
/// the same partial type, whose <see cref="Sample.Code"/> is the method's verbatim dedented body and whose
/// <see cref="Sample.Factory"/> is the method itself (a <c>()</c> method is wrapped as <c>(Knobs _) =&gt; M()</c>).
/// Diagnostics enforce the contract: FGG001 (not static), FGG002 (wrong return/params), FGG003 (non-partial container).
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public sealed class SampleAttribute(string title) : Attribute
{
    /// <summary>The sample's display title (the ExampleCard heading).</summary>
    public string Title { get; } = title;
    /// <summary>Optional one-line description shown under the title.</summary>
    public string? Description { get; set; }
}
