using Raptor21.EF.Extensions.StoredProcedures.Generator;
using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator.Tests;

/// <summary>
/// Pins the caching behaviour the header-only parser exists to buy.
/// </summary>
/// <remarks>
/// <para>
/// The parser stops at the body's <c>AS</c> and never looks below it. That is not a simplification, it
/// is the design: a procedure body is where a developer types, and a parser that read it would hand
/// Roslyn a different model on every keystroke, re-running the index, every method's resolution and
/// finally the emit of every group file in the assembly. None of that is visible in the generated text,
/// which is identical either way, so <c>GeneratorRunResult.TrackedSteps</c> is the only thing that can
/// tell "recomputed the same answer" from "never recomputed".
/// </para>
/// <para>
/// This pays in the IDE only. A command-line build gets no incrementality at all - Roslyn persists
/// nothing between <c>dotnet build</c> runs - which is the other half of the same constraint: every .sql
/// in the project is parsed from scratch on every build, so the per-file parse has to stay cheap.
/// </para>
/// </remarks>
public class GeneratorIncrementalityTests
{
    private const string Source = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Raptor21.EF.Extensions.StoredProcedures.Generated;

        namespace Demo;

        [StoredProcedureGroup]
        public partial class CatalogProcedures
        {
            [StoredProcedure("dbo.Product_Upsert")]
            public partial Task<(int ReturnValue, int Id)> UpsertAsync(
                string sku, string name, decimal price, int id,
                CancellationToken ct = default);
        }
        """;

    private const string Path = "/x/DbScripts/Product_Upsert.sql";

    private static string Sql(string body, string skuLength = "32") => $$"""
        CREATE OR ALTER PROCEDURE dbo.Product_Upsert
            @Sku   varchar({{skuLength}}),
            @Name  varchar(128),
            @Price decimal(18,2),
            @Id    int OUTPUT
        AS
        BEGIN
            {{body}}
        END
        """;

    [Fact]
    public void EditingAProcedureBodyDoesNotReRunTheHeaderIndexOrResolution()
    {
        var run = GeneratorTestHarness.RunTracked(Source, (Path, Sql("SET NOCOUNT ON; RETURN 0;")));

        run.ReplaceSqlFiles((Path, Sql("SET NOCOUNT ON; UPDATE dbo.Product SET Sku = @Sku; RETURN 1;")));

        // The file is a new instance, so the parse itself re-runs - but it stops at AS, so the model it
        // produces compares equal and the pipeline stops dead right there.
        Assert.Equal("Unchanged", run.Reasons(StoredProcedureGenerator.TrackingNames.SqlHeaders));
        Assert.Equal("Cached", run.Reasons(StoredProcedureGenerator.TrackingNames.SqlHeaderIndex));
        Assert.Equal("Cached", run.Reasons(StoredProcedureGenerator.TrackingNames.ResolvedMethods));
    }

    [Fact]
    public void EditingAProcedureHeaderReRunsResolution()
    {
        // The negative control for the test above. Without it, a parser that returned a constant - or one
        // that silently stopped reading lengths - would pass every caching assertion in this file.
        var run = GeneratorTestHarness.RunTracked(Source, (Path, Sql("SET NOCOUNT ON; RETURN 0;")));
        Assert.Contains("SqlTypeSpec(\"varchar\", 32)", run.GeneratedText);

        run.ReplaceSqlFiles((Path, Sql("SET NOCOUNT ON; RETURN 0;", skuLength: "64")));

        Assert.Equal("Modified", run.Reasons(StoredProcedureGenerator.TrackingNames.SqlHeaders));
        Assert.NotEqual("Cached", run.Reasons(StoredProcedureGenerator.TrackingNames.SqlHeaderIndex));
        Assert.NotEqual("Cached", run.Reasons(StoredProcedureGenerator.TrackingNames.ResolvedMethods));

        // And it reached the emitted code, which is what makes the assertions above mean something.
        Assert.Contains("SqlTypeSpec(\"varchar\", 64)", run.GeneratedText);
    }

    [Fact]
    public void EditingAnUnrelatedCsFileDoesNotReRunTheSqlBranch()
    {
        // An AdditionalText's identity in the pipeline is reference equality, and the IDE preserves the
        // instance for a file it did not touch - so this holds only while the .sql branch is never
        // combined with CompilationProvider or anything else carrying a Compilation, ISymbol, SourceText
        // or Location. Roslyn's own design document names texts.Combine(compilation) as the "Don't do
        // this!" example, and this test is what stops someone doing it.
        var run = GeneratorTestHarness.RunTracked(Source, (Path, Sql("SET NOCOUNT ON; RETURN 0;")));

        run.ReplaceSource(Source + "\n\npublic class Unrelated { public int N => 1; }");

        Assert.Equal("Cached", run.Reasons(StoredProcedureGenerator.TrackingNames.SqlHeaders));
        Assert.Equal("Cached", run.Reasons(StoredProcedureGenerator.TrackingNames.SqlHeaderIndex));
    }

    // ---------------------------------------------------------------- the EF snapshot branch

    // The same claim one branch over, and it matters more here than on the .sql side: this library writes
    // every one of its procedure scripts into the snapshot as an "Sp:" annotation, so a reader that did
    // not skip HasAnnotation would rebuild the model - and re-resolve every entity-bound row - on every
    // procedure edit. That is the exact analogue of the .sql parser stopping at AS.
    //
    // Three trees, because the arrangement is the claim. A real consumer's snapshot is its own file,
    // written by 'dotnet ef migrations add', and the row types are somewhere else entirely; in a
    // single-tree compilation every row's model is rebuilt on any edit, along with the row type's own
    // Location, whatever the reader did.

    private const int RowTree = 0;
    private const int SnapshotTree = 1;
    private const int UnrelatedTree = 2;

    /// <summary>The EF vocabulary, the entity class and the row that binds to it. Never replaced.</summary>
    private static readonly string ModelAndRows = EfSnapshotShim.Compose(
        EfSnapshotShim.Types("""
                public class Sample
                {
                    public int Small { get; set; }
                    public long Big { get; set; }
                }
            """),
        EfSnapshotShim.Rows(EfSnapshotShim.BoundRow("SampleRow", "Sample")));

    /// <summary>One snapshot file: the model-level annotations, then one entity block.</summary>
    private static string SnapshotFile(string annotations, string propertyName, string clrType, string columnType) =>
        EfSnapshotShim.ComposeAdditional(EfSnapshotShim.Snapshot("ProbeSnapshot",
            annotations + EfSnapshotShim.Block("Probe.Sample", $"""
                            b.Property<{clrType}>("{propertyName}").HasColumnType("{columnType}");

                            b.HasKey("{propertyName}");

                            b.ToTable("Sample", (string)null);
                """)));

    private const string VersionAnnotationOnly = """
                    modelBuilder.HasAnnotation("ProductVersion", "10.0.9");

        """;

    private const string VersionAndProcedureAnnotations = """
                    modelBuilder
                        .HasAnnotation("ProductVersion", "10.0.9")
                        .HasAnnotation("Sp:dbo.Sample_List", "CREATE OR ALTER PROCEDURE dbo.Sample_List AS BEGIN SET NOCOUNT ON; SELECT Big FROM dbo.Sample; END");

        """;

    private static TrackedRun RunWithSnapshot(string snapshot) =>
        GeneratorTestHarness.RunTrackedMany(new[] { ModelAndRows, snapshot, "public class Unrelated { public int N => 1; }" });

    [Fact]
    public void EditingAnUnrelatedCsFileDoesNotReRunTheModelIndexOrRowResolution()
    {
        var run = RunWithSnapshot(SnapshotFile(VersionAnnotationOnly, "Big", "long", "bigint"));
        Assert.Contains("record.GetInt64(record.GetOrdinal(\"Big\"))", run.GeneratedText);

        run.ReplaceSource(UnrelatedTree, "public class Unrelated { public int N => 2; }");

        Assert.Equal("Cached", run.Reasons(StoredProcedureGenerator.TrackingNames.EfSnapshots));
        Assert.Equal("Cached", run.Reasons(StoredProcedureGenerator.TrackingNames.EfModelIndex));
        Assert.Equal("Cached", run.Reasons(StoredProcedureGenerator.TrackingNames.ResolvedRows));
    }

    [Fact]
    public void AddingAProcedureAnnotationToTheSnapshotDoesNotReRunTheModelIndexOrRowResolution()
    {
        var run = RunWithSnapshot(SnapshotFile(VersionAnnotationOnly, "Big", "long", "bigint"));

        run.ReplaceSource(SnapshotTree, SnapshotFile(VersionAndProcedureAnnotations, "Big", "long", "bigint"));

        // The file is a new tree, so the reader runs again - but it skips HasAnnotation, so the EfModelFile
        // it produces compares equal and the pipeline stops dead right there. Nothing downstream of the
        // index sees a change, which is the whole reason the model carries no Location.
        //
        // "Cached" rather than the .sql branch's "Unchanged", and the difference is where the tracking
        // name sits rather than a difference in behaviour: SqlHeaders names the Select that produces the
        // model, so an equal model shows up on that step itself, while EfSnapshots names a Select
        // downstream of ForAttributeWithMetadataName's own transform - the equal-value comparison happens
        // one step earlier and this step is simply never asked to run. The negative control below shows
        // the same step reporting Modified when the model really moves, which is what stops this
        // assertion from being satisfied by a reader that had stopped reading anything at all.
        Assert.Equal("Cached", run.Reasons(StoredProcedureGenerator.TrackingNames.EfSnapshots));
        Assert.Equal("Cached", run.Reasons(StoredProcedureGenerator.TrackingNames.EfModelIndex));
        Assert.Equal("Cached", run.Reasons(StoredProcedureGenerator.TrackingNames.ResolvedRows));
    }

    [Fact]
    public void ChangingAMappedColumnTypeInTheSnapshotReRunsResolutionAndReachesTheEmittedCode()
    {
        // The negative control. Without it, a reader that returned a constant - or one that quietly
        // stopped reading Property<T> - would pass every caching assertion above.
        //
        // The entity class carries both members and the model maps one of them, because the row is a
        // projection of the model's COLUMNS: moving the mapping from one member to the other is a real
        // model change that needs no second edit to the class, and an edit to the class's own tree would
        // confound what is being measured.
        var run = RunWithSnapshot(SnapshotFile(VersionAnnotationOnly, "Big", "long", "bigint"));
        Assert.Contains("record.GetInt64(record.GetOrdinal(\"Big\"))", run.GeneratedText);

        run.ReplaceSource(SnapshotTree, SnapshotFile(VersionAnnotationOnly, "Small", "int", "int"));

        Assert.Equal("Modified", run.Reasons(StoredProcedureGenerator.TrackingNames.EfSnapshots));
        Assert.NotEqual("Cached", run.Reasons(StoredProcedureGenerator.TrackingNames.EfModelIndex));
        Assert.NotEqual("Cached", run.Reasons(StoredProcedureGenerator.TrackingNames.ResolvedRows));

        // And it reached the emitted code, which is what makes the assertions above mean something.
        Assert.Contains("record.GetInt32(record.GetOrdinal(\"Small\"))", run.GeneratedText);
        Assert.DoesNotContain("GetInt64", run.GeneratedText);
    }
}
