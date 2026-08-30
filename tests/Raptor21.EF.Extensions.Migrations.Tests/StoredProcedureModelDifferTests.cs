using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Xunit;

namespace Raptor21.EF.Extensions.Migrations.Tests;

/// <summary>
/// Pins the agreement between the differ's two entry points. EF asks <c>GetDifferences</c> what a
/// migration should contain and <c>HasDifferences</c> whether one is needed, and it reaches those two
/// answers down separate paths. The second used to ignore procedures entirely, so
/// <c>dotnet ef migrations has-pending-model-changes</c> reported a clean model while the very next
/// <c>migrations add</c> would have written a migration.
/// </summary>
public class StoredProcedureModelDifferTests
{
    private const string ProcedureName = "dbo.A";
    private const string Body = "CREATE OR ALTER PROCEDURE dbo.A AS\nSELECT 1\n";

    [Fact]
    public void EditedBody_IsADifference() => AssertAgree<Original, EditedBody>(expectDifference: true);

    [Fact]
    public void AddedProcedure_IsADifference() => AssertAgree<NoProcedures, Original>(expectDifference: true);

    [Fact]
    public void RemovedProcedure_IsADifference() => AssertAgree<Original, NoProcedures>(expectDifference: true);

    [Fact]
    public void ReIndentedBody_IsADifference()
    {
        // ScriptEquals normalises line endings and trims the ends of the script, nothing else, so moving a
        // line of the body is a real change. This case is here because it is where a hand-written "cheap"
        // comparison inside HasDifferences would first disagree with the differ, and a disagreement is the
        // failure this whole file exists to prevent.
        AssertAgree<Original, ReIndentedBody>(expectDifference: true);
    }

    [Fact]
    public void LineEndingOnlyEdit_IsNotADifference()
    {
        // The other side of the same rule: a file that changed only from LF to CRLF must not scaffold a
        // migration, and must not fail a build through has-pending-model-changes either.
        AssertAgree<Original, LineEndingsOnly>(expectDifference: false);
    }

    [Fact]
    public void IdenticalModels_AreNotADifference() => AssertAgree<Original, OriginalTwin>(expectDifference: false);

    [Fact]
    public void NoProceduresOnEitherSide_IsNotADifference() => AssertAgree<NoProcedures, NoProceduresTwin>(expectDifference: false);

    [Fact]
    public void TableOnlyChange_IsStillADifference()
    {
        // The procedure verdict is ORed onto EF's, never substituted for it. Returning only the procedure
        // answer would make a table-only change invisible, which is a strictly worse bug than the one being
        // fixed.
        AssertAgree<Original, ExtraTable>(expectDifference: true);
    }

    [Fact]
    public void NullSource_IsAProjectWithNoSnapshotYet()
    {
        // Not a defensive test of an impossible case: Migrator.HasPendingModelChanges reads
        // ModelSnapshot?.Model, so the source really is null in every repository before its first
        // migration - the one moment when a wrong answer is least likely to be noticed.
        using var target = new Original();

        var differ = Differ(target);
        var targetModel = RelationalModel(target);

        Assert.True(differ.HasDifferences(null, targetModel));
        Assert.Equal(differ.GetDifferences(null, targetModel).Count > 0, differ.HasDifferences(null, targetModel));
    }

    [Fact]
    public void ProcedureScripts_LiveOnTheModelAndNotOnTheRelationalModel()
    {
        using var context = new Original();
        var relational = RelationalModel(context);

        // The mechanism behind the override, stated as a test. Both entry points compare IRelationalModel,
        // whose annotation bag is filled only by the provider's IRelationalAnnotationProvider, while
        // RegisterStoredProcedures writes onto IModel - so EF's own differ cannot see a procedure edit
        // however it is called, and hopping to .Model is the only way to reach the scripts. Should a future
        // EF start surfacing model annotations here, this fails and asks for a re-read; the override stays
        // correct either way, because it can only ever OR a difference in and never mask one.
        Assert.DoesNotContain(
            relational.GetAnnotations(),
            a => a.Name.StartsWith(StoredProcedureScript.AnnotationPrefix, StringComparison.Ordinal));
        Assert.Contains(
            relational.Model.GetAnnotations(),
            a => a.Name == StoredProcedureScript.AnnotationPrefix + ProcedureName);
    }

    private static void AssertAgree<TSource, TTarget>(bool expectDifference)
        where TSource : DbContext, new()
        where TTarget : DbContext, new()
    {
        using var source = new TSource();
        using var target = new TTarget();

        var differ = Differ(source);
        var sourceModel = RelationalModel(source);
        var targetModel = RelationalModel(target);

        var operations = differ.GetDifferences(sourceModel, targetModel).Count;
        var has = differ.HasDifferences(sourceModel, targetModel);

        Assert.Equal(expectDifference, operations > 0);

        // The assertion that matters, and the one that fails without the override. Pinning HasDifferences
        // to a hard-coded boolean per scenario would catch today's defect and nothing after it; asserting
        // that it answers exactly what GetDifferences produces is the property that actually broke, and it
        // cannot rot as the diff rule evolves.
        Assert.Equal(operations > 0, has);
    }

    private static IMigrationsModelDiffer Differ(DbContext context)
    {
        var differ = context.GetService<IMigrationsModelDiffer>();

        // Without this, a registration that quietly stopped taking effect would leave every assertion above
        // passing against EF's own differ, which agrees with itself about everything.
        return Assert.IsType<StoredProcedureModelDiffer>(differ);
    }

    private static IRelationalModel RelationalModel(DbContext context)
        => context.GetService<IDesignTimeModel>().Model.GetRelationalModel();

    // EF caches the built model per DbContext TYPE - the default IModelCacheKeyFactory keys on the
    // context's runtime type - so two instances of one context configured with different scripts hand back
    // the SAME IModel, and every assertion here would compare a model with itself and pass while proving
    // nothing. That is the only reason each variant below is its own subclass. Do not collapse them into
    // one context parameterised by a constructor argument.
    private abstract class ModelVariant : DbContext
    {
        protected sealed override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                // Building a model and diffing two models are pure metadata work, so nothing here opens a
                // connection. The host is named the way it is so that a change which starts contacting one
                // fails loudly instead of quietly reaching whatever a developer has running locally.
                .UseSqlServer("Server=host-that-must-never-be-contacted;Database=none")
                .ReplaceService<IMigrationsModelDiffer, StoredProcedureModelDiffer>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
            => modelBuilder.Entity<Widget>();

        // Exactly what RegisterStoredProcedures does with the contents of a .sql file, minus the embedded
        // resource: this annotation is the input the differ actually reads.
        protected static void Script(ModelBuilder modelBuilder, string sql)
            => modelBuilder.Model.SetAnnotation(StoredProcedureScript.AnnotationPrefix + ProcedureName, sql);
    }

    private sealed class NoProcedures : ModelVariant
    {
    }

    private sealed class NoProceduresTwin : ModelVariant
    {
    }

    private sealed class Original : ModelVariant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            Script(modelBuilder, Body);
        }
    }

    private sealed class OriginalTwin : ModelVariant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            Script(modelBuilder, Body);
        }
    }

    private sealed class EditedBody : ModelVariant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            Script(modelBuilder, "CREATE OR ALTER PROCEDURE dbo.A AS\nSELECT 2\n");
        }
    }

    private sealed class LineEndingsOnly : ModelVariant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            Script(modelBuilder, "CREATE OR ALTER PROCEDURE dbo.A AS\r\nSELECT 1");
        }
    }

    private sealed class ReIndentedBody : ModelVariant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            Script(modelBuilder, "CREATE OR ALTER PROCEDURE dbo.A AS\n    SELECT 1\n");
        }
    }

    private sealed class ExtraTable : ModelVariant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Gadget>();
            Script(modelBuilder, Body);
        }
    }

    private sealed class Widget
    {
        public int Id { get; set; }

        public string? Name { get; set; }
    }

    private sealed class Gadget
    {
        public int Id { get; set; }
    }
}
