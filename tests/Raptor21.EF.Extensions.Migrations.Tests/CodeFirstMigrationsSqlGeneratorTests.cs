using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Raptor21.EF.Extensions.Migrations.Tests;

/// <summary>
/// Covers <see cref="CodeFirstDatabaseObjectsMigrationsSqlGenerator"/>: the <c>ON [scheme]([column])</c>
/// clause, the fact that everything else is the provider's own output byte for byte, and the two
/// combinations it refuses. All of it runs through <see cref="IMigrationsSqlGenerator.Generate"/> on EF's real
/// SQL Server provider, which never opens a connection.
/// </summary>
public class CodeFirstMigrationsSqlGeneratorTests
{
    private const string MigrationId = "20260101000000_PlacedTable";

    [Fact]
    public void PlacedTable_GetsTheOnClauseBetweenTheColumnListAndTheTerminator()
    {
        var text = Generate<CodeFirstContext>(Placed(Events()));

        Assert.Equal(
            "CREATE TABLE [dbo].[Events] (\n" +
            "    [Id] bigint NOT NULL,\n" +
            "    [OccurredAt] datetime2(3) NOT NULL\n" +
            ") ON [ps_Events]([OccurredAt]);\n",
            text);
    }

    [Fact]
    public void UnplacedTable_IsTheProvidersOutput()
    {
        // The override must not touch a table it has nothing to say about. Compared against the provider's own
        // generator on an identical operation rather than against a literal, so the assertion follows the
        // provider if its formatting ever changes.
        Assert.Equal(Generate<ProviderContext>(Events()), Generate<CodeFirstContext>(Events()));
    }

    [Fact]
    public void PlacedTableWithComments_KeepsTheOnClauseAndTheProvidersDescriptions()
    {
        // The provider refuses to leave a CREATE TABLE unterminated when a comment exists, because the
        // sp_addextendedproperty calls have to follow a terminated statement. The override hides the comments
        // for the one call that needs the statement open and then writes them exactly as the provider does, so
        // the only difference to the provider's output is the clause itself.
        var placed = Placed(Events(), withComments: true);
        var providers = Generate<ProviderContext>(Events(withComments: true));

        var text = Generate<CodeFirstContext>(placed);

        Assert.Contains(") ON [ps_Events]([OccurredAt]);\n", text, StringComparison.Ordinal);
        Assert.Equal(2, text.Split("sp_addextendedproperty").Length - 1);
        Assert.Equal(providers, text.Replace(" ON [ps_Events]([OccurredAt])", string.Empty, StringComparison.Ordinal));

        // The operation is handed back the way it came: a later generator pass (the scaffolder renders the
        // same operation into C#) must still see the comments.
        Assert.Equal("Every event", placed.Comment);
        Assert.Equal("When it happened", placed.Columns[1].Comment);
    }

    [Fact]
    public void TemporalPlacedTable_IsRefused()
    {
        var operation = Placed(Events());
        operation["SqlServer:IsTemporal"] = true;
        operation["SqlServer:TemporalHistoryTableName"] = "EventsHistory";
        operation["SqlServer:TemporalPeriodStartColumnName"] = "PeriodStart";
        operation["SqlServer:TemporalPeriodEndColumnName"] = "PeriodEnd";

        var ex = Assert.Throws<NotSupportedException>(() => Generate<CodeFirstContext>(operation));
        Assert.Contains("temporal", ex.Message);
    }

    [Fact]
    public void MemoryOptimizedPlacedTable_IsRefused()
    {
        var operation = Placed(Events());
        operation["SqlServer:MemoryOptimized"] = true;

        var ex = Assert.Throws<NotSupportedException>(() => Generate<CodeFirstContext>(operation));
        Assert.Contains("memory-optimized", ex.Message);
    }

    [Fact]
    public void HalfAPlacement_IsRefused()
    {
        var operation = Events();
        operation[CodeFirstAnnotations.PartitionScheme] = "ps_Events";

        Assert.Throws<InvalidOperationException>(() => Generate<CodeFirstContext>(operation));
    }

    [Theory]
    [InlineData(MigrationsSqlGenerationOptions.Default)]
    [InlineData(MigrationsSqlGenerationOptions.Idempotent)]
    [InlineData(MigrationsSqlGenerationOptions.NoTransactions)]
    public void GeneratedScript_CarriesTheOnClause(MigrationsSqlGenerationOptions options)
    {
        using var context = new CodeFirstContext();

        // GenerateScript is what `dotnet ef migrations script` calls and replays the migration below offline.
        // The migration carries the annotations the way a scaffolded one does — as .Annotation(...) calls on
        // the CreateTable builder — which is the form they arrive in at deployment.
        var script = context.GetService<IMigrator>()
            .GenerateScript(fromMigration: Migration.InitialDatabase, toMigration: null, options: options);

        Assert.Contains(") ON [ps_Events]([OccurredAt]);", script, StringComparison.Ordinal);
        if (options == MigrationsSqlGenerationOptions.Idempotent)
            Assert.Contains("IF NOT EXISTS", script, StringComparison.Ordinal);
    }

    [Fact]
    public void PartitionAndColumnstoreStatements_ReachTheProviderAsOneCommandEach()
    {
        using var context = new CodeFirstContext();
        var split = PartitionDiff.Compute(
                new PartitionLayout([new PartitionFunctionDefinition("pf", "int", PartitionRange.Right, ["1"])], [new PartitionSchemeDefinition("ps", "pf")]),
                new PartitionLayout([new PartitionFunctionDefinition("pf", "int", PartitionRange.Right, ["1", "2"])], [new PartitionSchemeDefinition("ps", "pf")]))
            .Single();
        var columnstore = ColumnstoreDiff.Compute([], [new ClusteredColumnstoreIndexDefinition("dbo", "Events", "cci")]).Single();

        // The SQL Server generator splits a SqlOperation on lines reading GO. Neither statement contains one,
        // and a multi-line BEGIN ... END block has to arrive as one batch or the split runs half of it.
        var commands = context.GetService<IMigrationsSqlGenerator>()
            .Generate([new SqlOperation { Sql = split.Sql }, new SqlOperation { Sql = columnstore.Sql }]);

        Assert.Collection(
            commands,
            c => Assert.Equal(split.Sql, c.CommandText.ReplaceLineEndings("\n").TrimEnd('\n')),
            c => Assert.Equal(columnstore.Sql, c.CommandText.ReplaceLineEndings("\n").TrimEnd('\n')));
    }

    [Fact]
    public void UseCodeFirstDatabaseObjects_InstallsBothServices()
    {
        using var context = new CodeFirstContext();

        Assert.IsType<CodeFirstDatabaseObjectsModelDiffer>(context.GetService<IMigrationsModelDiffer>());
        Assert.IsType<CodeFirstDatabaseObjectsMigrationsSqlGenerator>(context.GetService<IMigrationsSqlGenerator>());
    }

    private static string Generate<TContext>(CreateTableOperation operation)
        where TContext : DbContext, new()
    {
        using var context = new TContext();

        var command = Assert.Single(context.GetService<IMigrationsSqlGenerator>().Generate([operation]));

        // EF joins lines with the platform's line endings; the expectations are written with LF.
        return command.CommandText.ReplaceLineEndings("\n");
    }

    // A fresh operation per call: the generator under test restores what it touches, and building a new one
    // each time is what proves it rather than assuming it.
    private static CreateTableOperation Events(bool withComments = false)
    {
        var operation = new CreateTableOperation
        {
            Schema = "dbo",
            Name = "Events",
            Comment = withComments ? "Every event" : null,
        };
        operation.Columns.Add(new AddColumnOperation
        {
            Schema = "dbo",
            Table = "Events",
            Name = "Id",
            ClrType = typeof(long),
            ColumnType = "bigint",
            IsNullable = false,
        });
        operation.Columns.Add(new AddColumnOperation
        {
            Schema = "dbo",
            Table = "Events",
            Name = "OccurredAt",
            ClrType = typeof(DateTime),
            ColumnType = "datetime2(3)",
            IsNullable = false,
            Comment = withComments ? "When it happened" : null,
        });
        return operation;
    }

    private static CreateTableOperation Placed(CreateTableOperation operation, bool withComments = false)
    {
        if (withComments)
        {
            operation.Comment = "Every event";
            operation.Columns[1].Comment = "When it happened";
        }

        operation[CodeFirstAnnotations.PartitionScheme] = "ps_Events";
        operation[CodeFirstAnnotations.PartitionColumn] = "OccurredAt";
        return operation;
    }

    public sealed class CodeFirstContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseSqlServer("Server=host-that-must-never-be-contacted;Database=none")
                .UseCodeFirstDatabaseObjects();
    }

    public sealed class ProviderContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseSqlServer("Server=host-that-must-never-be-contacted;Database=none");
    }

    [DbContext(typeof(CodeFirstContext))]
    [Migration(MigrationId)]
    public sealed class PlacedTableMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                    name: "Events",
                    schema: "dbo",
                    columns: table => new
                    {
                        Id = table.Column<long>(nullable: false),
                        OccurredAt = table.Column<DateTime>(type: "datetime2(3)", nullable: false),
                    },
                    constraints: table => { })
                .Annotation(CodeFirstAnnotations.PartitionScheme, "ps_Events")
                .Annotation(CodeFirstAnnotations.PartitionColumn, "OccurredAt");
        }

        protected override void Down(MigrationBuilder migrationBuilder)
            => migrationBuilder.DropTable("Events", "dbo");
    }
}
