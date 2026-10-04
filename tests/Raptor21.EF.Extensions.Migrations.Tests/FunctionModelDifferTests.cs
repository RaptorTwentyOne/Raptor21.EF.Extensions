using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Raptor21.EF.Extensions.Migrations.Tests;

/// <summary>
/// Drives functions through <see cref="CodeFirstDatabaseObjectsModelDiffer"/>: where their operations land
/// among EF's and the procedures', and the agreement between the two entry points for a function-only edit.
/// </summary>
public class FunctionModelDifferTests
{
    private const string FunctionName = "dbo.CountWidgets";
    private const string Function = "CREATE OR ALTER FUNCTION dbo.CountWidgets() RETURNS int AS\nBEGIN RETURN (SELECT COUNT(*) FROM dbo.Widget) END\n";
    private const string EditedFunction = "CREATE OR ALTER FUNCTION dbo.CountWidgets() RETURNS int AS\nBEGIN RETURN (SELECT COUNT(1) FROM dbo.Widget) END\n";
    private const string ProcedureName = "dbo.ReadWidgets";
    private const string Procedure = "CREATE OR ALTER PROCEDURE dbo.ReadWidgets AS\nSELECT dbo.CountWidgets()\n";

    [Fact]
    public void NewModel_TablesThenFunctionsThenProcedures()
    {
        var operations = Diff<Empty, WithFunctionAndProcedure>();

        var table = Assert.Single(operations.OfType<CreateTableOperation>());
        var function = Assert.Single(operations, o => Sql(o).Contains("CREATE OR ALTER FUNCTION", StringComparison.Ordinal));
        var procedure = Assert.Single(operations, o => Sql(o).Contains("CREATE OR ALTER PROCEDURE", StringComparison.Ordinal));

        Assert.True(operations.IndexOf(table) < operations.IndexOf(function));
        Assert.True(operations.IndexOf(function) < operations.IndexOf(procedure));
        Assert.StartsWith("EXEC(N'CREATE OR ALTER FUNCTION", Sql(function), StringComparison.Ordinal);
    }

    [Fact]
    public void RemovedModel_ProceduresThenFunctionsThenTables()
    {
        var operations = Diff<WithFunctionAndProcedure, Empty>();

        var procedureDrop = Assert.Single(operations, o => Sql(o) == "DROP PROCEDURE IF EXISTS [dbo].[ReadWidgets];");
        var functionDrop = Assert.Single(operations, o => Sql(o) == "DROP FUNCTION IF EXISTS [dbo].[CountWidgets];");

        // EF's own DropTable is the first operation; the package's drops follow its operations.
        Assert.IsType<DropTableOperation>(operations[0]);
        Assert.True(operations.IndexOf(procedureDrop) < operations.IndexOf(functionDrop));
    }

    [Fact]
    public void AKindChange_IsOneRecreateStatement()
    {
        var operation = Assert.Single(Diff<WithFunctionAndProcedure, WithInlineFunction>());

        Assert.StartsWith("DROP FUNCTION IF EXISTS [dbo].[CountWidgets];\nEXEC(N'CREATE OR ALTER FUNCTION", Sql(operation), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(typeof(WithFunctionAndProcedure), typeof(WithFunctionAndProcedureTwin), false)]
    [InlineData(typeof(WithFunctionAndProcedure), typeof(WithEditedFunction), true)]
    [InlineData(typeof(WithEditedFunction), typeof(WithFunctionAndProcedure), true)]
    [InlineData(typeof(WithFunctionAndProcedure), typeof(WithFunctionPreamble), true)]
    [InlineData(typeof(Empty), typeof(OnlyTable), true)]
    [InlineData(typeof(OnlyTable), typeof(WithFunctionOnly), true)]
    [InlineData(typeof(WithFunctionOnly), typeof(OnlyTable), true)]
    public void HasDifferences_AnswersExactlyWhatGetDifferencesProduces(Type sourceType, Type targetType, bool expectDifference)
    {
        using var source = (DbContext)Activator.CreateInstance(sourceType)!;
        using var target = (DbContext)Activator.CreateInstance(targetType)!;

        var differ = Differ(source);
        var sourceModel = RelationalModel(source);
        var targetModel = RelationalModel(target);

        var operations = differ.GetDifferences(sourceModel, targetModel).Count;

        Assert.Equal(expectDifference, operations > 0);
        Assert.Equal(operations > 0, differ.HasDifferences(sourceModel, targetModel));
    }

    [Fact]
    public void AFunctionOnlyEdit_IsExactlyOneOperation()
    {
        // Only the function moved, so only the function travels - not the table, not the procedure.
        var operation = Assert.Single(Diff<WithFunctionAndProcedure, WithEditedFunction>());

        Assert.Contains("COUNT(1)", Sql(operation), StringComparison.Ordinal);
    }

    [Fact]
    public void FunctionScripts_LiveOnTheModelAndNotOnTheRelationalModel()
    {
        using var context = new WithFunctionAndProcedure();
        var relational = RelationalModel(context);

        // The same mechanism the procedures rely on: EF's own differ compares IRelationalModel, whose annotations
        // come from the provider, so a function edit is invisible to it and the override is what reports it.
        Assert.DoesNotContain(relational.GetAnnotations(), a => a.Name.StartsWith(FunctionScript.AnnotationPrefix, StringComparison.Ordinal));
        Assert.Contains(relational.Model.GetAnnotations(), a => a.Name == FunctionScript.AnnotationPrefix + FunctionName);
    }

    private static List<MigrationOperation> Diff<TSource, TTarget>()
        where TSource : DbContext, new()
        where TTarget : DbContext, new()
    {
        using var source = new TSource();
        using var target = new TTarget();

        return Differ(target).GetDifferences(RelationalModel(source), RelationalModel(target)).ToList();
    }

    private static string Sql(MigrationOperation operation) => (operation as SqlOperation)?.Sql ?? string.Empty;

    private static IMigrationsModelDiffer Differ(DbContext context)
        => Assert.IsAssignableFrom<CodeFirstDatabaseObjectsModelDiffer>(context.GetService<IMigrationsModelDiffer>());

    private static IRelationalModel RelationalModel(DbContext context)
        => context.GetService<IDesignTimeModel>().Model.GetRelationalModel();

    // EF caches the built model per DbContext TYPE, so every variant is its own subclass; see the note in
    // StoredProcedureModelDifferTests. Do not collapse them into one context parameterised by a constructor.
    public abstract class Variant : DbContext
    {
        protected sealed override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseSqlServer("Server=host-that-must-never-be-contacted;Database=none")
                .UseCodeFirstDatabaseObjects();

        protected static void Table(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema("dbo");
            modelBuilder.Entity<Widget>();
        }

        protected static void Fn(ModelBuilder modelBuilder, string sql = Function)
            => modelBuilder.Model.SetAnnotation(FunctionScript.AnnotationPrefix + FunctionName, sql);

        protected static void Sp(ModelBuilder modelBuilder)
            => modelBuilder.Model.SetAnnotation(StoredProcedureScript.AnnotationPrefix + ProcedureName, Procedure);
    }

    public sealed class Empty : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => modelBuilder.HasDefaultSchema("dbo");
    }

    public sealed class OnlyTable : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder) => Table(modelBuilder);
    }

    public sealed class WithFunctionOnly : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            Table(modelBuilder);
            Fn(modelBuilder);
        }
    }

    public sealed class WithFunctionAndProcedure : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            Table(modelBuilder);
            Fn(modelBuilder);
            Sp(modelBuilder);
        }
    }

    public sealed class WithFunctionAndProcedureTwin : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            Table(modelBuilder);
            Fn(modelBuilder);
            Sp(modelBuilder);
        }
    }

    public sealed class WithEditedFunction : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            Table(modelBuilder);
            Fn(modelBuilder, EditedFunction);
            Sp(modelBuilder);
        }
    }

    public sealed class WithFunctionPreamble : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            Table(modelBuilder);
            Fn(modelBuilder, "SET QUOTED_IDENTIFIER OFF\n" + Function);
            Sp(modelBuilder);
        }
    }

    public sealed class WithInlineFunction : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            Table(modelBuilder);
            Fn(modelBuilder, "CREATE OR ALTER FUNCTION dbo.CountWidgets() RETURNS TABLE AS\nRETURN SELECT COUNT(*) AS n FROM dbo.Widget\n");
            Sp(modelBuilder);
        }
    }

    public sealed class Widget
    {
        public int Id { get; set; }
    }
}
