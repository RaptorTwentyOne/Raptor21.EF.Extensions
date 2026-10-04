using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace Raptor21.EF.Extensions.Migrations.Tests;

/// <summary>
/// Covers <see cref="DatabaseScriptModelExtensions.RegisterDatabaseScripts"/>: one folder holding procedures
/// and functions, each classified by its header and recorded under its own annotation prefix.
/// </summary>
public class RegisterDatabaseScriptsTests
{
    private const string Prefix = "Fx.DbScripts.";

    private static string Procedure(string name) => $"CREATE OR ALTER PROCEDURE {name} AS SELECT 1;";

    private static string Function(string name) => $"CREATE OR ALTER FUNCTION {name}() RETURNS int AS BEGIN RETURN 1 END";

    private static IModel Register(FakeResourceAssembly assembly)
    {
        var builder = new ModelBuilder();
        builder.RegisterDatabaseScripts(assembly, Prefix);
        return builder.FinalizeModel();
    }

    [Fact]
    public void MixedFolder_SortsEachScriptUnderItsKind()
    {
        var assembly = new FakeResourceAssembly()
            .With("Fx.DbScripts.dbo.LOAD_USER.sql", Procedure("[dbo].[LOAD_USER]"))
            .With("Fx.DbScripts.dbo.GetAccountID.sql", Function("[dbo].[GetAccountID]"))
            .With("Fx.DbScripts.dbo.Legacy.sql", "SET ANSI_NULLS OFF\nSET QUOTED_IDENTIFIER OFF\n" + Procedure("dbo.Legacy"))
            .With("Fx.DbScripts.readme.txt", "not a script");

        var model = Register(assembly);

        var procedures = StoredProcedureModelExtensions.GetStoredProcedureScripts(model);
        var functions = DatabaseScriptModelExtensions.GetFunctionScripts(model);

        Assert.Equal(["dbo.LOAD_USER", "dbo.Legacy"], procedures.Keys.Order(StringComparer.Ordinal));
        Assert.Equal("dbo.GetAccountID", Assert.Single(functions).Key);

        // The preamble is part of the stored script: it is what the snapshot carries and what the diff compares.
        Assert.StartsWith("SET ANSI_NULLS OFF\n", procedures["dbo.Legacy"], StringComparison.Ordinal);
    }

    [Fact]
    public void AProcedureAndAFunctionOfOneName_CollideNamingBothFiles()
    {
        var assembly = new FakeResourceAssembly()
            .With("Fx.DbScripts.A.sql", Procedure("dbo.X"))
            .With("Fx.DbScripts.B.sql", Function("X"));

        var ex = Assert.Throws<InvalidOperationException>(() => Register(assembly));

        Assert.Contains("Fx.DbScripts.A.sql", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Fx.DbScripts.B.sql", ex.Message, StringComparison.Ordinal);
        Assert.Contains("dbo.X", ex.Message, StringComparison.Ordinal);
        Assert.Contains("one namespace", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoFunctionsOfOneName_Collide()
    {
        var assembly = new FakeResourceAssembly()
            .With("Fx.DbScripts.A.sql", Function("dbo.F"))
            .With("Fx.DbScripts.B.sql", Function("[dbo].[F]"));

        var ex = Assert.Throws<InvalidOperationException>(() => Register(assembly));

        Assert.Contains("function 'dbo.F'", ex.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("CREATE VIEW dbo.V AS SELECT 1 AS a")]
    [InlineData("CREATE PROCEDURE dbo.P AS SELECT 1")]
    [InlineData("CREATE FUNCTION dbo.F() RETURNS int AS BEGIN RETURN 1 END")]
    [InlineData("CREATE OR ALTER TRIGGER dbo.T ON dbo.W AFTER INSERT AS SELECT 1")]
    public void AnythingElse_IsRefusedNamingTheScript(string sql)
    {
        var assembly = new FakeResourceAssembly().With("Fx.DbScripts.Odd.sql", sql);

        var ex = Assert.Throws<InvalidOperationException>(() => Register(assembly));

        Assert.Contains("Odd.sql", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AFunctionWithoutReturns_IsRefusedAtRegistration()
    {
        var assembly = new FakeResourceAssembly().With("Fx.DbScripts.F.sql", "CREATE OR ALTER FUNCTION dbo.F() AS BEGIN RETURN 1 END");

        var ex = Assert.Throws<InvalidOperationException>(() => Register(assembly));

        Assert.Contains("F.sql", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARepeatedPreambleOption_IsRefusedNamingTheScript()
    {
        var assembly = new FakeResourceAssembly()
            .With("Fx.DbScripts.P.sql", "SET QUOTED_IDENTIFIER OFF\nSET QUOTED_IDENTIFIER ON\n" + Procedure("dbo.P"));

        var ex = Assert.Throws<InvalidOperationException>(() => Register(assembly));

        Assert.Contains("P.sql", ex.Message, StringComparison.Ordinal);
        Assert.Contains("QUOTED_IDENTIFIER", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ArgumentGuardsFireBeforeAnyResourceIsRead()
    {
        var builder = new ModelBuilder();
        var assembly = new FakeResourceAssembly();

        Assert.Throws<ArgumentNullException>(() => ((ModelBuilder)null!).RegisterDatabaseScripts(assembly, Prefix));
        Assert.Equal("assembly", Assert.Throws<ArgumentNullException>(
            () => builder.RegisterDatabaseScripts(null!, Prefix)).ParamName);
        Assert.Equal("resourcePrefix", Assert.Throws<ArgumentException>(
            () => builder.RegisterDatabaseScripts(assembly, "  ")).ParamName);
    }
}
