using Raptor21.EF.Extensions.StoredProcedures.Scripts;
using Xunit;

namespace Raptor21.EF.Extensions.Migrations.Tests;

public class StoredProcedureScriptTests
{
    [Theory]
    [InlineData("CREATE OR ALTER PROCEDURE [dbo].[ACCOUNT_LOGIN] @x int AS SELECT 1", "dbo.ACCOUNT_LOGIN")]
    [InlineData("create or alter proc dbo.FOO as select 1", "dbo.FOO")]
    [InlineData("CREATE OR ALTER PROCEDURE BAR AS SELECT 1", "dbo.BAR")]
    [InlineData("CREATE  OR  ALTER  PROCEDURE  [sales] . [GET_X]  AS SELECT 1", "sales.GET_X")]
    public void ParseQualifiedName_ExtractsSchemaAndName(string sql, string expected)
    {
        Assert.Equal(expected, StoredProcedureScript.ParseQualifiedName(sql, "test.sql"));
    }

    [Fact]
    public void ParseQualifiedName_PlainCreate_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => StoredProcedureScript.ParseQualifiedName("CREATE PROCEDURE dbo.X AS SELECT 1", "x.sql"));
        Assert.Contains("CREATE OR ALTER", ex.Message);
    }

    [Fact]
    public void ParseQualifiedName_WithGo_Throws()
    {
        var sql = "CREATE OR ALTER PROCEDURE dbo.X AS SELECT 1\nGO\n";
        var ex = Assert.Throws<InvalidOperationException>(() => StoredProcedureScript.ParseQualifiedName(sql, "x.sql"));
        Assert.Contains("GO", ex.Message);
    }

    // The two halves of this library used to disagree about GO, which is the worst shape a defect can
    // take here: a character-aware scanner splits this script correctly while a regex over the whole text
    // rejected it outright, so a file that built cleanly failed at deployment. Both now call
    // SqlBatch, so a GO that is content rather than a separator is content on both sides.
    [Theory]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.X AS SELECT '\nGO\n'", "a string literal")]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.X AS /*\nGO\n*/ SELECT 1", "a block comment")]
    public void ParseQualifiedName_GoThatIsNotASeparator_IsAccepted(string sql, string construct)
    {
        Assert.Equal("dbo.X", StoredProcedureScript.ParseQualifiedName(sql, "x.sql"));

        // Stated twice on purpose: the point is not that this parser accepts the script, it is that both
        // readers reach the same verdict. A future change that teaches one of them a new construct and
        // not the other fails here rather than at someone's deployment.
        Assert.False(SqlBatch.ContainsSeparator(sql), $"GO inside {construct} is not a batch separator.");
    }

    [Fact]
    public void ParseQualifiedName_IndentedGo_Throws()
    {
        // The other direction of the same agreement: an indented GO IS a separator, so a script carrying
        // one is still not the single batch this parser requires.
        var sql = "CREATE OR ALTER PROCEDURE dbo.X AS SELECT 1\n    GO\n";

        Assert.Throws<InvalidOperationException>(() => StoredProcedureScript.ParseQualifiedName(sql, "x.sql"));
        Assert.True(SqlBatch.ContainsSeparator(sql));
    }
    [Theory]
    [InlineData("dbo.X", "[dbo].[X]")]
    [InlineData("X", "[X]")]
    [InlineData("dbo.MOB_NPC_İNSERT", "[dbo].[MOB_NPC_İNSERT]")]
    [InlineData("dbo.odd name]x", "[dbo].[odd name]]x]")]
    [InlineData("dbo.a.b", "[dbo].[a.b]")]
    public void Bracket_WrapsParts(string input, string expected)
    {
        Assert.Equal(expected, StoredProcedureScript.Bracket(input));
    }

    // A procedure of the KnightOnline database, verbatim in its name: U+0130 LATIN CAPITAL LETTER I WITH DOT
    // ABOVE. The header used to be read with an ASCII-only pattern, which stopped at that letter and registered
    // the script as dbo.MOB_NPC_ - so the annotation key, and the DROP its migration's Down ran, named an object
    // that does not exist.
    [Theory]
    [InlineData("CREATE OR ALTER PROCEDURE [dbo].[MOB_NPC_İNSERT] @x int AS SELECT 1", "dbo.MOB_NPC_İNSERT")]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.MOB_NPC_İNSERT @x int AS SELECT 1", "dbo.MOB_NPC_İNSERT")]
    [InlineData("CREATE OR ALTER PROCEDURE MOB_NPC_İNSERT AS SELECT 1", "dbo.MOB_NPC_İNSERT")]
    [InlineData("CREATE OR ALTER PROCEDURE [şema].[ÇAĞRI_1$] AS SELECT 1", "şema.ÇAĞRI_1$")]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.MOB_NPC_İNSERT AS SELECT 1", "dbo.MOB_NPC_İNSERT")]
    [InlineData("CREATE OR ALTER PROCEDURE dbo._x@y#z AS SELECT 1", "dbo._x@y#z")]
    public void ParseQualifiedName_ReadsUnicodeRegularIdentifiers(string sql, string expected)
    {
        Assert.Equal(expected, StoredProcedureScript.ParseQualifiedName(sql, "x.sql"));
    }

    [Theory]
    [InlineData("CREATE OR ALTER PROCEDURE [dbo].[odd name]]x] AS SELECT 1", "dbo.odd name]x")]
    [InlineData("CREATE OR ALTER PROCEDURE [odd name] AS SELECT 1", "dbo.odd name")]
    [InlineData("CREATE OR ALTER PROCEDURE [dbo].[a.b] AS SELECT 1", "dbo.a.b")]
    [InlineData("CREATE OR ALTER PROCEDURE \"dbo\".\"q\"\"x\" AS SELECT 1", "dbo.q\"x")]
    [InlineData("CREATE OR ALTER PROCEDURE [dbo].\"mixed\" AS SELECT 1", "dbo.mixed")]
    public void ParseQualifiedName_UnescapesDelimitedIdentifiers(string sql, string expected)
    {
        Assert.Equal(expected, StoredProcedureScript.ParseQualifiedName(sql, "x.sql"));
    }

    [Fact]
    public void DelimitedName_RoundTripsThroughBracket()
    {
        // What the Down's DROP names is Bracket of the parsed key, so the two have to be inverses: a ']' unescaped
        // by the parse comes back doubled, and a '.' inside the name stays inside the name.
        var key = StoredProcedureScript.ParseQualifiedName("CREATE OR ALTER PROCEDURE [dbo].[a.b]]c] AS SELECT 1", "x.sql");

        Assert.Equal("[dbo].[a.b]]c]", StoredProcedureScript.Bracket(key));
    }

    [Fact]
    public void ASchemaContainingADot_IsRefused()
    {
        // The canonical key is split on its first dot, so [a.b].[c] would come back as [a].[b.c]. Refused at
        // registration rather than dropped as the wrong object.
        var ex = Assert.Throws<InvalidOperationException>(
            () => StoredProcedureScript.ParseQualifiedName("CREATE OR ALTER PROCEDURE [a.b].[c] AS SELECT 1", "x.sql"));

        Assert.Contains("a.b", ex.Message, StringComparison.Ordinal);
        Assert.Contains("x.sql", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DropOfAUnicodeOrDelimitedName_NamesTheRealObject()
    {
        var ops = StoredProcedureDiff.Compute(
            new Dictionary<string, string>
            {
                ["dbo.MOB_NPC_İNSERT"] = "CREATE OR ALTER PROCEDURE [dbo].[MOB_NPC_İNSERT] AS SELECT 1",
                ["dbo.odd name]x"] = "CREATE OR ALTER PROCEDURE [dbo].[odd name]]x] AS SELECT 1",
            },
            new Dictionary<string, string>());

        Assert.Contains(ops, o => o.Sql == "DROP PROCEDURE IF EXISTS [dbo].[MOB_NPC_İNSERT];");
        Assert.Contains(ops, o => o.Sql == "DROP PROCEDURE IF EXISTS [dbo].[odd name]]x];");
    }
}
