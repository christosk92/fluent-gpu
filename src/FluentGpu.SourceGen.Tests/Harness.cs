using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace FluentGpu.SourceGen.Tests;

/// <summary>
/// A self-contained Roslyn test driver. Analyzer runs resolve against the loaded runtime assemblies (which include the
/// real FluentGpu.Engine via the project reference), so Element / Prop&lt;T&gt; / Signal / Component / DepKey semantic
/// checks bind. Generator runs use a BCL-only reference set so a test can declare its own trigger types (e.g. a fake
/// TokenSet/Tok) without colliding with the engine's, and asserts on generated source-text fragments.
/// </summary>
internal static class Harness
{
    private static readonly ImmutableArray<MetadataReference> EngineRefs = BuildRefs(includeFluentGpu: true);
    private static readonly ImmutableArray<MetadataReference> BclRefs = BuildRefs(includeFluentGpu: false);

    private static readonly CSharpParseOptions ParseOpts = new(LanguageVersion.Preview);

    private static ImmutableArray<MetadataReference> BuildRefs(bool includeFluentGpu)
    {
        var tpa = ((string)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES")!).Split(Path.PathSeparator);
        var builder = ImmutableArray.CreateBuilder<MetadataReference>();
        foreach (string path in tpa)
        {
            if (path.Length == 0) continue;
            string file = Path.GetFileName(path);
            // Generator golden tests declare their own FluentGpu.* trigger types; keep the engine assemblies out so
            // there is no duplicate-type ambiguity. Analyzer tests keep them in for real semantic resolution.
            if (!includeFluentGpu && file.StartsWith("FluentGpu", StringComparison.OrdinalIgnoreCase)) continue;
            builder.Add(MetadataReference.CreateFromFile(path));
        }
        return builder.ToImmutable();
    }

    private static CSharpCompilation Compile(string source, ImmutableArray<MetadataReference> refs)
        => CSharpCompilation.Create(
            "FgAnalyzerTest_" + Guid.NewGuid().ToString("N"),
            new[] { CSharpSyntaxTree.ParseText(source, ParseOpts) },
            refs,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                nullableContextOptions: NullableContextOptions.Enable,
                allowUnsafe: true));

    /// <summary>Run <paramref name="analyzer"/> over <paramref name="source"/> (resolved against the real engine) and
    /// return only its diagnostics.</summary>
    public static ImmutableArray<Diagnostic> Analyze(DiagnosticAnalyzer analyzer, string source)
    {
        var compilation = Compile(source, EngineRefs);
        var withAnalyzers = compilation.WithAnalyzers(
            ImmutableArray.Create(analyzer),
            new CompilationWithAnalyzersOptions(new AnalyzerOptions(ImmutableArray<AdditionalText>.Empty),
                onAnalyzerException: null, concurrentAnalysis: false, logAnalyzerExecutionTime: false));
        return withAnalyzers.GetAnalyzerDiagnosticsAsync(CancellationToken.None).GetAwaiter().GetResult();
    }

    public static int Count(ImmutableArray<Diagnostic> diagnostics, string id)
        => diagnostics.Count(d => d.Id == id);

    /// <summary>The source text the diagnostic points at (its span) — lets a test assert the flagged SPAN, not just
    /// that the rule fired.</summary>
    public static string SpanText(Diagnostic diagnostic)
    {
        var span = diagnostic.Location.SourceSpan;
        return diagnostic.Location.SourceTree?.GetText().ToString(span) ?? "";
    }

    /// <summary>Run an incremental generator (BCL-only refs; declare trigger types in <paramref name="source"/>) and
    /// return the concatenated generated source plus the generator diagnostics.</summary>
    public static (string Generated, ImmutableArray<Diagnostic> Diagnostics) Generate(
        IIncrementalGenerator generator, string source, params (string Name, string Content)[] additionalTexts)
    {
        var compilation = Compile(source, BclRefs);
        IEnumerable<AdditionalText> additional = additionalTexts.Select(t => (AdditionalText)new InMemoryText(t.Name, t.Content));

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { generator.AsSourceGenerator() },
            additionalTexts: additional,
            parseOptions: ParseOpts,
            optionsProvider: null);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out _, out var diagnostics);
        var result = driver.GetRunResult();
        string generated = string.Join("\n\n// ---- next generated file ----\n\n",
            result.GeneratedTrees.Select(t => t.ToString()));
        return (generated, diagnostics);
    }

    private sealed class InMemoryText : AdditionalText
    {
        private readonly string _content;
        public InMemoryText(string path, string content) { Path = path; _content = content; }
        public override string Path { get; }
        public override SourceText GetText(CancellationToken cancellationToken = default)
            => SourceText.From(_content, System.Text.Encoding.UTF8);
    }
}
