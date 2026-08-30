using Xunit;

namespace Raptor21.EF.Extensions.Migrations.Tests;

public class StoredProcedureDiffTests
{
    private static Dictionary<string, string> Set(params (string Name, string Sql)[] procs) =>
        procs.ToDictionary(p => p.Name, p => p.Sql, StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void AddedProcedure_EmitsCreateOrAlter()
    {
        var ops = StoredProcedureDiff.Compute(Set(), Set(("dbo.A", "CREATE OR ALTER PROCEDURE dbo.A AS SELECT 1")));

        var op = Assert.Single(ops);
        Assert.Equal("dbo.A", op.QualifiedName);
        Assert.Equal(ProcOpKind.CreateOrAlter, op.Kind);
        Assert.Contains("CREATE OR ALTER", op.Sql);
    }

    [Fact]
    public void RemovedProcedure_EmitsDropIfExists()
    {
        var ops = StoredProcedureDiff.Compute(Set(("dbo.A", "CREATE OR ALTER PROCEDURE dbo.A AS SELECT 1")), Set());

        var op = Assert.Single(ops);
        Assert.Equal(ProcOpKind.Drop, op.Kind);
        Assert.Equal("DROP PROCEDURE IF EXISTS [dbo].[A];", op.Sql);
    }

    [Fact]
    public void ChangedBody_EmitsCreateOrAlterWithNewBody()
    {
        var ops = StoredProcedureDiff.Compute(
            Set(("dbo.A", "CREATE OR ALTER PROCEDURE dbo.A AS SELECT 1")),
            Set(("dbo.A", "CREATE OR ALTER PROCEDURE dbo.A AS SELECT 2")));

        var op = Assert.Single(ops);
        Assert.Equal(ProcOpKind.CreateOrAlter, op.Kind);
        Assert.Contains("SELECT 2", op.Sql);
    }

    [Fact]
    public void UnchangedBody_DifferingLineEndings_EmitsNothing()
    {
        var ops = StoredProcedureDiff.Compute(
            Set(("dbo.A", "CREATE OR ALTER PROCEDURE dbo.A AS\r\nSELECT 1\r\n")),
            Set(("dbo.A", "CREATE OR ALTER PROCEDURE dbo.A AS\nSELECT 1")));

        Assert.Empty(ops);
    }

    [Fact]
    public void Symmetric_DownDirection_ReversesOperations()
    {
        var snapshot = Set(("dbo.A", "CREATE OR ALTER PROCEDURE dbo.A AS SELECT 1"));   // old
        var current = Set(
            ("dbo.A", "CREATE OR ALTER PROCEDURE dbo.A AS SELECT 2"),                    // changed
            ("dbo.B", "CREATE OR ALTER PROCEDURE dbo.B AS SELECT 1"));                   // added

        // Up: snapshot -> current
        var up = StoredProcedureDiff.Compute(snapshot, current);
        Assert.Equal(2, up.Count);
        Assert.Contains(up, o => o.QualifiedName == "dbo.A" && o.Kind == ProcOpKind.CreateOrAlter && o.Sql.Contains("SELECT 2"));
        Assert.Contains(up, o => o.QualifiedName == "dbo.B" && o.Kind == ProcOpKind.CreateOrAlter);

        // Down: current -> snapshot (EF calls the differ with arguments swapped)
        var down = StoredProcedureDiff.Compute(current, snapshot);
        Assert.Equal(2, down.Count);
        Assert.Contains(down, o => o.QualifiedName == "dbo.A" && o.Kind == ProcOpKind.CreateOrAlter && o.Sql.Contains("SELECT 1")); // restored old body
        Assert.Contains(down, o => o.QualifiedName == "dbo.B" && o.Kind == ProcOpKind.Drop);                                        // newly added -> dropped
    }
}
