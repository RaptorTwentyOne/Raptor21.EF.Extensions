using Xunit;

namespace Raptor21.EF.Extensions.Migrations.Tests;

/// <summary>Covers the pure columnstore diff: one statement per table whose index appeared, vanished or was renamed.</summary>
public class ColumnstoreDiffTests
{
    private static readonly ClusteredColumnstoreIndexDefinition Events = new("dbo", "Events", "cci_Events");

    [Fact]
    public void AddedIndex_IsCreatedBehindAnExistenceGuard()
    {
        var op = Assert.Single(ColumnstoreDiff.Compute([], [Events]));

        Assert.Equal(ColumnstoreOpKind.Create, op.Kind);
        Assert.Equal(
            "IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'cci_Events' AND object_id = OBJECT_ID(N'[dbo].[Events]'))\n" +
            "    CREATE CLUSTERED COLUMNSTORE INDEX [cci_Events] ON [dbo].[Events];",
            op.Sql);
    }

    [Fact]
    public void RemovedIndex_IsDroppedIfItExists()
    {
        var op = Assert.Single(ColumnstoreDiff.Compute([Events], []));

        Assert.Equal(ColumnstoreOpKind.Drop, op.Kind);
        Assert.Equal("DROP INDEX IF EXISTS [cci_Events] ON [dbo].[Events];", op.Sql);
    }

    [Fact]
    public void RenamedIndex_IsRenamedInPlace()
    {
        // Dropping and recreating a columnstore index rebuilds every row group of the table; sp_rename is a
        // catalog update. The guard is on the OLD name, because that is what exists when the step runs.
        var op = Assert.Single(ColumnstoreDiff.Compute([Events], [Events with { IndexName = "CCI_Events_v2" }]));

        Assert.Equal(ColumnstoreOpKind.Rename, op.Kind);
        Assert.Equal("CCI_Events_v2", op.IndexName);
        Assert.Equal(
            "IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'cci_Events' AND object_id = OBJECT_ID(N'[dbo].[Events]'))\n" +
            "    EXEC sp_rename N'[dbo].[Events].[cci_Events]', N'CCI_Events_v2', N'INDEX';",
            op.Sql);
    }

    [Fact]
    public void UnchangedIndex_EmitsNothing()
        => Assert.Empty(ColumnstoreDiff.Compute([Events], [Events with { }]));

    [Fact]
    public void TableWithoutSchema_IsNamedBare()
    {
        // ITable.Schema is null when the model has no default schema; EF writes CREATE TABLE [Events] in that
        // case and the index statement has to resolve to the same table under the same default.
        var op = Assert.Single(ColumnstoreDiff.Compute([], [new ClusteredColumnstoreIndexDefinition(null, "Events", "cci")]));

        Assert.Contains("OBJECT_ID(N'[Events]')", op.Sql);
        Assert.EndsWith("ON [Events];", op.Sql);
    }

    [Fact]
    public void BracketsAndQuotes_AreEscaped()
    {
        var op = Assert.Single(ColumnstoreDiff.Compute([], [new ClusteredColumnstoreIndexDefinition("dbo", "We]ird", "it's")]));

        Assert.Contains("WHERE name = N'it''s'", op.Sql);
        Assert.Contains("OBJECT_ID(N'[dbo].[We]]ird]')", op.Sql);
        Assert.EndsWith("CREATE CLUSTERED COLUMNSTORE INDEX [it's] ON [dbo].[We]]ird];", op.Sql);
    }

    [Fact]
    public void TwoIndexesOnOneTable_AreRefused()
        => Assert.Throws<ArgumentException>(
            () => ColumnstoreDiff.Compute([], [Events, Events with { IndexName = "cci_Other" }]));

    [Fact]
    public void Symmetric_ACreateGoingUpIsADropComingDown()
    {
        Assert.Equal(ColumnstoreOpKind.Create, Assert.Single(ColumnstoreDiff.Compute([], [Events])).Kind);
        Assert.Equal(ColumnstoreOpKind.Drop, Assert.Single(ColumnstoreDiff.Compute([Events], [])).Kind);
    }
}
