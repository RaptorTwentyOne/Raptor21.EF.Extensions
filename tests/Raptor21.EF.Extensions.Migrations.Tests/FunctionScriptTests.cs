using Xunit;

namespace Raptor21.EF.Extensions.Migrations.Tests;

public class FunctionScriptTests
{
    [Theory]
    [InlineData("CREATE OR ALTER FUNCTION [dbo].[GetAccountID](@x int) RETURNS int AS BEGIN RETURN 1 END", "dbo.GetAccountID")]
    [InlineData("create or alter function dbo.f() returns int as begin return 1 end", "dbo.f")]
    [InlineData("CREATE OR ALTER FUNCTION F() RETURNS int AS BEGIN RETURN 1 END", "dbo.F")]
    [InlineData("CREATE  OR  ALTER  FUNCTION  [sales] . [GET_X]() RETURNS TABLE AS RETURN SELECT 1 AS x", "sales.GET_X")]
    [InlineData("SET QUOTED_IDENTIFIER OFF\n-- leading comment\nCREATE OR ALTER FUNCTION dbo.Q() RETURNS int AS BEGIN RETURN 1 END", "dbo.Q")]
    public void ParseQualifiedName_ExtractsSchemaAndName(string sql, string expected)
    {
        Assert.Equal(expected, FunctionScript.ParseQualifiedName(sql, "test.sql"));
    }

    // The same identifier rules as procedures: Unicode regular identifiers read whole, delimited ones unescaped.
    [Theory]
    [InlineData("CREATE OR ALTER FUNCTION [dbo].[MOB_NPC_İNSERT]() RETURNS int AS BEGIN RETURN 1 END", "dbo.MOB_NPC_İNSERT")]
    [InlineData("CREATE OR ALTER FUNCTION dbo.MOB_NPC_İNSERT() RETURNS int AS BEGIN RETURN 1 END", "dbo.MOB_NPC_İNSERT")]
    [InlineData("CREATE OR ALTER FUNCTION MOB_NPC_İNSERT(@a int) RETURNS TABLE AS RETURN SELECT @a AS a", "dbo.MOB_NPC_İNSERT")]
    [InlineData("CREATE OR ALTER FUNCTION [dbo].[f name]]x]() RETURNS int AS BEGIN RETURN 1 END", "dbo.f name]x")]
    public void ParseQualifiedName_FollowsTheIdentifierRules(string sql, string expected)
    {
        Assert.Equal(expected, FunctionScript.ParseQualifiedName(sql, "f.sql"));
    }

    [Fact]
    public void AUnicodeName_StillReadsItsKindAfterTheName()
    {
        // The kind is read from the RETURNS clause AFTER the header, so a header cut short at a non-ASCII letter
        // would also start that search inside the name.
        Assert.Equal(
            FunctionKind.InlineTableValued,
            FunctionScript.DetectKind("CREATE OR ALTER FUNCTION dbo.İŞLEV_RETURNS(@a int) RETURNS TABLE AS RETURN SELECT @a AS a", "f.sql"));
    }

    [Fact]
    public void DropOfAUnicodeOrDelimitedName_NamesTheRealObject()
    {
        var ops = FunctionDiff.Compute(
            new Dictionary<string, string>
            {
                ["dbo.MOB_NPC_İNSERT"] = "CREATE OR ALTER FUNCTION dbo.MOB_NPC_İNSERT() RETURNS int AS BEGIN RETURN 1 END",
                ["dbo.f name]x"] = "CREATE OR ALTER FUNCTION [dbo].[f name]]x]() RETURNS int AS BEGIN RETURN 1 END",
            },
            new Dictionary<string, string>());

        Assert.Contains(ops, o => o.Sql == "DROP FUNCTION IF EXISTS [dbo].[MOB_NPC_İNSERT];");
        Assert.Contains(ops, o => o.Sql == "DROP FUNCTION IF EXISTS [dbo].[f name]]x];");
    }

    [Fact]
    public void ParseQualifiedName_PlainCreate_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => FunctionScript.ParseQualifiedName("CREATE FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1 END", "f.sql"));
        Assert.Contains("CREATE OR ALTER FUNCTION", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ParseQualifiedName_AProcedure_Throws()
    {
        Assert.Throws<InvalidOperationException>(
            () => FunctionScript.ParseQualifiedName("CREATE OR ALTER PROCEDURE dbo.p AS SELECT 1", "p.sql"));
    }

    [Fact]
    public void ParseQualifiedName_WithGo_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => FunctionScript.ParseQualifiedName("CREATE OR ALTER FUNCTION dbo.f() RETURNS int AS BEGIN RETURN 1 END\nGO\n", "f.sql"));
        Assert.Contains("GO", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CREATE OR ALTER FUNCTION dbo.f(@a varchar(21)) RETURNS varchar(21) AS BEGIN RETURN @a END", FunctionKind.Scalar)]
    [InlineData("CREATE OR ALTER FUNCTION dbo.f() RETURNS bit WITH SCHEMABINDING AS BEGIN RETURN 1 END", FunctionKind.Scalar)]
    [InlineData("CREATE OR ALTER FUNCTION dbo.f(@a int) RETURNS TABLE AS RETURN (SELECT @a AS a)", FunctionKind.InlineTableValued)]
    [InlineData("CREATE OR ALTER FUNCTION dbo.f(@a int)\nRETURNS\n  table\nAS RETURN SELECT 1 AS a", FunctionKind.InlineTableValued)]
    [InlineData("CREATE OR ALTER FUNCTION dbo.f() RETURNS @t TABLE (a int) AS BEGIN INSERT @t VALUES (1) RETURN END", FunctionKind.MultiStatementTableValued)]
    [InlineData("CREATE OR ALTER FUNCTION dbo.f() RETURNS @result /* rows */ TABLE (a int) AS BEGIN RETURN END", FunctionKind.MultiStatementTableValued)]
    public void DetectKind_ReadsTheReturnsClause(string sql, FunctionKind expected)
    {
        Assert.Equal(expected, FunctionScript.DetectKind(sql, "f.sql"));
    }

    [Fact]
    public void DetectKind_IgnoresReturnsInsideCommentsAndLiterals()
    {
        // A parameter list annotated in prose, and a default spelled like the keyword, must not decide the kind:
        // read naively, the first "returns" here is followed by "the", and the second by "TABLE'".
        const string sql =
            "CREATE OR ALTER FUNCTION dbo.f(\n" +
            "    @a int, -- returns the id\n" +
            "    @b varchar(20) = 'RETURNS TABLE' /* returns table */\n" +
            ")\n" +
            "RETURNS TABLE AS RETURN SELECT @a AS a";

        Assert.Equal(FunctionKind.InlineTableValued, FunctionScript.DetectKind(sql, "f.sql"));

        const string scalar =
            "CREATE OR ALTER FUNCTION dbo.f(@a int /* returns table */) RETURNS int AS BEGIN RETURN @a END";
        Assert.Equal(FunctionKind.Scalar, FunctionScript.DetectKind(scalar, "f.sql"));
    }

    [Fact]
    public void DetectKind_NoReturnsClause_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => FunctionScript.DetectKind("CREATE OR ALTER FUNCTION dbo.f() AS BEGIN RETURN 1 END", "f.sql"));
        Assert.Contains("RETURNS", ex.Message, StringComparison.Ordinal);
    }
}
