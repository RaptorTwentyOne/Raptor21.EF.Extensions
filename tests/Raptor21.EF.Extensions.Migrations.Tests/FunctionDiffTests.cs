using Xunit;

namespace Raptor21.EF.Extensions.Migrations.Tests;

public class FunctionDiffTests
{
    private const string Scalar = "CREATE OR ALTER FUNCTION dbo.F() RETURNS int AS BEGIN RETURN 1 END";
    private const string ScalarEdited = "CREATE OR ALTER FUNCTION dbo.F() RETURNS int AS BEGIN RETURN 2 END";
    private const string Inline = "CREATE OR ALTER FUNCTION dbo.F() RETURNS TABLE AS RETURN SELECT 1 AS a";

    private static Dictionary<string, string> Set(params (string Name, string Sql)[] functions) =>
        functions.ToDictionary(f => f.Name, f => f.Sql, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void AddedFunction_EmitsAWrappedCreateOrAlter()
    {
        var op = Assert.Single(FunctionDiff.Compute(Set(), Set(("dbo.F", Scalar))));

        Assert.Equal("dbo.F", op.QualifiedName);
        Assert.Equal(FunctionOpKind.CreateOrAlter, op.Kind);
        Assert.Equal($"EXEC(N'{Scalar}');", op.Sql);
    }

    [Fact]
    public void RemovedFunction_EmitsDropIfExists()
    {
        var op = Assert.Single(FunctionDiff.Compute(Set(("dbo.F", Scalar)), Set()));

        Assert.Equal(FunctionOpKind.Drop, op.Kind);
        Assert.Equal("DROP FUNCTION IF EXISTS [dbo].[F];", op.Sql);
    }

    [Fact]
    public void ChangedBodyOfTheSameKind_EmitsCreateOrAlter()
    {
        var op = Assert.Single(FunctionDiff.Compute(Set(("dbo.F", Scalar)), Set(("dbo.F", ScalarEdited))));

        Assert.Equal(FunctionOpKind.CreateOrAlter, op.Kind);
        Assert.Contains("RETURN 2", op.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void UnchangedBody_DifferingLineEndings_EmitsNothing()
    {
        Assert.Empty(FunctionDiff.Compute(
            Set(("dbo.F", "CREATE OR ALTER FUNCTION dbo.F() RETURNS int AS\r\nBEGIN RETURN 1 END\r\n")),
            Set(("dbo.F", "CREATE OR ALTER FUNCTION dbo.F() RETURNS int AS\nBEGIN RETURN 1 END"))));
    }

    [Fact]
    public void ChangedPreamble_IsAChange()
    {
        // The options are recorded on the module, so flipping one is a real change even with the body untouched.
        var op = Assert.Single(FunctionDiff.Compute(Set(("dbo.F", Scalar)), Set(("dbo.F", "SET ANSI_NULLS OFF\n" + Scalar))));

        Assert.Equal(FunctionOpKind.CreateOrAlter, op.Kind);
        Assert.StartsWith("EXEC(N'SET ANSI_NULLS OFF; EXEC(N''", op.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void KindChange_IsADropAndACreateInOneStatement_BothWays()
    {
        // CREATE OR ALTER cannot turn a scalar function into a table-valued one, so it is dropped first - in the
        // same operation, so no failure can land between the two and leave the function missing.
        var up = Assert.Single(FunctionDiff.Compute(Set(("dbo.F", Scalar)), Set(("dbo.F", Inline))));
        Assert.Equal(FunctionOpKind.Recreate, up.Kind);
        Assert.Equal($"DROP FUNCTION IF EXISTS [dbo].[F];\nEXEC(N'{Inline}');", up.Sql);

        var down = Assert.Single(FunctionDiff.Compute(Set(("dbo.F", Inline)), Set(("dbo.F", Scalar))));
        Assert.Equal(FunctionOpKind.Recreate, down.Kind);
        Assert.Equal($"DROP FUNCTION IF EXISTS [dbo].[F];\nEXEC(N'{Scalar}');", down.Sql);
    }

    [Fact]
    public void Symmetric_DownDirection_ReversesOperations()
    {
        var snapshot = Set(("dbo.F", Scalar));
        var current = Set(
            ("dbo.F", ScalarEdited),
            ("dbo.G", "CREATE OR ALTER FUNCTION dbo.G() RETURNS int AS BEGIN RETURN 3 END"));

        var up = FunctionDiff.Compute(snapshot, current);
        Assert.Equal(2, up.Count);
        Assert.Contains(up, o => o.QualifiedName == "dbo.F" && o.Kind == FunctionOpKind.CreateOrAlter && o.Sql.Contains("RETURN 2"));
        Assert.Contains(up, o => o.QualifiedName == "dbo.G" && o.Kind == FunctionOpKind.CreateOrAlter);

        var down = FunctionDiff.Compute(current, snapshot);
        Assert.Equal(2, down.Count);
        Assert.Contains(down, o => o.QualifiedName == "dbo.F" && o.Kind == FunctionOpKind.CreateOrAlter && o.Sql.Contains("RETURN 1"));
        Assert.Contains(down, o => o.QualifiedName == "dbo.G" && o.Kind == FunctionOpKind.Drop);
    }

    [Fact]
    public void Operations_ComeOutInNameOrder()
    {
        var ops = FunctionDiff.Compute(
            Set(),
            Set(("dbo.b", Scalar.Replace("dbo.F", "dbo.b")), ("dbo.A", Scalar.Replace("dbo.F", "dbo.A")), ("dbo.c", Scalar.Replace("dbo.F", "dbo.c"))));

        Assert.Equal(["dbo.A", "dbo.b", "dbo.c"], ops.Select(o => o.QualifiedName));
    }
}
