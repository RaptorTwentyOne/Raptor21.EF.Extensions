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

    [Theory]
    [InlineData("dbo.X", "[dbo].[X]")]
    [InlineData("X", "[X]")]
    public void Bracket_WrapsParts(string input, string expected)
    {
        Assert.Equal(expected, StoredProcedureScript.Bracket(input));
    }
}
