using Xunit;

namespace Raptor21.EF.Extensions.Migrations.Tests;

/// <summary>Covers the pure full-text diff: the statements for a catalog or index that appeared, vanished or changed, and their order.</summary>
public class FullTextDiffTests
{
    private static readonly FullTextLayout NoCatalogs = FullTextLayout.Empty;
    private static readonly FullTextLayout Account = new(["ft_Account"]);

    private static readonly FullTextIndexDefinition Customers = new(
        "dbo", "Customers", "ft_Account", "PK_Customers", FullTextChangeTracking.Auto,
        [new FullTextColumn("Name", 1055), new FullTextColumn("Email")]);

    [Fact]
    public void AddedCatalog_IsCreatedBehindAnExistenceGuard()
    {
        var op = Assert.Single(FullTextDiff.Compute(NoCatalogs, Account, [], []));

        Assert.Equal(FullTextOpKind.CreateCatalog, op.Kind);
        Assert.Equal("ft_Account", op.Name);
        Assert.Equal(
            "IF NOT EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'ft_Account')\n" +
            "    CREATE FULLTEXT CATALOG [ft_Account];",
            op.Sql);
    }

    [Fact]
    public void RemovedCatalog_IsDroppedIfItExists()
    {
        var op = Assert.Single(FullTextDiff.Compute(Account, NoCatalogs, [], []));

        Assert.Equal(FullTextOpKind.DropCatalog, op.Kind);
        Assert.True(op.IsDrop);
        Assert.Equal(
            "IF EXISTS (SELECT 1 FROM sys.fulltext_catalogs WHERE name = N'ft_Account')\n" +
            "    DROP FULLTEXT CATALOG [ft_Account];",
            op.Sql);
    }

    [Fact]
    public void AddedIndex_IsCreatedWithItsColumnsKeyCatalogAndTracking()
    {
        var op = Assert.Single(FullTextDiff.Compute(Account, Account, [], [Customers]));

        Assert.Equal(FullTextOpKind.CreateIndex, op.Kind);
        Assert.Equal("dbo.Customers", op.Name);
        Assert.Equal(
            "IF NOT EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'[dbo].[Customers]'))\n" +
            "    CREATE FULLTEXT INDEX ON [dbo].[Customers] ([Name] LANGUAGE 1055, [Email]) KEY INDEX [PK_Customers] ON [ft_Account] WITH CHANGE_TRACKING AUTO;",
            op.Sql);
    }

    [Fact]
    public void RemovedIndex_IsDroppedByTable()
    {
        // A table has one full-text index and sys.fulltext_indexes has no name for it, so both the guard and
        // the DROP address the table.
        var op = Assert.Single(FullTextDiff.Compute(Account, Account, [Customers], []));

        Assert.Equal(FullTextOpKind.DropIndex, op.Kind);
        Assert.Equal(
            "IF EXISTS (SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID(N'[dbo].[Customers]'))\n" +
            "    DROP FULLTEXT INDEX ON [dbo].[Customers];",
            op.Sql);
    }

    [Fact]
    public void ChangedIndex_IsADropThenACreate()
    {
        var ops = FullTextDiff.Compute(Account, Account, [Customers], [Customers with { ChangeTracking = FullTextChangeTracking.Manual }]);

        Assert.Collection(
            ops,
            op => Assert.Equal(FullTextOpKind.DropIndex, op.Kind),
            op =>
            {
                Assert.Equal(FullTextOpKind.CreateIndex, op.Kind);
                Assert.Contains("WITH CHANGE_TRACKING MANUAL;", op.Sql, StringComparison.Ordinal);
            });
    }

    [Fact]
    public void UnchangedIndex_EmitsNothing()
        => Assert.Empty(FullTextDiff.Compute(Account, Account, [Customers], [Customers with { Columns = [new FullTextColumn("EMAIL"), new FullTextColumn("name", 1055)] }]));

    [Fact]
    public void Steps_ComeOutInTheOrderThatIsValidOnItsOwn()
    {
        // Index drops first (an index in a catalog being dropped must go before the catalog), catalog creates
        // before the indexes that live in them, catalog drops last.
        var orders = new FullTextIndexDefinition("dbo", "Orders", "ft_Sales", "PK_Orders", FullTextChangeTracking.Auto, [new FullTextColumn("Note")]);
        var ops = FullTextDiff.Compute(Account, new(["ft_Sales"]), [Customers], [orders]);

        Assert.Equal(
            [FullTextOpKind.DropIndex, FullTextOpKind.CreateCatalog, FullTextOpKind.CreateIndex, FullTextOpKind.DropCatalog],
            ops.Select(o => o.Kind).ToArray());
    }

    [Fact]
    public void TableWithoutSchema_IsNamedBare()
    {
        var op = Assert.Single(FullTextDiff.Compute(Account, Account, [], [Customers with { Schema = null }]));

        Assert.Contains("OBJECT_ID(N'[Customers]')", op.Sql, StringComparison.Ordinal);
        Assert.Contains("CREATE FULLTEXT INDEX ON [Customers] (", op.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void BracketsAndQuotes_AreEscaped()
    {
        var op = Assert.Single(FullTextDiff.Compute(NoCatalogs, new(["it's"]), [], []));

        Assert.Contains("WHERE name = N'it''s'", op.Sql, StringComparison.Ordinal);
        Assert.EndsWith("CREATE FULLTEXT CATALOG [it's];", op.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void TwoIndexesOnOneTable_AreRefused()
        => Assert.Throws<ArgumentException>(
            () => FullTextDiff.Compute(Account, Account, [], [Customers, Customers with { Columns = [new FullTextColumn("Name")] }]));

    [Fact]
    public void ACatalogDeclaredTwice_IsRefused()
        => Assert.Throws<ArgumentException>(() => new FullTextLayout(["ft", "FT"]));
}
