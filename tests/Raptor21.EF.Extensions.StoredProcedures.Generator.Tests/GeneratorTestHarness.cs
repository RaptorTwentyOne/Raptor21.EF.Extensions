using System.Collections.Immutable;
using System.Reflection;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Text;
using Raptor21.EF.Extensions.StoredProcedures.Generator;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator.Tests;

/// <summary>Runs <see cref="StoredProcedureGenerator"/> over a source snippet and exposes the results for assertions.</summary>
internal static class GeneratorTestHarness
{
    private static readonly ImmutableArray<MetadataReference> References = BuildReferences();

    /// <summary>Runs the generator over one source file plus any number of .sql scripts.</summary>
    /// <remarks>
    /// The scripts are a <c>params</c> parameter rather than a second overload on purpose. Every call
    /// site that passes none still binds to this method unchanged, so the tests written before the
    /// generator could read a .sql are also the tests that prove a consumer who never promotes one keeps
    /// exactly today's behaviour - the compiler enforces that, rather than a reviewer remembering it.
    /// </remarks>
    public static RunResult Run(string source, params (string Path, string Text)[] sqlFiles) =>
        RunMany(new[] { source }, sqlFiles);

    /// <summary>Runs the generator over several source files - one compilation, several syntax trees.</summary>
    /// <remarks>
    /// A separate name rather than an overload, so no existing call site's binding can move. The EF
    /// snapshot branch is the reason it exists: the reader takes the model from a <c>ModelSnapshot</c>
    /// class, and whether that class shares a file with the row types it describes is exactly the kind of
    /// arrangement detail a test must be able to vary - a real consumer's snapshot is always its own
    /// file, written by <c>dotnet ef migrations add</c>, and the incrementality claims are only true of
    /// that arrangement.
    /// </remarks>
    public static RunResult RunMany(string[] sources, params (string Path, string Text)[] sqlFiles)
    {
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s)).ToArray();
        var compilation = Compile(trees);

        // Typed GeneratorDriver, not CSharpGeneratorDriver: RunGenerators and ReplaceAdditionalTexts both
        // return the base type, so a var-typed CSharpGeneratorDriver cannot be assigned back (CS0266).
        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new StoredProcedureGenerator().AsSourceGenerator() },
            AdditionalTexts(sqlFiles),
            (CSharpParseOptions)trees[0].Options);

        driver = driver.RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out var generatorDiagnostics);

        var generatedText = string.Join(
            "\n\n",
            driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()));

        var outputErrors = outputCompilation.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .ToImmutableArray();

        return new RunResult(generatedText, generatorDiagnostics, outputErrors);
    }

    /// <summary>Runs the generator, compiles what it emitted, and loads the result so a generated body can be run.</summary>
    /// <remarks>
    /// Every other method here asserts on the emitted TEXT, which pins what the generator writes but says
    /// nothing about what the writing does. Loading the assembly is what turns "the body contains one
    /// <c>await using</c>" into "the body disposes what it leased, after the executor call, and only what
    /// the provider said it owned" - which is the claim the borrowing design actually rests on, and the
    /// one a text assertion cannot reach.
    /// <para>
    /// The assembly goes into the default load context, so its reference to the runtime library binds to
    /// the very assembly this project already has loaded - which is what lets a test implement
    /// <c>ISqlConnectionProvider</c> here and have generated code accept it.
    /// </para>
    /// </remarks>
    public static Assembly RunAndLoad(string source)
    {
        var tree = CSharpSyntaxTree.ParseText(source);
        var compilation = Compile(tree);

        CSharpGeneratorDriver
            .Create(
                new[] { new StoredProcedureGenerator().AsSourceGenerator() },
                AdditionalTexts(),
                (CSharpParseOptions)tree.Options)
            .RunGeneratorsAndUpdateCompilation(compilation, out var outputCompilation, out _);

        using var peStream = new MemoryStream();
        var emitted = outputCompilation.Emit(peStream);

        // Emit does strictly more than GetDiagnostics - it has to write metadata and IL for everything the
        // generator produced - so the failure is reported here in full rather than as a load error later.
        if (!emitted.Success)
            throw new InvalidOperationException(
                "The generated code did not compile:\n" + string.Join(
                    "\n",
                    emitted.Diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).Select(d => $"{d.Id}: {d.GetMessage()}")));

        return Assembly.Load(peStream.ToArray());
    }

    /// <summary>Runs the generator with step tracking on, so a later edit can be asked what re-ran.</summary>
    /// <remarks>
    /// <c>IncrementalGeneratorOutputKind.None</c> disables no output - the generated text is still
    /// produced, which is what lets an incrementality test also assert that a header edit reached the
    /// emitted code. Tracking is off in <see cref="Run"/> because it costs memory per step and no
    /// behavioural test needs it.
    /// </remarks>
    public static TrackedRun RunTracked(string source, params (string Path, string Text)[] sqlFiles) =>
        RunTrackedMany(new[] { source }, sqlFiles);

    /// <summary>The tracked counterpart of <see cref="RunMany"/>, so one file can be replaced alone.</summary>
    /// <remarks>
    /// Replacing one tree of several is the whole point. <c>ForAttributeWithMetadataName</c> re-runs its
    /// transform only for nodes whose tree changed, so a snapshot edit that leaves the row types' file
    /// alone is the only arrangement in which "the snapshot moved and row resolution did not re-run" is
    /// even a meaningful question - in a single-tree compilation every row's model is rebuilt, and with
    /// it the row type's own Location, whatever the reader did.
    /// </remarks>
    public static TrackedRun RunTrackedMany(string[] sources, params (string Path, string Text)[] sqlFiles)
    {
        var trees = sources.Select(s => CSharpSyntaxTree.ParseText(s)).ToArray();
        var compilation = Compile(trees);

        GeneratorDriver driver = CSharpGeneratorDriver.Create(
            new[] { new StoredProcedureGenerator().AsSourceGenerator() },
            AdditionalTexts(sqlFiles),
            (CSharpParseOptions)trees[0].Options,
            optionsProvider: null,
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

        return new TrackedRun(compilation, trees, driver);
    }

    /// <summary>Wraps each (path, text) pair as an <see cref="AdditionalText"/> the driver can consume.</summary>
    internal static ImmutableArray<AdditionalText> AdditionalTexts(params (string Path, string Text)[] sqlFiles) =>
        sqlFiles.Length == 0
            ? ImmutableArray<AdditionalText>.Empty
            : sqlFiles.Select(f => (AdditionalText)new InMemoryAdditionalText(f.Path, f.Text)).ToImmutableArray();

    private static CSharpCompilation Compile(params SyntaxTree[] trees) =>
        CSharpCompilation.Create(
            assemblyName: "GeneratorTestAssembly",
            syntaxTrees: trees,
            references: References,
            options: new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, nullableContextOptions: NullableContextOptions.Enable));

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

    /// <summary>A .sql script held in memory, standing in for a promoted <c>EmbeddedResource</c> item.</summary>
    /// <remarks>
    /// The generator reads its scripts through <c>AdditionalTextsProvider</c> and nothing else - it
    /// cannot open the embedded resource (the assembly does not exist yet) and RS1035 makes
    /// <c>System.IO.File</c> a compile error in the generator project. Supplying the text here is
    /// therefore the whole of what a consumer's build supplies, and a test that passes no script is
    /// testing a real consumer state rather than an artificial one.
    /// </remarks>
    private sealed class InMemoryAdditionalText : AdditionalText
    {
        private readonly SourceText _text;

        public InMemoryAdditionalText(string path, string text)
        {
            Path = path;
            _text = SourceText.From(text, Encoding.UTF8);
        }

        public override string Path { get; }

        public override SourceText GetText(CancellationToken cancellationToken = default) => _text;
    }
}

/// <summary>One generator run whose pipeline steps can be interrogated after an edit.</summary>
/// <remarks>
/// The header-only parser's central claim - that editing a procedure body re-runs nothing - is not
/// observable in the emitted text, because the emitted text is identical either way. Only
/// <c>GeneratorRunResult.TrackedSteps</c> can tell the difference between "recomputed the same answer"
/// and "never recomputed", which is the difference this design exists to buy.
/// </remarks>
internal sealed class TrackedRun
{
    private Compilation _compilation;
    private readonly SyntaxTree[] _trees;
    private GeneratorDriver _driver;

    internal TrackedRun(Compilation compilation, SyntaxTree[] trees, GeneratorDriver driver)
    {
        _compilation = compilation;
        _trees = trees;
        _driver = driver.RunGenerators(compilation);
    }

    /// <summary>Everything the generator produced on the most recent run.</summary>
    public string GeneratedText =>
        string.Join("\n\n", _driver.GetRunResult().GeneratedTrees.Select(t => t.ToString()));

    /// <summary>Replaces the .sql scripts wholesale and re-runs, the way an IDE edit does.</summary>
    public TrackedRun ReplaceSqlFiles(params (string Path, string Text)[] sqlFiles)
    {
        _driver = _driver
            .ReplaceAdditionalTexts(GeneratorTestHarness.AdditionalTexts(sqlFiles))
            .RunGenerators(_compilation);
        return this;
    }

    /// <summary>Replaces the C# source and re-runs, leaving every <see cref="AdditionalText"/> instance alone.</summary>
    /// <remarks>
    /// Leaving the instances alone is the point: an <c>AdditionalText</c>'s identity in the pipeline is
    /// reference equality, and the IDE preserves the instance for a file it did not touch. A .sql branch
    /// that re-ran here would be one that had been joined to something compilation-derived.
    /// </remarks>
    public TrackedRun ReplaceSource(string source) => ReplaceSource(0, source);

    /// <summary>Replaces one source file of several, leaving the other trees' instances alone.</summary>
    /// <remarks>
    /// The other trees keep their identity, which is what a syntax provider's caching is keyed on: a
    /// declaration in an untouched tree is not re-transformed, so a step that re-ran is a step that
    /// really did depend on the file that moved.
    /// </remarks>
    public TrackedRun ReplaceSource(int index, string source)
    {
        var updated = CSharpSyntaxTree.ParseText(source, (CSharpParseOptions)_trees[index].Options);
        _compilation = _compilation.ReplaceSyntaxTree(_trees[index], updated);
        _trees[index] = updated;
        _driver = _driver.RunGenerators(_compilation);
        return this;
    }

    /// <summary>The distinct re-run reasons recorded for one <c>WithTrackingName</c> step.</summary>
    /// <remarks>
    /// Returns <c>(step absent)</c> rather than throwing, so a renamed tracking name fails the assertion
    /// with a readable message instead of a <c>KeyNotFoundException</c>.
    /// </remarks>
    public string Reasons(string trackingName)
    {
        var steps = _driver.GetRunResult().Results[0].TrackedSteps;
        return steps.TryGetValue(trackingName, out var runs)
            ? string.Join(",", runs.SelectMany(r => r.Outputs).Select(o => o.Reason.ToString()).Distinct())
            : "(step absent)";
    }

    /// <summary>Every tracked step and its reasons, for an assertion failure message.</summary>
    public string StepSummary()
    {
        var steps = _driver.GetRunResult().Results[0].TrackedSteps;
        return string.Join("; ", steps.Keys.OrderBy(k => k, StringComparer.Ordinal).Select(k => k + "=" + Reasons(k)));
    }
}

internal sealed record RunResult(
    string GeneratedText,
    ImmutableArray<Diagnostic> GeneratorDiagnostics,
    ImmutableArray<Diagnostic> OutputErrors)
{
    public bool HasGeneratorDiagnostic(string id) => GeneratorDiagnostics.Any(d => d.Id == id);

    public int CountOf(string id) => GeneratorDiagnostics.Count(d => d.Id == id);

    public IEnumerable<Diagnostic> WithId(string id) => GeneratorDiagnostics.Where(d => d.Id == id);

    public string OutputErrorSummary() => OutputErrors.IsDefaultOrEmpty
        ? "(none)"
        : string.Join("\n", OutputErrors.Select(d => $"{d.Id}: {d.GetMessage()}"));

    /// <summary>Every generator diagnostic with its id, file and message - the failure message for a diagnostic assertion.</summary>
    public string DiagnosticSummary() => GeneratorDiagnostics.IsDefaultOrEmpty
        ? "(none)"
        : string.Join(
            "\n",
            GeneratorDiagnostics.Select(d => $"{d.Id} [{d.Severity}] @{d.Location.GetLineSpan().Path}: {d.GetMessage()}"));
}
