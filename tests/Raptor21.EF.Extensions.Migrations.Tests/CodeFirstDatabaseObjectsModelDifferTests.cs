using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Xunit;

namespace Raptor21.EF.Extensions.Migrations.Tests;

/// <summary>
/// Drives <see cref="CodeFirstDatabaseObjectsModelDiffer"/> end to end through real <see cref="DbContext"/>
/// models: the order operations come out in, the annotations a placed table's <c>CreateTableOperation</c>
/// carries, the agreement between the two entry points, and every refusal the differ makes at
/// <c>migrations add</c> so that it never has to be made at <c>database update</c>.
/// </summary>
public class CodeFirstDatabaseObjectsModelDifferTests
{
    private const string ProcedureName = "dbo.CountEvents";
    private const string Procedure = "CREATE OR ALTER PROCEDURE dbo.CountEvents AS\nSELECT COUNT(*) FROM dbo.Events\n";
    private const string EditedProcedure = "CREATE OR ALTER PROCEDURE dbo.CountEvents AS\nSELECT COUNT(1) FROM dbo.Events\n";

    [Fact]
    public void NewPartitionedTable_OperationsArriveInTheOrderTheDatabaseNeeds()
    {
        var operations = Diff<Baseline, PartitionedEvents>();

        // Function, then the scheme that maps it, before anything EF produced.
        Assert.Contains(
            "CREATE PARTITION FUNCTION [pf_EventsMonth] (datetime2(3)) AS RANGE RIGHT FOR VALUES ('20260801', '20260901')",
            Sql(operations[0]));
        Assert.Contains("CREATE PARTITION SCHEME [ps_Events] AS PARTITION [pf_EventsMonth] ALL TO ([PRIMARY])", Sql(operations[1]));

        // The table's operation carries the scheme and the COLUMN — the entity declared the property
        // OccurredAt, which HasColumnName maps to OccurredAtUtc, and the SQL generator only sees operations.
        var createTable = Assert.Single(operations.OfType<CreateTableOperation>(), o => o.Name == "Events");
        Assert.Equal("dbo", createTable.Schema);
        Assert.Equal("ps_Events", createTable[CodeFirstAnnotations.PartitionScheme]);
        Assert.Equal("OccurredAtUtc", createTable[CodeFirstAnnotations.PartitionColumn]);

        // The columnstore index immediately behind its table, and every nonclustered index after it, so the
        // nonclustered indexes are built once, over the columnstore, rather than over a heap and rebuilt.
        var tableAt = operations.IndexOf(createTable);
        Assert.Contains("CREATE CLUSTERED COLUMNSTORE INDEX [cci_Events] ON [dbo].[Events]", Sql(operations[tableAt + 1]));

        var indexes = operations.OfType<CreateIndexOperation>().ToList();
        Assert.NotEmpty(indexes);
        Assert.All(indexes, index => Assert.True(operations.IndexOf(index) > tableAt + 1));

        // Procedures last among the creates: one may read the table and its index.
        var procedure = Assert.Single(operations, o => Sql(o).Contains("CREATE OR ALTER PROCEDURE", StringComparison.Ordinal));
        Assert.True(operations.IndexOf(procedure) > operations.IndexOf(indexes[^1]));
    }

    [Fact]
    public void NullSource_IsAProjectWithNoSnapshotYet()
    {
        // Migrator.HasPendingModelChanges reads ModelSnapshot?.Model, so the source really is null before the
        // first migration; the partition layout and the table layout both have to read an absent model as empty.
        using var target = new PartitionedEvents();
        var differ = Differ(target);
        var model = RelationalModel(target);

        var operations = differ.GetDifferences(null, model);

        Assert.Contains("CREATE PARTITION FUNCTION", Sql(operations[0]));
        Assert.True(differ.HasDifferences(null, model));

        // An unplaced table in the same model is left exactly as EF made it.
        var widget = Assert.Single(operations.OfType<CreateTableOperation>(), o => o.Name == "Widget");
        Assert.Null(widget[CodeFirstAnnotations.PartitionScheme]);
        Assert.Null(widget[CodeFirstAnnotations.PartitionColumn]);
    }

    [Theory]
    [InlineData(typeof(Baseline), typeof(PartitionedEvents), true)]
    [InlineData(typeof(PartitionedEvents), typeof(Baseline), true)]
    [InlineData(typeof(PartitionedEvents), typeof(PartitionedEventsTwin), false)]
    [InlineData(typeof(PartitionedEvents), typeof(PartitionedEventsWithExtraMonth), true)]
    [InlineData(typeof(PartitionedEvents), typeof(PartitionedEventsWithoutColumnstore), true)]
    [InlineData(typeof(PartitionedEvents), typeof(PartitionedEventsRenamedColumnstore), true)]
    [InlineData(typeof(Baseline), typeof(BaselineTwin), false)]
    public void HasDifferences_AnswersExactlyWhatGetDifferencesProduces(Type sourceType, Type targetType, bool expectDifference)
    {
        using var source = (DbContext)Activator.CreateInstance(sourceType)!;
        using var target = (DbContext)Activator.CreateInstance(targetType)!;

        var differ = Differ(source);
        var sourceModel = RelationalModel(source);
        var targetModel = RelationalModel(target);

        var operations = differ.GetDifferences(sourceModel, targetModel).Count;

        Assert.Equal(expectDifference, operations > 0);

        // The property that broke once for procedures and is held for every object kind here: the cheap
        // entry point answers what the expensive one produces, whatever the diff rule becomes.
        Assert.Equal(operations > 0, differ.HasDifferences(sourceModel, targetModel));
    }

    [Fact]
    public void AddedBoundary_IsASplitBeforeEverythingElse_AndAMergeOnTheWayDown()
    {
        var up = Assert.Single(Diff<PartitionedEvents, PartitionedEventsWithExtraMonth>());
        Assert.Contains("ALTER PARTITION SCHEME [ps_Events] NEXT USED [PRIMARY];", Sql(up));
        Assert.Contains("ALTER PARTITION FUNCTION [pf_EventsMonth]() SPLIT RANGE ('20261001');", Sql(up));

        var down = Assert.Single(Diff<PartitionedEventsWithExtraMonth, PartitionedEvents>());
        Assert.Contains("ALTER PARTITION FUNCTION [pf_EventsMonth]() MERGE RANGE ('20261001');", Sql(down));
    }

    [Fact]
    public void ColumnstoreOnAnExistingTable_IsCreatedBeforeProceduresAndDroppedBeforeEverythingElse()
    {
        // The two variants also differ in the procedure body, so each direction carries a procedure operation
        // to order against. Creates come before procedures because a procedure may read the index; drops come
        // FIRST, before anything EF produced, because the direction that removes the index is the direction
        // that may re-create a clustered primary key, which SQL Server refuses while the columnstore stands
        // (error 1902).
        var added = Diff<PartitionedEventsWithoutColumnstore, PartitionedEvents>();
        Assert.Collection(
            added,
            op => Assert.StartsWith("IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'cci_Events'", Sql(op), StringComparison.Ordinal),
            op => Assert.StartsWith("EXEC(N'CREATE OR ALTER PROCEDURE", Sql(op), StringComparison.Ordinal));

        var removed = Diff<PartitionedEvents, PartitionedEventsWithoutColumnstore>();
        Assert.Collection(
            removed,
            op => Assert.Equal("DROP INDEX IF EXISTS [cci_Events] ON [dbo].[Events];", Sql(op)),
            op => Assert.StartsWith("EXEC(N'CREATE OR ALTER PROCEDURE", Sql(op), StringComparison.Ordinal));
    }

    [Fact]
    public void RenamedColumnstore_IsAnSpRename()
    {
        var op = Assert.Single(Diff<PartitionedEvents, PartitionedEventsRenamedColumnstore>());

        Assert.Contains("EXEC sp_rename N'[dbo].[Events].[cci_Events]', N'cci_Events_v2', N'INDEX';", Sql(op));
    }

    [Fact]
    public void GivingAnExistingTableAColumnstore_CreatesAfterThePrimaryKeySwap_AndDropsBeforeItOnTheWayDown()
    {
        // Making a live table columnstore means re-clustering it: EF scaffolds IsClustered(false) as a
        // DropPrimaryKey at the front of the migration and an AddPrimaryKey at the back. Up must create the
        // index after the clustered key is gone; Down must drop it before the clustered key returns, or SQL
        // Server refuses the second clustered index with error 1902.
        var up = Diff<EventsWithClusteredKey, EventsWithColumnstoreInsteadOfClusteredKey>();
        var upAddKey = up.FindIndex(o => o is AddPrimaryKeyOperation);
        var upCreate = Assert.Single(up, o => Sql(o).Contains("CREATE CLUSTERED COLUMNSTORE INDEX", StringComparison.Ordinal));
        Assert.True(upAddKey >= 0);
        Assert.True(up.IndexOf(upCreate) > upAddKey);

        var down = Diff<EventsWithColumnstoreInsteadOfClusteredKey, EventsWithClusteredKey>();
        var downAddKey = down.FindIndex(o => o is AddPrimaryKeyOperation);
        var downDrop = Assert.Single(down, o => Sql(o).StartsWith("DROP INDEX", StringComparison.Ordinal));
        Assert.True(downAddKey >= 0);
        Assert.True(down.IndexOf(downDrop) < downAddKey);
    }

    [Fact]
    public void ColumnstoreAndANewIndexOnAnExistingTable_TheIndexIsBuiltOverTheColumnstore()
    {
        // EF sorts a new CreateIndexOperation into the tail of the migration; a columnstore appended after
        // that tail would arrive after the index and force its rebuild. The create is inserted before the
        // table's first new index instead — legal even before the AddPrimaryKey, because the old clustered
        // key was dropped at the front and the table is a heap at that point.
        var ops = Diff<EventsWithClusteredKey, EventsWithColumnstoreAndExtraIndex>();

        var create = Assert.Single(ops, o => Sql(o).Contains("CREATE CLUSTERED COLUMNSTORE INDEX", StringComparison.Ordinal));
        var firstIndex = ops.FindIndex(o => o is CreateIndexOperation);
        Assert.True(firstIndex >= 0);
        Assert.True(ops.IndexOf(create) < firstIndex);
        Assert.True(ops.IndexOf(create) > ops.FindIndex(o => o is DropPrimaryKeyOperation));
    }

    [Fact]
    public void RenamedTableKeepingItsLayout_EmitsNoColumnstoreOperationAtAll()
    {
        // EF matches the table through its entity type and scaffolds a RenameTableOperation; the differ makes
        // the same match, so the index that merely followed its table is not read as dropped and created.
        var ops = Diff<PartitionedEvents, PartitionedEventsRenamedTable>();

        Assert.Contains(ops, o => o is RenameTableOperation);
        Assert.DoesNotContain(ops, o => Sql(o).Contains("COLUMNSTORE", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(ops, o => Sql(o).StartsWith("DROP INDEX", StringComparison.Ordinal));
    }

    [Fact]
    public void RenamedTableLosingItsColumnstore_DropsTheIndexUnderItsOldName_BeforeTheRename()
    {
        var ops = Diff<PartitionedEvents, PartitionedEventsRenamedTableWithoutColumnstore>();

        var drop = Assert.Single(ops, o => Sql(o).StartsWith("DROP INDEX", StringComparison.Ordinal));
        Assert.Equal("DROP INDEX IF EXISTS [cci_Events] ON [dbo].[Events];", Sql(drop));
        Assert.True(ops.IndexOf(drop) < ops.FindIndex(o => o is RenameTableOperation));
    }

    [Fact]
    public void OwnedTypeSharingTheTable_DoesNotReadAsASecondClusteredKey()
    {
        // The owned type's own key maps onto the same primary key constraint as a second IKey carrying no
        // clustering annotation of its own. Clustering is read the provider's way — first mapped key, falling
        // back to the shared-object root — so "no annotation" is not "clustered", and this model deploys.
        var ops = Diff<Baseline, ColumnstoreWithOwnedType>();

        Assert.Contains(ops, o => Sql(o).Contains("CREATE CLUSTERED COLUMNSTORE INDEX [cci_Events]", StringComparison.Ordinal));
    }

    [Fact]
    public void KeylessTable_TakesItsColumnstoreStraightBehindTheCreateTable()
    {
        // HasNoKey() is one of the two shapes the clustered-key refusal recommends; its happy path must hold.
        var ops = Diff<Baseline, KeylessColumnstore>();

        var createTable = Assert.Single(ops.OfType<CreateTableOperation>(), o => o.Name == "Events");
        Assert.Contains("CREATE CLUSTERED COLUMNSTORE INDEX [cci_Events]", Sql(ops[ops.IndexOf(createTable) + 1]));
    }

    [Fact]
    public void TableExcludedFromMigrations_IsLeftAloneEntirely()
    {
        // The table is managed outside migrations, and so is whatever sits on it: no operation and no
        // refusal — the clustered default key next to the declared columnstore is the outside owner's call.
        Assert.Empty(Diff<Baseline, ExcludedTableWithColumnstore>());
    }

    [Fact]
    public void DroppedTable_TakesItsIndexWithIt_AndItsSchemeAndFunctionGoLast()
    {
        var operations = Diff<PartitionedEvents, Baseline>();

        Assert.Single(operations.OfType<DropTableOperation>(), o => o.Name == "Events");

        // DROP TABLE removes the index; a DROP INDEX against the dropped table would fail the migration.
        Assert.DoesNotContain(operations, o => Sql(o).Contains("DROP INDEX", StringComparison.Ordinal));

        // Scheme before function, both after the procedure drop and after the table that sat on them.
        Assert.Contains("DROP PARTITION SCHEME [ps_Events]", Sql(operations[^2]));
        Assert.Contains("DROP PARTITION FUNCTION [pf_EventsMonth]", Sql(operations[^1]));
        Assert.True(operations.FindIndex(o => Sql(o).StartsWith("DROP PROCEDURE", StringComparison.Ordinal)) < operations.Count - 2);
    }

    [Theory]
    [InlineData(typeof(PartitionedEvents), typeof(PartitionedEventsOnAnotherColumn))]
    [InlineData(typeof(PartitionedEvents), typeof(EventsNotPartitioned))]
    [InlineData(typeof(EventsNotPartitioned), typeof(PartitionedEvents))]
    [InlineData(typeof(PartitionedEvents), typeof(RenamedTableOffTheScheme))]
    [InlineData(typeof(EventsNotPartitioned), typeof(PartitionedEventsRenamedTable))]
    public void ChangingAnExistingTablesPlacement_IsRefusedByBothEntryPoints(Type sourceType, Type targetType)
    {
        using var source = (DbContext)Activator.CreateInstance(sourceType)!;
        using var target = (DbContext)Activator.CreateInstance(targetType)!;

        var differ = Differ(source);
        var sourceModel = RelationalModel(source);
        var targetModel = RelationalModel(target);

        // Both, because a HasDifferences that answered true where GetDifferences throws would let
        // has-pending-model-changes report a difference the scaffold then cannot produce.
        var ex = Assert.Throws<NotSupportedException>(() => differ.GetDifferences(sourceModel, targetModel));
        Assert.Contains("dbo.Events", ex.Message);
        Assert.Contains("by hand", ex.Message);
        Assert.Throws<NotSupportedException>(() => differ.HasDifferences(sourceModel, targetModel));
    }

    [Theory]
    [InlineData(typeof(UndeclaredScheme), "HasPartitionScheme(\"ps_missing\"")]
    [InlineData(typeof(SchemeOnUndeclaredFunction), "HasPartitionFunction(\"pf_missing\"")]
    [InlineData(typeof(ColumnstoreWithClusteredKey), "IsClustered(false)")]
    [InlineData(typeof(UniqueIndexWithoutPartitionColumn), "unique index 'IX_Events_Kind'")]
    [InlineData(typeof(PartitionByUnmappedProperty), "'Missing'")]
    [InlineData(typeof(DifferWithoutGenerator), "UseCodeFirstDatabaseObjects()")]
    [InlineData(typeof(SharedTableDisagreeingOnPlacement), "on the root type")]
    [InlineData(typeof(PkWithoutPartitionColumn), "primary key")]
    [InlineData(typeof(AlternateKeyWithoutPartitionColumn), "unique constraint")]
    [InlineData(typeof(ClusteredAlternateKeyNextToColumnstore), "alternate key")]
    [InlineData(typeof(ClusteredIndexNextToColumnstore), "Remove IsClustered()")]
    [InlineData(typeof(FunctionDeclaredInTwoCasings), "case-insensitively")]
    public void AModelThatCannotDeploy_IsRefusedAtScaffoldTime(Type targetType, string expectedInMessage)
    {
        using var source = new Baseline();
        using var target = (DbContext)Activator.CreateInstance(targetType)!;

        var differ = Differ(target);
        var sourceModel = RelationalModel(source);
        var targetModel = RelationalModel(target);

        var ex = Assert.Throws<InvalidOperationException>(() => differ.GetDifferences(sourceModel, targetModel));
        Assert.Contains(expectedInMessage, ex.Message);
        Assert.Throws<InvalidOperationException>(() => differ.HasDifferences(sourceModel, targetModel));
    }

    [Fact]
    public void ColumnstoreWithoutPlacement_NeedsOnlyTheDiffer()
    {
        // A columnstore index is a SqlOperation and any generator runs it; the generator guard is for the ON
        // clause alone. A consumer with the single old ReplaceService keeps this working.
        var operations = Diff<Baseline, ColumnstoreOnlyWithOldRegistration>();

        var createTable = Assert.Single(operations.OfType<CreateTableOperation>(), o => o.Name == "Events");
        Assert.Contains("CREATE CLUSTERED COLUMNSTORE INDEX [cci_Events]", Sql(operations[operations.IndexOf(createTable) + 1]));
    }

    [Fact]
    public void OnPartitionScheme_RefusesAnExpressionThatIsNotAPropertyAccess()
    {
        var builder = new ModelBuilder().Entity<Event>();

        Assert.Throws<ArgumentException>(() => builder.OnPartitionScheme("ps", e => e.Id + 1));
        Assert.Throws<ArgumentException>(() => builder.OnPartitionScheme("ps", e => new { e.Id }));
    }

    private static List<MigrationOperation> Diff<TSource, TTarget>()
        where TSource : DbContext, new()
        where TTarget : DbContext, new()
    {
        using var source = new TSource();
        using var target = new TTarget();

        return Differ(target).GetDifferences(RelationalModel(source), RelationalModel(target)).ToList();
    }

    private static string Sql(MigrationOperation operation) => (operation as SqlOperation)?.Sql ?? string.Empty;

    private static IMigrationsModelDiffer Differ(DbContext context)
    {
        var differ = context.GetService<IMigrationsModelDiffer>();

        // Without this, a registration that quietly stopped taking effect would leave the ordering assertions
        // failing for the wrong reason and the agreement assertions passing against EF's own differ.
        return Assert.IsAssignableFrom<CodeFirstDatabaseObjectsModelDiffer>(differ);
    }

    private static IRelationalModel RelationalModel(DbContext context)
        => context.GetService<IDesignTimeModel>().Model.GetRelationalModel();

    // EF caches the built model per DbContext TYPE, so every variant is its own subclass; see the note in
    // StoredProcedureModelDifferTests. Do not collapse them into one context parameterised by a constructor.
    private abstract class Variant : DbContext
    {
        protected sealed override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => Configure(optionsBuilder.UseSqlServer("Server=host-that-must-never-be-contacted;Database=none"));

        protected virtual void Configure(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.UseCodeFirstDatabaseObjects();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema("dbo");
            modelBuilder.Entity<Widget>();
        }

        protected static void DeclareEventsLayout(ModelBuilder modelBuilder, int months = 2)
        {
            modelBuilder.HasPartitionFunction(
                "pf_EventsMonth", "datetime2(3)", PartitionRange.Right, PartitionBoundaries.Monthly(new DateOnly(2026, 8, 1), months));
            modelBuilder.HasPartitionScheme("ps_Events", "pf_EventsMonth");
        }

        // The shape every partitioned variant shares: a nonclustered composite key that includes the
        // partitioning column (SQL Server requires it in every unique index aligned to the scheme), a plain
        // nonclustered index to order against, and a column name that differs from the property name so the
        // property-to-column translation is visible.
        protected static EntityTypeBuilder<Event> Events(ModelBuilder modelBuilder)
        {
            var events = modelBuilder.Entity<Event>();
            events.ToTable("Events");
            events.Property(e => e.OccurredAt).HasColumnName("OccurredAtUtc").HasColumnType("datetime2(3)");
            events.HasKey(e => new { e.Id, e.OccurredAt }).IsClustered(false);
            events.HasIndex(e => e.Kind);
            return events;
        }

        protected static void Script(ModelBuilder modelBuilder, string sql = Procedure)
            => modelBuilder.Model.SetAnnotation(StoredProcedureScript.AnnotationPrefix + ProcedureName, sql);
    }

    private sealed class Baseline : Variant
    {
    }

    private sealed class BaselineTwin : Variant
    {
    }

    private sealed class PartitionedEvents : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            DeclareEventsLayout(modelBuilder);
            Events(modelBuilder)
                .OnPartitionScheme("ps_Events", e => e.OccurredAt)
                .HasClusteredColumnstoreIndex("cci_Events");
            Script(modelBuilder);
        }
    }

    private sealed class PartitionedEventsTwin : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            DeclareEventsLayout(modelBuilder);
            Events(modelBuilder)
                .OnPartitionScheme("ps_Events", e => e.OccurredAt)
                .HasClusteredColumnstoreIndex("cci_Events");
            Script(modelBuilder);
        }
    }

    private sealed class PartitionedEventsWithExtraMonth : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            DeclareEventsLayout(modelBuilder, months: 3);
            Events(modelBuilder)
                .OnPartitionScheme("ps_Events", e => e.OccurredAt)
                .HasClusteredColumnstoreIndex("cci_Events");
            Script(modelBuilder);
        }
    }

    private sealed class PartitionedEventsWithoutColumnstore : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            DeclareEventsLayout(modelBuilder);
            Events(modelBuilder).OnPartitionScheme("ps_Events", e => e.OccurredAt);
            Script(modelBuilder, EditedProcedure);
        }
    }

    private sealed class PartitionedEventsRenamedColumnstore : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            DeclareEventsLayout(modelBuilder);
            Events(modelBuilder)
                .OnPartitionScheme("ps_Events", e => e.OccurredAt)
                .HasClusteredColumnstoreIndex("cci_Events_v2");
            Script(modelBuilder);
        }
    }

    private sealed class PartitionedEventsOnAnotherColumn : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            DeclareEventsLayout(modelBuilder);
            Events(modelBuilder)
                .OnPartitionScheme("ps_Events", nameof(Event.Id))
                .HasClusteredColumnstoreIndex("cci_Events");
            Script(modelBuilder);
        }
    }

    private sealed class EventsNotPartitioned : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            DeclareEventsLayout(modelBuilder);
            Events(modelBuilder).HasClusteredColumnstoreIndex("cci_Events");
            Script(modelBuilder);
        }
    }

    private sealed class UndeclaredScheme : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            DeclareEventsLayout(modelBuilder);
            Events(modelBuilder).OnPartitionScheme("ps_missing", e => e.OccurredAt);
        }
    }

    private sealed class SchemeOnUndeclaredFunction : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.HasPartitionScheme("ps_Events", "pf_missing");
            Events(modelBuilder).OnPartitionScheme("ps_Events", e => e.OccurredAt);
        }
    }

    private sealed class ColumnstoreWithClusteredKey : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            // EF's default: a clustered primary key on Id, which is the second clustered index.
            modelBuilder.Entity<Event>().ToTable("Events").HasClusteredColumnstoreIndex("cci_Events");
        }
    }

    private sealed class UniqueIndexWithoutPartitionColumn : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            DeclareEventsLayout(modelBuilder);
            var events = Events(modelBuilder).OnPartitionScheme("ps_Events", e => e.OccurredAt);
            events.HasIndex(e => e.Kind).IsUnique();
        }
    }

    private sealed class PartitionByUnmappedProperty : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            DeclareEventsLayout(modelBuilder);
            Events(modelBuilder).OnPartitionScheme("ps_Events", "Missing");
        }
    }

    private sealed class DifferWithoutGenerator : Variant
    {
        // The registration a procedure-only consumer has today. It keeps working until a table is placed on a
        // scheme, at which point only the generator can write the ON clause and the differ must say so.
        protected override void Configure(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.ReplaceService<IMigrationsModelDiffer, StoredProcedureModelDiffer>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            DeclareEventsLayout(modelBuilder);
            Events(modelBuilder).OnPartitionScheme("ps_Events", e => e.OccurredAt);
        }
    }

    private sealed class ColumnstoreOnlyWithOldRegistration : Variant
    {
        protected override void Configure(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder.ReplaceService<IMigrationsModelDiffer, StoredProcedureModelDiffer>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            Events(modelBuilder).HasClusteredColumnstoreIndex("cci_Events");
        }
    }

    // The clustering swap pair: the same table with the composite key clustered and no columnstore, and with
    // the key nonclustered and the columnstore in its place. Diffing them is the "make a live table
    // columnstore" migration, whose base operations are the DropPrimaryKey / AddPrimaryKey swap.
    private sealed class EventsWithClusteredKey : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            Events(modelBuilder).HasKey(e => new { e.Id, e.OccurredAt }).IsClustered();
        }
    }

    private sealed class EventsWithColumnstoreInsteadOfClusteredKey : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            Events(modelBuilder).HasClusteredColumnstoreIndex("cci_Events");
        }
    }

    private sealed class EventsWithColumnstoreAndExtraIndex : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            var events = Events(modelBuilder);
            events.HasIndex(e => e.OccurredAt);
            events.HasClusteredColumnstoreIndex("cci_Events");
        }
    }

    private sealed class PartitionedEventsRenamedTable : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            DeclareEventsLayout(modelBuilder);
            var events = Events(modelBuilder);
            events.ToTable("EventLog");
            events.OnPartitionScheme("ps_Events", e => e.OccurredAt);
            events.HasClusteredColumnstoreIndex("cci_Events");
            Script(modelBuilder);
        }
    }

    private sealed class PartitionedEventsRenamedTableWithoutColumnstore : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            DeclareEventsLayout(modelBuilder);
            var events = Events(modelBuilder);
            events.ToTable("EventLog");
            events.OnPartitionScheme("ps_Events", e => e.OccurredAt);
            Script(modelBuilder);
        }
    }

    private sealed class RenamedTableOffTheScheme : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            DeclareEventsLayout(modelBuilder);
            var events = Events(modelBuilder);
            events.ToTable("EventLog");
            events.HasClusteredColumnstoreIndex("cci_Events");
            Script(modelBuilder);
        }
    }

    private sealed class SharedTableDisagreeingOnPlacement : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            DeclareEventsLayout(modelBuilder);
            Events(modelBuilder).OnPartitionScheme("ps_Events", e => e.OccurredAt);

            // Table splitting: EventDetail rides the same Events table and names a different column.
            var details = modelBuilder.Entity<EventDetail>();
            details.ToTable("Events");
            details.Property(d => d.OccurredAt).HasColumnName("OccurredAtUtc").HasColumnType("datetime2(3)");
            details.HasKey(d => new { d.Id, d.OccurredAt }).IsClustered(false);
            details.HasOne<Event>().WithOne().HasForeignKey<EventDetail>(d => new { d.Id, d.OccurredAt });
            details.OnPartitionScheme("ps_Events", d => d.Id);
        }
    }

    private sealed class PkWithoutPartitionColumn : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            DeclareEventsLayout(modelBuilder);
            var events = modelBuilder.Entity<Event>();
            events.ToTable("Events");
            events.Property(e => e.OccurredAt).HasColumnName("OccurredAtUtc").HasColumnType("datetime2(3)");
            events.HasKey(e => e.Id).IsClustered(false);
            events.OnPartitionScheme("ps_Events", e => e.OccurredAt);
        }
    }

    private sealed class AlternateKeyWithoutPartitionColumn : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            DeclareEventsLayout(modelBuilder);
            var events = Events(modelBuilder);
            events.OnPartitionScheme("ps_Events", e => e.OccurredAt);
            events.HasAlternateKey(e => e.Kind);
        }
    }

    private sealed class ClusteredAlternateKeyNextToColumnstore : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            var events = Events(modelBuilder);
            events.HasAlternateKey(e => e.Kind).IsClustered();
            events.HasClusteredColumnstoreIndex("cci_Events");
        }
    }

    private sealed class ClusteredIndexNextToColumnstore : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            var events = Events(modelBuilder);
            events.HasIndex(e => e.OccurredAt).IsClustered();
            events.HasClusteredColumnstoreIndex("cci_Events");
        }
    }

    private sealed class FunctionDeclaredInTwoCasings : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            DeclareEventsLayout(modelBuilder);
            modelBuilder.HasPartitionFunction(
                "PF_EVENTSMONTH", "datetime2(3)", PartitionRange.Right, PartitionBoundaries.Monthly(new DateOnly(2026, 8, 1), 2));
        }
    }

    private sealed class ColumnstoreWithOwnedType : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            var events = modelBuilder.Entity<EventWithOrigin>();
            events.ToTable("Events");
            events.HasKey(e => e.Id).IsClustered(false);
            events.OwnsOne(e => e.Origin);
            events.HasClusteredColumnstoreIndex("cci_Events");
        }
    }

    private sealed class KeylessColumnstore : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            var events = modelBuilder.Entity<Event>();
            events.ToTable("Events");
            events.HasNoKey();
            events.HasClusteredColumnstoreIndex("cci_Events");
        }
    }

    private sealed class ExcludedTableWithColumnstore : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);
            modelBuilder.Entity<Event>()
                .ToTable("Events", t => t.ExcludeFromMigrations())
                .HasClusteredColumnstoreIndex("cci_Events");
        }
    }

    private sealed class EventDetail
    {
        public long Id { get; set; }

        public DateTime OccurredAt { get; set; }

        public string? Note { get; set; }
    }

    private sealed class EventWithOrigin
    {
        public long Id { get; set; }

        public EventOrigin? Origin { get; set; }
    }

    private sealed class EventOrigin
    {
        public string? Source { get; set; }
    }

    private sealed class Widget
    {
        public int Id { get; set; }

        public string? Name { get; set; }
    }

    private sealed class Event
    {
        public long Id { get; set; }

        public DateTime OccurredAt { get; set; }

        public string? Kind { get; set; }
    }
}
