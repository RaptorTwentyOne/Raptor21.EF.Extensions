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
    // take here: the runtime applier split this script correctly while a regex over the whole text
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
    public void Bracket_WrapsParts(string input, string expected)
    {
        Assert.Equal(expected, StoredProcedureScript.Bracket(input));
    }
}
