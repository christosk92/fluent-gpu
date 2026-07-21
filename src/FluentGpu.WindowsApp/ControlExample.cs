using FluentGpu.Controls;
using FluentGpu.Dsl;
using FluentGpu.Foundation;
using FluentGpu.GalleryKit;

/// <summary>
/// Thin compat shims (WS7 W7.0). The ~100 gallery pages still call <c>ControlExample.Build</c> and
/// <c>CodeText.Block</c>; the implementations graduated in this phase — <c>ControlExample.Build</c> forwards to
/// <see cref="ExampleCard"/> (GalleryKit; the full rename to <c>ExampleCard</c> at the call sites is G8b), and
/// <c>CodeText.Block</c> forwards to the theme-aware <see cref="CodeBlock"/> control (FluentGpu.Controls).
/// </summary>
static class ControlExample
{
    public static Element Build(string title, Element example, string? description = null,
        Element? options = null, Element? output = null, string? code = null, FlexAlign exampleAlign = FlexAlign.Start)
        => ExampleCard.Build(title, example, description, options, output, code, exampleAlign);
}

static class CodeText
{
    public static Element Block(string code) => CodeBlock.Of(code);
}
