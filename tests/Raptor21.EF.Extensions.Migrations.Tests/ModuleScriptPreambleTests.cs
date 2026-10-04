using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Raptor21.EF.Extensions.Migrations.Tests;

/// <summary>
/// Covers the <c>SET ANSI_NULLS</c> / <c>SET QUOTED_IDENTIFIER</c> preamble: how it is split from the module,
/// and the nested <c>EXEC</c> it turns the wrap into. The two options are recorded on a module when it is
/// created, so a module whose options are OFF on the server can only be reproduced by a migration that creates
/// it under OFF.
/// </summary>
public class ModuleScriptPreambleTests
{
    private const string Module = "CREATE OR ALTER PROCEDURE dbo.P AS\nSELECT \"abc\" AS x WHERE @v = NULL\n";

    [Fact]
    public void NoPreamble_LeavesTheScriptExactlyAsWritten()
    {
        const string sql = "\r\n-- header comment\r\nCREATE OR ALTER PROCEDURE dbo.P AS SELECT 1";

        var preamble = ModuleScriptPreamble.Parse(sql);

        Assert.False(preamble.HasOptions);
        Assert.Empty(preamble.SetStatements);
        Assert.Null(preamble.AnsiNulls);
        Assert.Null(preamble.QuotedIdentifier);
        Assert.Same(sql, preamble.ModuleText);
    }

    [Theory]
    [InlineData("SET ANSI_NULLS OFF\nSET QUOTED_IDENTIFIER OFF\n" + Module)]
    [InlineData("SET ANSI_NULLS OFF;\r\nSET QUOTED_IDENTIFIER OFF;\r\n" + Module)]
    [InlineData("  set ansi_nulls off;   set quoted_identifier off;  \n" + Module)]
    [InlineData("﻿SET ANSI_NULLS OFF\n\nSET QUOTED_IDENTIFIER OFF\n" + Module)]
    public void Preamble_IsSplitFromTheModule(string sql)
    {
        var preamble = ModuleScriptPreamble.Parse(sql);

        Assert.True(preamble.HasOptions);
        Assert.Equal(["SET ANSI_NULLS OFF;", "SET QUOTED_IDENTIFIER OFF;"], preamble.SetStatements);
        Assert.False(preamble.AnsiNulls);
        Assert.False(preamble.QuotedIdentifier);

        // Byte for byte: the module is what SQL Server will store in sys.sql_modules.definition.
        Assert.Equal(Module, preamble.ModuleText);
    }

    [Fact]
    public void OnlyTheLineBreakEndingTheLastStatementBelongsToThePreamble()
    {
        // A module that opens with a blank line keeps it, so the line numbers SQL Server reports in an error
        // still point at the module's own text.
        var preamble = ModuleScriptPreamble.Parse("SET QUOTED_IDENTIFIER ON\n\n" + Module);

        Assert.True(preamble.QuotedIdentifier);
        Assert.Null(preamble.AnsiNulls);
        Assert.Equal("\n" + Module, preamble.ModuleText);
    }

    [Fact]
    public void ASetInsideTheModule_IsNotAPreamble()
    {
        // The preamble is anchored at the start of the script. A SET the module itself runs - or any other
        // statement first - ends it before it began.
        const string sql = "-- comment\nSET ANSI_NULLS OFF\nCREATE OR ALTER PROCEDURE dbo.P AS SELECT 1";
        Assert.False(ModuleScriptPreamble.Parse(sql).HasOptions);

        const string nocount = "SET NOCOUNT ON\nCREATE OR ALTER PROCEDURE dbo.P AS SELECT 1";
        Assert.False(ModuleScriptPreamble.Parse(nocount).HasOptions);
    }

    [Fact]
    public void TheSameOptionTwice_IsRefused()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ModuleScriptPreamble.Parse("SET ANSI_NULLS OFF\nSET ANSI_NULLS ON\n" + Module));

        Assert.Contains("ANSI_NULLS", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WrapInExec_WithoutAPreamble_IsTheSingleWrap()
    {
        // Byte-identical to the wrap every existing migration was scaffolded with: a consumer that adds no
        // preamble must not see a single migration change.
        Assert.Equal("EXEC(N'SELECT ''a''');", StoredProcedureScript.WrapInExec("SELECT 'a'"));
    }

    [Fact]
    public void WrapInExec_WithAPreamble_NestsTheModuleInsideADynamicBatchThatSetsTheOptions()
    {
        const string sql = "SET ANSI_NULLS OFF\nSET QUOTED_IDENTIFIER OFF\nCREATE OR ALTER PROCEDURE dbo.P AS SELECT 'it''s' AS q;\n";

        // Written out in full: the module's quotes are doubled once for the inner literal, and the whole inner
        // statement - its own quotes included - is doubled again for the outer one. One literal per level.
        Assert.Equal(
            "EXEC(N'SET ANSI_NULLS OFF; SET QUOTED_IDENTIFIER OFF; EXEC(N''CREATE OR ALTER PROCEDURE dbo.P AS SELECT ''''it''''''''s'''' AS q'')');",
            StoredProcedureScript.WrapInExec(sql));
    }

    [Fact]
    public void WrapInExec_WithAPreamble_RoundTripsTheModule()
    {
        const string sql = "SET QUOTED_IDENTIFIER OFF;\n" +
                           "CREATE OR ALTER FUNCTION dbo.F(@s varchar(10))\nRETURNS varchar(20)\nAS\nBEGIN\n" +
                           "    -- don't trip\n    RETURN \"x\" + @s + N'it''s'\nEND\n";

        var outer = Unwrap(StoredProcedureScript.WrapInExec(sql), "EXEC(N'", "');").Replace("''", "'");

        const string setPart = "SET QUOTED_IDENTIFIER OFF; ";
        Assert.StartsWith(setPart, outer, StringComparison.Ordinal);

        var inner = Unwrap(outer[setPart.Length..], "EXEC(N'", "')").Replace("''", "'");
        Assert.Equal(ModuleScriptPreamble.Parse(sql).ModuleText.TrimEnd('\n', '\r', ';'), inner);
    }

    [Fact]
    public void PreambleWrap_ReachesTheProviderAsOneIntactCommand()
    {
        using var context = new ScriptContext();
        var sql = StoredProcedureScript.WrapInExec("SET ANSI_NULLS OFF\n" + Module);

        var commands = context.GetService<IMigrationsSqlGenerator>().Generate([new SqlOperation { Sql = sql }]);

        // The SETs and the module must arrive together: split into two commands, the SET would run in a batch
        // of its own and be gone before the module was created.
        var command = Assert.Single(commands);
        Assert.Equal(sql, command.CommandText.ReplaceLineEndings("\n").Trim());
    }

    [Theory]
    [InlineData(MigrationsSqlGenerationOptions.Idempotent)]
    [InlineData(MigrationsSqlGenerationOptions.Default)]
    [InlineData(MigrationsSqlGenerationOptions.NoTransactions)]
    public void GeneratedScript_KeepsTheModuleInsideTheNestedExec(MigrationsSqlGenerationOptions options)
    {
        using var context = new ScriptContext();

        var script = context.GetService<IMigrator>()
            .GenerateScript(fromMigration: Migration.InitialDatabase, toMigration: null, options: options);

        // Every route a script takes - inside IF NOT EXISTS ... BEGIN for --idempotent, after BEGIN TRANSACTION
        // for the plain script - leaves the CREATE OR ALTER legal only as an argument to EXEC, and the SETs only
        // effective inside the outer dynamic batch; both hold when the line is the nested EXEC.
        var line = Assert.Single(
            script.Split('\n'),
            l => l.Contains("CREATE OR ALTER FUNCTION", StringComparison.OrdinalIgnoreCase));

        Assert.StartsWith("EXEC(N'SET ANSI_NULLS OFF; SET QUOTED_IDENTIFIER OFF; EXEC(N''", line.Trim(), StringComparison.Ordinal);
    }

    private static string Unwrap(string text, string open, string close)
    {
        Assert.StartsWith(open, text, StringComparison.Ordinal);
        Assert.EndsWith(close, text, StringComparison.Ordinal);
        return text[open.Length..^close.Length];
    }

    private const string LegacyFunction =
        "SET ANSI_NULLS OFF\nSET QUOTED_IDENTIFIER OFF\nCREATE OR ALTER FUNCTION dbo.Legacy() RETURNS varchar(10) AS BEGIN RETURN \"x\" END";

    public sealed class ScriptContext : DbContext
    {
        protected override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseSqlServer("Server=host-that-must-never-be-contacted;Database=none")
                .UseCodeFirstDatabaseObjects();
    }

    [DbContext(typeof(ScriptContext))]
    [Migration("20260102000000_LegacyFunction")]
    public sealed class LegacyFunctionMigration : Migration
    {
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Widgets",
                columns: table => new { Id = table.Column<int>(nullable: false) },
                constraints: table => table.PrimaryKey("PK_Widgets", x => x.Id));

            // The statement the differ scaffolds for the function, exactly.
            migrationBuilder.Sql(FunctionDiff.Compute(
                    new Dictionary<string, string>(),
                    new Dictionary<string, string> { ["dbo.Legacy"] = LegacyFunction })
                .Single()
                .Sql);
        }

        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP FUNCTION IF EXISTS [dbo].[Legacy];");
            migrationBuilder.DropTable("Widgets");
        }
    }
}
