using Xunit;

namespace Raptor21.EF.Extensions.Migrations.Tests;

/// <summary>
/// Covers the pure partition diff: the T-SQL each step produces and the order the steps come out in. The
/// order is the part a database cares about — a scheme created before its function or dropped before the
/// last table left it fails at <c>database update</c>, not at <c>migrations add</c>.
/// </summary>
public class PartitionDiffTests
{
    private static readonly PartitionFunctionDefinition Months = new(
        "pf_EventsMonth", "datetime2(3)", PartitionRange.Right, ["'2026-08-01'", "'2026-09-01'"]);

    private static readonly PartitionSchemeDefinition Events = new("ps_Events", "pf_EventsMonth");

    private static PartitionLayout Layout(
        IEnumerable<PartitionFunctionDefinition>? functions = null,
        IEnumerable<PartitionSchemeDefinition>? schemes = null)
        => new(functions ?? [], schemes ?? []);

    [Fact]
    public void NewFunctionAndScheme_CreateTheFunctionFirst()
    {
        var ops = PartitionDiff.Compute(PartitionLayout.Empty, Layout([Months], [Events]));

        Assert.Collection(
            ops,
            op =>
            {
                Assert.Equal(PartitionOpKind.CreateFunction, op.Kind);
                Assert.Equal(
                    "IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'pf_EventsMonth')\n" +
                    "    CREATE PARTITION FUNCTION [pf_EventsMonth] (datetime2(3)) AS RANGE RIGHT FOR VALUES ('2026-08-01', '2026-09-01');",
                    op.Sql);
            },
            op =>
            {
                Assert.Equal(PartitionOpKind.CreateScheme, op.Kind);
                Assert.Equal(
                    "IF NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'ps_Events')\n" +
                    "    CREATE PARTITION SCHEME [ps_Events] AS PARTITION [pf_EventsMonth] ALL TO ([PRIMARY]);",
                    op.Sql);
            });
    }

    [Fact]
    public void RemovedFunctionAndScheme_DropTheSchemeFirst()
    {
        var ops = PartitionDiff.Compute(Layout([Months], [Events]), PartitionLayout.Empty);

        Assert.Collection(
            ops,
            op =>
            {
                Assert.Equal(PartitionOpKind.DropScheme, op.Kind);
                Assert.True(op.IsDrop);
                Assert.Equal(
                    "IF EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = N'ps_Events')\n" +
                    "    DROP PARTITION SCHEME [ps_Events];",
                    op.Sql);
            },
            op =>
            {
                Assert.Equal(PartitionOpKind.DropFunction, op.Kind);
                Assert.True(op.IsDrop);
                Assert.Equal(
                    "IF EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = N'pf_EventsMonth')\n" +
                    "    DROP PARTITION FUNCTION [pf_EventsMonth];",
                    op.Sql);
            });
    }

    [Fact]
    public void AddedBoundary_SplitsAfterNamingNextUsedOnEverySchemeOfThatFunction()
    {
        var grown = Months with { Boundaries = [.. Months.Boundaries, "'2026-10-01'"] };
        var other = new PartitionFunctionDefinition("pf_Other", "int", PartitionRange.Left, ["10"]);
        var target = Layout(
            [grown, other],
            [Events, new PartitionSchemeDefinition("ps_Archive", "pf_EventsMonth", "FG_Archive"), new PartitionSchemeDefinition("ps_Other", "pf_Other")]);

        var op = Assert.Single(PartitionDiff.Compute(Layout([Months, other], target.Schemes.Values), target));

        Assert.Equal(PartitionOpKind.SplitRange, op.Kind);
        Assert.False(op.IsDrop);
        Assert.Equal(
            "IF NOT EXISTS (SELECT 1 FROM sys.partition_range_values AS prv " +
            "INNER JOIN sys.partition_functions AS pf ON pf.function_id = prv.function_id " +
            "WHERE pf.name = N'pf_EventsMonth' AND prv.value = CONVERT(sql_variant, CONVERT(datetime2(3), '2026-10-01')))\n" +
            "BEGIN\n" +
            "    ALTER PARTITION SCHEME [ps_Archive] NEXT USED [FG_Archive];\n" +
            "    ALTER PARTITION SCHEME [ps_Events] NEXT USED [PRIMARY];\n" +
            "    ALTER PARTITION FUNCTION [pf_EventsMonth]() SPLIT RANGE ('2026-10-01');\n" +
            "END;",
            op.Sql);
    }

    [Fact]
    public void AddedBoundary_NamesNextUsedOnASchemeTheSameMigrationDrops()
    {
        // The scheme drop runs after the split, so at split time the dropped scheme is still bound to the
        // function and still needs its NEXT USED marker — error 7707 without it, once an earlier split has
        // consumed the marker ALL TO left behind.
        var grown = Months with { Boundaries = [.. Months.Boundaries, "'2026-10-01'"] };
        var dropped = new PartitionSchemeDefinition("ps_Old", "pf_EventsMonth", "FG_Old");

        var ops = PartitionDiff.Compute(Layout([Months], [Events, dropped]), Layout([grown], [Events]));

        var split = Assert.Single(ops, o => o.Kind == PartitionOpKind.SplitRange);
        Assert.Contains("ALTER PARTITION SCHEME [ps_Events] NEXT USED [PRIMARY];", split.Sql);
        Assert.Contains("ALTER PARTITION SCHEME [ps_Old] NEXT USED [FG_Old];", split.Sql);
        Assert.Equal(PartitionOpKind.DropScheme, ops[^1].Kind);
    }

    [Fact]
    public void RemovedBoundary_Merges()
    {
        var shrunk = Months with { Boundaries = ["'2026-09-01'"] };

        var op = Assert.Single(PartitionDiff.Compute(Layout([Months], [Events]), Layout([shrunk], [Events])));

        Assert.Equal(PartitionOpKind.MergeRange, op.Kind);
        Assert.Equal(
            "IF EXISTS (SELECT 1 FROM sys.partition_range_values AS prv " +
            "INNER JOIN sys.partition_functions AS pf ON pf.function_id = prv.function_id " +
            "WHERE pf.name = N'pf_EventsMonth' AND prv.value = CONVERT(sql_variant, CONVERT(datetime2(3), '2026-08-01')))\n" +
            "    ALTER PARTITION FUNCTION [pf_EventsMonth]() MERGE RANGE ('2026-08-01');",
            op.Sql);
    }

    [Fact]
    public void MovedBoundary_MergesBeforeItSplits()
    {
        var moved = Months with { Boundaries = ["'2026-08-15'", "'2026-09-01'"] };

        var ops = PartitionDiff.Compute(Layout([Months], [Events]), Layout([moved], [Events]));

        Assert.Equal([PartitionOpKind.MergeRange, PartitionOpKind.SplitRange], ops.Select(o => o.Kind));
    }

    [Fact]
    public void Symmetric_ASplitGoingUpIsAMergeComingDown()
    {
        var grown = Months with { Boundaries = [.. Months.Boundaries, "'2026-10-01'"] };
        var before = Layout([Months], [Events]);
        var after = Layout([grown], [Events]);

        Assert.Equal(PartitionOpKind.SplitRange, Assert.Single(PartitionDiff.Compute(before, after)).Kind);
        Assert.Equal(PartitionOpKind.MergeRange, Assert.Single(PartitionDiff.Compute(after, before)).Kind);
    }

    [Fact]
    public void UnchangedLayout_EmitsNothing()
        => Assert.Empty(PartitionDiff.Compute(Layout([Months], [Events]), Layout([Months], [Events])));

    [Fact]
    public void NamesCompareTheWaySqlServerComparesThem()
    {
        // Object names are case-insensitive under SQL Server's default collation; treating PF and pf as two
        // functions would try to create the second over the first.
        var renamedCase = Layout(
            [Months with { Name = "PF_EVENTSMONTH" }],
            [Events with { Name = "PS_EVENTS", FunctionName = "PF_EVENTSMONTH" }]);

        Assert.Empty(PartitionDiff.Compute(Layout([Months], [Events]), renamedCase));
    }

    [Fact]
    public void ChangedType_IsRefused()
    {
        var retyped = Months with { SqlType = "date" };

        var ex = Assert.Throws<NotSupportedException>(() => PartitionDiff.Compute(Layout([Months]), Layout([retyped])));
        Assert.Contains("pf_EventsMonth", ex.Message);
        Assert.Contains("by hand", ex.Message);
    }

    [Fact]
    public void ChangedRange_IsRefused()
    {
        var flipped = Months with { Range = PartitionRange.Left };

        Assert.Throws<NotSupportedException>(() => PartitionDiff.Compute(Layout([Months]), Layout([flipped])));
    }

    [Fact]
    public void ChangedSchemeFilegroup_IsRefused()
    {
        var moved = Events with { Filegroup = "FG_Events" };

        var ex = Assert.Throws<NotSupportedException>(
            () => PartitionDiff.Compute(Layout([Months], [Events]), Layout([Months], [moved])));
        Assert.Contains("ps_Events", ex.Message);
    }

    [Fact]
    public void ChangedSchemeFunction_IsRefused()
    {
        var other = new PartitionFunctionDefinition("pf_Other", "datetime2(3)", PartitionRange.Right, []);
        var remapped = Events with { FunctionName = "pf_Other" };

        Assert.Throws<NotSupportedException>(
            () => PartitionDiff.Compute(Layout([Months, other], [Events]), Layout([Months, other], [remapped])));
    }

    [Fact]
    public void BracketsInNames_AreEscaped()
    {
        var odd = new PartitionFunctionDefinition("pf]odd", "int", PartitionRange.Right, ["1"]);

        var op = Assert.Single(PartitionDiff.Compute(PartitionLayout.Empty, Layout([odd])));

        Assert.Contains("CREATE PARTITION FUNCTION [pf]]odd] (int)", op.Sql);
        Assert.Contains("WHERE name = N'pf]odd'", op.Sql);
    }
}
