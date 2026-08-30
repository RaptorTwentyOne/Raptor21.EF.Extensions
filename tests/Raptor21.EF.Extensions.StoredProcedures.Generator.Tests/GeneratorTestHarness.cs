using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Raptor21.EF.Extensions.StoredProcedures.Generator;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator.Tests;

/// <summary>Runs <see cref="StoredProcedureGenerator"/> over a source snippet and exposes the results for assertions.</summary>
internal static class GeneratorTestHarness
{
    private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    public static RunResult Run(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = CSharpCompilation.Create(
            assemblyName: "GeneratorTestAssembly",
            syntaxTrees: new[] { tree },
            references: References,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

        var driver = CSharpGeneratorDriver.Create(new StoredProcedureGenerator());
        var updated = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);

        var generatedText = string.Join(
            "\n\n",
            updated.GetRunResult().GeneratedTrees.Select(t => t.ToString()));

        var outputErrors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

        return new RunResult(generatedText, generatorDiagnostics, outputErrors);
    }

    private static ImmutableArray<MetadataReference> BuildReferences()
    {
        var tpa = (string?)AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") ?? "";
        var refs = tpa.Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrWhiteSpace(p) && File.Exists(p))
            .Select(p => (MetadataReference)MetadataReference.CreateFromFile(p))
            .ToList();

        // Ensure the runtime library is present even if trimmed from TPA.
        var runtimeLib = typeof(global::Raptor21.EF.Extensions.StoredProcedures.Contracts.IStoredProcedureContract).Assembly.Location;
        if (!refs.Any(r => string.Equals((r as PortableExecutableReference)?.FilePath, runtimeLib, StringComparison.OrdinalIgnoreCase)))
            refs.Add(MetadataReference.CreateFromFile(runtimeLib));

        return refs.ToImmutableArray();
    }
}

internal sealed record RunResult(
    string GeneratedText,
    ImmutableArray<Diagnostic> GeneratorDiagnostics,
    ImmutableArray<Diagnostic> OutputErrors)
{
    public bool HasGeneratorDiagnostic(string id) => GeneratorDiagnostics.Any(d => d.Id == id);

    public string OutputErrorSummary() => OutputErrors.IsDefaultOrEmpty
        ? "(none)"
        : string.Join("\n", OutputErrors.Select(d => $"{d.Id}: {d.GetMessage()}"));
}
