using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Raptor21.EF.Extensions.StoredProcedures.Scripts;
using Xunit;

namespace Raptor21.EF.Extensions.Migrations.Tests;

/// <summary>
/// Covers the shape of the SQL a procedure operation contributes to a generated deployment script.
/// <c>CREATE OR ALTER PROCEDURE</c> must be the first statement in its batch and is first in none of the
/// scripts EF writes, so the operation carries the script inside <c>EXEC(N'...')</c>; these tests pin both
/// the escaping and the fact that the wrap survives all the way to a <see cref="MigrationCommand"/>.
/// </summary>
public class IdempotentScriptTests
{
    private const string MigrationId = "20260101000000_Procedures";

    // One body carrying everything the escaping has to survive: an embedded quote, an already-doubled
    // quote, an apostrophe inside a line comment, and a line reading GO. It is not a synthetic worst case
    // - ParseQualifiedName accepts it, because a GO inside a string literal is content rather than a
    // separator, so this is a script a consumer can really register.
    private const string TrickyScript =
        "CREATE OR ALTER PROCEDURE [dbo].[TRICKY]\n" +
        "    @name nvarchar(50)\n" +
        "AS\n" +
        "BEGIN\n" +
        "    -- don't split me\n" +
        "    SELECT N'it''s fine' AS Quoted, @name AS Name;\n" +
        "    SELECT N'a\n" +
        "GO\n" +
        "b' AS NotASeparator;\n" +
        "END;\n";

    [Theory]
    [InlineData("SELECT 'a'", "EXEC(N'SELECT ''a''');")]
    [InlineData("SELECT 'it''s'", "EXEC(N'SELECT ''it''''s''');")]
    [InlineData("-- don't split me\nSELECT 1", "EXEC(N'-- don''t split me\nSELECT 1');")]
    public void WrapInExec_DoublesEverySingleQuote(string sql, string expected)
    {
        // Written out in full rather than computed, because the three cases are the ones the rule is easy
        // to get wrong on: a plain quoted literal, a literal that already contains a doubled quote (which
        // must be doubled again and not left alone), and a quote inside a comment (which is not special -
        // a comment is only characters once it is inside a string literal).
        Assert.Equal(expected, StoredProcedureScript.WrapInExec(sql));
    }

    [Fact]
    public void WrapInExec_TrimsTheTrailingBatchTerminator()
    {
        // The one liberty the wrap takes with the script, and it is cosmetic: without it a body ending in
        // END; would be emitted as EXEC(N'...END;');.
        Assert.Equal("EXEC(N'SELECT 1');", StoredProcedureScript.WrapInExec("SELECT 1;\r\n"));
    }

    [Fact]
    public void WrapInExec_EmitsOneLiteralAndRoundTrips()
    {
        var literal = UnwrapExec(StoredProcedureScript.WrapInExec(TrickyScript));

        // Un-escaping is exactly the inverse of the rule, so the body has to come back byte for byte apart
        // from the trailing terminator pinned above. Asserting the round trip rather than a hand-written
        // expected string is what makes this test survive an edit to the script: an escaping that dropped
        // or duplicated a character would fail here even though the emitted SQL still parsed, and a body
        // that deploys truncated is worse than one that fails to parse at all.
        Assert.Equal(TrickyScript.TrimEnd('\n', '\r', ';'), literal.Replace("''", "'"));
    }

    [Fact]
    public void TrickyScript_IsAScriptAConsumerCanRegister()
    {
        // Stated so the tests above cannot quietly drift into covering a script the library would refuse:
        // if the GO in that body ever became a separator, the escaping would be proven against input the
        // differ never sees.
        Assert.False(SqlBatch.ContainsSeparator(TrickyScript));
        Assert.Equal("dbo.TRICKY", StoredProcedureScript.ParseQualifiedName(TrickyScript, "tricky.sql"));
    }

    [Fact]
    public void Compute_WrapsCreateOrAlterAndLeavesDropAlone()
    {
        var source = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo.Gone"] = "CREATE OR ALTER PROCEDURE dbo.Gone AS SELECT 1",
        };
        var target = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["dbo.New"] = "CREATE OR ALTER PROCEDURE dbo.New AS SELECT 1",
        };

        var ops = StoredProcedureDiff.Compute(source, target);

        // The asymmetry is deliberate. DROP PROCEDURE carries no first-in-batch rule, is already valid
        // inside EF's IF NOT EXISTS ... BEGIN ... END, and wrapping it would hide what the migration does
        // behind a string literal for no gain.
        Assert.Equal(
            "EXEC(N'CREATE OR ALTER PROCEDURE dbo.New AS SELECT 1');",
            Assert.Single(ops, o => o.Kind == ProcOpKind.CreateOrAlter).Sql);
        Assert.Equal(
            "DROP PROCEDURE IF EXISTS [dbo].[Gone];",
            Assert.Single(ops, o => o.Kind == ProcOpKind.Drop).Sql);
    }

    [Theory]
    [InlineData(MigrationsSqlGenerationOptions.Idempotent)]
    [InlineData(MigrationsSqlGenerationOptions.Default)]
    [InlineData(MigrationsSqlGenerationOptions.NoTransactions)]
    public void GeneratedScript_NeverLeavesCreateOrAlterMidBatch(MigrationsSqlGenerationOptions options)
    {
        var script = GenerateScript(options);

        // CREATE OR ALTER PROCEDURE must be the first statement in its batch. With --idempotent EF puts
        // IF NOT EXISTS (...) BEGIN in front of every command, and the plain script opens a
        // BEGIN TRANSACTION and then separates commands with a newline instead of a batch terminator, so
        // in both the statement is not first and SQL Server rejects it. Only --no-transactions emits a GO
        // between commands; it is here as a regression guard, because a fix for the other two that broke
        // the one route already working would be no fix at all.
        //
        // Asserting what the line IS beats reasoning about what precedes it: there is exactly one way for
        // the procedure to be legal in all three, and that is for it to be an argument to EXEC.
        var line = Assert.Single(
            script.Split('\n'),
            l => l.Contains("CREATE OR ALTER PROCEDURE", StringComparison.OrdinalIgnoreCase));

        Assert.StartsWith("EXEC(N'", line.Trim(), StringComparison.Ordinal);
    }

    [Fact]
    public void IdempotentScript_StillWrapsCommandsInTheHistoryCheck()
    {
        var script = GenerateScript(MigrationsSqlGenerationOptions.Idempotent);

        // The other half of the scenario the theory above tests. Without this, a future EF that stopped
        // wrapping commands would leave that theory passing against a hazard that no longer exists, and
        // the wrap would look load-bearing when it had quietly become decoration.
        Assert.Contains("IF NOT EXISTS", script, StringComparison.Ordinal);
        Assert.Contains(MigrationId, script, StringComparison.Ordinal);
    }

    [Fact]
    public void WrappedProcedure_ReachesTheProviderAsOneIntactCommand()
    {
        using var context = new ScriptContext();

        var commands = context.GetService<IMigrationsSqlGenerator>()
            .Generate([new SqlOperation { Sql = ProcedureOperationSql() }]);

        // EF splits a SqlOperation on GO with its own scanner before anything reaches the database. The
        // body's GO is inside a literal and so is not a separator to either scanner - but the wrap has to
        // keep that true, because a split here would deploy the first half of the procedure and execute
        // the second half as loose T-SQL.
        var command = Assert.Single(commands);
        Assert.Equal(TrickyScript.TrimEnd('\n', '\r', ';'), UnwrapExec(command.CommandText).Replace("''", "'"));
    }

    private static string GenerateScript(MigrationsSqlGenerationOptions options)
    {
        using var context = new ScriptContext();

        // GenerateScript is what `dotnet ef migrations script` calls and is offline by contract: it replays
        // the migration below and never touches the database, which is why the connection string can name a
        // host that does not exist.
        return context.GetService<IMigrator>()
            .GenerateScript(fromMigration: Migration.InitialDatabase, toMigration: null, options: options);
    }

    // The SQL under test comes from the differ, not from WrapInExec directly, so the script tests fail for
    // the reason the gap describes rather than merely failing to compile: take the wrap back out of Compute
    // and this hands back the bare script, which is the invalid T-SQL those tests are about.
    private static string ProcedureOperationSql()
        => StoredProcedureDiff.Compute(
                new Dictionary<string, string>(),
                new Dictionary<string, string> { ["dbo.TRICKY"] = TrickyScript })
            .Single()
            .Sql;

    private static string UnwrapExec(string statement)
    {
        const string open = "EXEC(N'";
        const string close = "');";

        // EF re-joins the command with the platform's line endings, so normalise before comparing against a
        // source string written with LF.
        var text = statement.ReplaceLineEndings("\n").Trim();

        Assert.StartsWith(open, text, StringComparison.Ordinal);
        Assert.EndsWith(close, text, StringComparison.Ordinal);

        return text[open.Length..^close.Length];
    }

    public sealed class ScriptContext : DbContext
    {
        // The model is deliberately empty: GenerateScript replays the migration below and reads nothing
        // from the context's own model. The ReplaceService is here because it is how a consumer configures
        // this library, not because the script route needs it.
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseSqlServer("Server=host-that-must-never-be-contacted;Database=none")
                .ReplaceService<IMigrationsModelDiffer, StoredProcedureModelDiffer>();
    }

    [DbContext(typeof(ScriptContext))]
    [Migration(MigrationId)]
    public sealed class ProceduresMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // A table first, so the procedure is not the only command in the migration and the generated
            // script has the shape a real one has.
            migrationBuilder.CreateTable(
                name: "Widgets",
                columns: table => new { Id = table.Column<int>(nullable: false) },
                constraints: table => table.PrimaryKey("PK_Widgets", x => x.Id));

            migrationBuilder.Sql(ProcedureOperationSql());
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP PROCEDURE IF EXISTS [dbo].[TRICKY];");
            migrationBuilder.DropTable("Widgets");
        }
    }
}
