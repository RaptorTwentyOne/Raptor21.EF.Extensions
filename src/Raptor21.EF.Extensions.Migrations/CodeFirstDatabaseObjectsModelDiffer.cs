using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace Raptor21.EF.Extensions.Migrations;

/// <summary>
/// Extends EF Core's migration model differ with the code-first database objects this package declares:
/// stored procedures registered by <see cref="StoredProcedureModelExtensions.RegisterStoredProcedures"/>,
/// stored procedures and user-defined functions registered by
/// <see cref="DatabaseScriptModelExtensions.RegisterDatabaseScripts"/>,
/// partition functions and schemes, a table's placement on a scheme, clustered columnstore indexes, and
/// full-text catalogs and indexes. Each is diffed against the model snapshot next to EF's own table diff and
/// lands in the same migration, in an order the database accepts. EF invokes the differ once per direction,
/// so rollback follows from the same rules. Both entry points are overridden, so a change to any of these
/// objects is reported by <c>dotnet ef migrations has-pending-model-changes</c> as well as scaffolded by
/// <c>migrations add</c>.
/// </summary>
/// <remarks>
/// <para>
/// Register with <see cref="CodeFirstDbContextOptionsBuilderExtensions.UseCodeFirstDatabaseObjects(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder)"/>,
/// which also installs <see cref="CodeFirstDatabaseObjectsMigrationsSqlGenerator"/>; a table placed on a
/// partition scheme needs both, and the differ refuses to run without the generator rather than let the
/// table be created on the default filegroup in silence.
/// </para>
/// <para>
/// The operation order is: partition function creates, partition scheme creates, boundary merges and splits,
/// full-text catalog creates, full-text index drops, columnstore drops, EF's operations — each
/// <c>CreateTableOperation</c> for a placed table annotated with its scheme and column, a placed table's
/// columnstore index immediately behind its <c>CREATE TABLE</c> so that every nonclustered index EF creates
/// afterwards is built over it, and an existing table's new columnstore inserted before the first index this
/// migration adds to that table — remaining columnstore creates and renames on tables that already exist,
/// full-text index creates (after every index, because the key index must exist), user-defined function
/// creates, alters and kind-change re-creates, procedures, user-defined function drops, full-text catalog
/// drops, partition scheme drops, partition function drops. Every full-text statement is marked transaction-suppressed,
/// because SQL Server refuses full-text DDL inside a user transaction (error 574); EF commits the migration's
/// transaction before such a statement and opens a new one after it.
/// </para>
/// <para>
/// Subclassing <see cref="MigrationsModelDiffer"/> requires forwarding its internal-flagged dependencies; EF1001
/// is suppressed at project level.
/// </para>
/// </remarks>
public class CodeFirstDatabaseObjectsModelDiffer : MigrationsModelDiffer
{
    private readonly IMigrationsSqlGenerator _migrationsSqlGenerator;

    /// <summary>Creates the differ. Every parameter is resolved by EF's service provider.</summary>
    /// <param name="typeMappingSource">Forwarded to <see cref="MigrationsModelDiffer"/>.</param>
    /// <param name="migrationsAnnotationProvider">Forwarded to <see cref="MigrationsModelDiffer"/>.</param>
    /// <param name="relationalAnnotationProvider">Forwarded to <see cref="MigrationsModelDiffer"/>.</param>
    /// <param name="rowIdentityMapFactory">Forwarded to <see cref="MigrationsModelDiffer"/>.</param>
    /// <param name="commandBatchPreparerDependencies">Forwarded to <see cref="MigrationsModelDiffer"/>.</param>
    /// <param name="migrationsSqlGenerator">
    /// The generator EF will hand these operations to. Injected so that a model placing a table on a
    /// partition scheme can be refused up front when the generator is not one that writes the <c>ON</c>
    /// clause, instead of producing a migration that quietly creates the table on the default filegroup.
    /// </param>
    public CodeFirstDatabaseObjectsModelDiffer(
        IRelationalTypeMappingSource typeMappingSource,
        IMigrationsAnnotationProvider migrationsAnnotationProvider,
        IRelationalAnnotationProvider relationalAnnotationProvider,
        IRowIdentityMapFactory rowIdentityMapFactory,
        CommandBatchPreparerDependencies commandBatchPreparerDependencies,
        IMigrationsSqlGenerator migrationsSqlGenerator)
        : base(
            typeMappingSource,
            migrationsAnnotationProvider,
            relationalAnnotationProvider,
            rowIdentityMapFactory,
            commandBatchPreparerDependencies)
    {
        ArgumentNullException.ThrowIfNull(migrationsSqlGenerator);
        _migrationsSqlGenerator = migrationsSqlGenerator;
    }

    /// <summary>
    /// Produces the operations that turn <paramref name="source"/> into <paramref name="target"/>: the partition
    /// objects that must exist first, columnstore drops, EF's own operations with placement annotations and
    /// columnstore indexes attached, then procedures, then the partition objects being removed.
    /// </summary>
    /// <exception cref="InvalidOperationException">The target model's declarations disagree with each other or with the registered SQL generator.</exception>
    /// <exception cref="NotSupportedException">A change was requested that SQL Server cannot make in place.</exception>
    public override IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
    {
        // Plan first, then ask the base. The plan is where every validation and every NotSupported lives, and
        // running it before the base's topological sorts means a model this package refuses is refused before
        // the expensive half has been paid for.
        var plan = Plan(source, target);
        var tableOperations = base.GetDifferences(source, target);

        var operations = new List<MigrationOperation>(tableOperations.Count + plan.Count);

        foreach (var op in plan.Partition)
        {
            if (!op.IsDrop)
                operations.Add(Sql(op.Sql));
        }

        foreach (var op in plan.FullText)
        {
            if (op.Kind == FullTextOpKind.CreateCatalog)
                operations.Add(Sql(op.Sql, suppressTransaction: true));
        }

        // Full-text index drops run before EF's operations — and before the columnstore drops, which never
        // touch a full-text index but come next by convention — because an ALTER COLUMN on a full-text column
        // and a DROP INDEX of the key index are both refused while the full-text index stands (errors 7614 and
        // 3723). Rebuilt indexes below are dropped here as well.
        var rebuilt = RebuiltByKeyIndexDrop(tableOperations, plan);
        foreach (var op in plan.FullText)
        {
            if (op.Kind == FullTextOpKind.DropIndex)
                operations.Add(Sql(op.Sql, suppressTransaction: true));
        }

        foreach (var definition in rebuilt)
            operations.Add(Sql(FullTextDiff.DropIndex(definition).Sql, suppressTransaction: true));

        // Columnstore drops run before EF's operations. The direction that removes a live table's columnstore
        // is the direction that may re-create a clustered primary key — the Down of "give this table a
        // columnstore", whose Up dropped that key — and SQL Server refuses the second clustered index while
        // the columnstore still stands (error 1902); dropping first also spares ALTER COLUMN and friends a
        // rebuild of the row groups. A dropped table never reaches here, because Plan filters it out — DROP
        // TABLE takes the index with it — and the statement names the OLD table, which is the name the table
        // still has before any RenameTableOperation below has run.
        foreach (var op in plan.Columnstore)
        {
            if (op.Kind == ColumnstoreOpKind.Drop)
                operations.Add(Sql(op.Sql));
        }

        // A columnstore index on a table this migration creates goes straight behind the CREATE TABLE. EF's
        // Sort puts every CreateIndexOperation after every CreateTableOperation, so a SqlOperation appended at
        // the end would come after the nonclustered indexes — which would then be built as rowstore indexes
        // over a heap, and SQL Server would have to rebuild each of them when the columnstore arrived.
        var pendingCreates = new Dictionary<TableKey, ColumnstoreOp>();
        foreach (var op in plan.Columnstore)
        {
            if (op.Kind == ColumnstoreOpKind.Create)
                pendingCreates[new TableKey(op.Schema, op.Table)] = op;
        }

        foreach (var operation in tableOperations)
        {
            // The same reasoning for a table that already exists: an index this migration adds to it is
            // created after the table's new columnstore, so it is built once, over the columnstore. Any
            // clustered primary key is already gone at this point — EF sorts the DropPrimaryKey half of the
            // clustering swap to the front of the migration and the nonclustered AddPrimaryKey to the back —
            // so the columnstore create is legal here even before the AddPrimaryKey has run.
            if (operation is CreateIndexOperation index
                && pendingCreates.Remove(new TableKey(index.Schema, index.Table), out var beforeIndex))
                operations.Add(Sql(beforeIndex.Sql));

            operations.Add(operation);

            if (operation is not CreateTableOperation create)
                continue;

            var key = new TableKey(create.Schema, create.Name);
            if (plan.Placements.TryGetValue(key, out var placement))
            {
                // The entity carried the PROPERTY name; the operation carries the COLUMN name, because the SQL
                // generator that reads it has the operation and nothing else.
                create[CodeFirstAnnotations.PartitionScheme] = placement.Scheme;
                create[CodeFirstAnnotations.PartitionColumn] = placement.Column;
            }

            if (pendingCreates.Remove(key, out var columnstore))
                operations.Add(Sql(columnstore.Sql));
        }

        // Whatever is still pending is an index on an existing table that gains no new index in this
        // migration; it and the renames run after EF's operations, once a RenameTableOperation has given a
        // renamed table the new name these statements refer to.
        foreach (var op in plan.Columnstore)
        {
            if (op.Kind == ColumnstoreOpKind.Rename
                || (op.Kind == ColumnstoreOpKind.Create && pendingCreates.ContainsKey(new TableKey(op.Schema, op.Table))))
                operations.Add(Sql(op.Sql));
        }

        // Full-text index creates come after every index EF creates — the key index among them — and before
        // the procedures, because a procedure that reads the table with CONTAINS binds against the full-text
        // index at creation when the table already exists.
        foreach (var op in plan.FullText)
        {
            if (op.Kind == FullTextOpKind.CreateIndex)
                operations.Add(Sql(op.Sql, suppressTransaction: true));
        }

        foreach (var definition in rebuilt)
            operations.Add(Sql(FullTextDiff.CreateIndex(definition).Sql, suppressTransaction: true));

        // Function creates and alters come after every table and index (an inline table-valued function is
        // bound to the tables it reads when it is created) and before the procedures, which may call them. A
        // kind change is one Recreate statement, drop and create together, and belongs here too.
        foreach (var op in plan.Functions)
        {
            if (op.Kind != FunctionOpKind.Drop)
                operations.Add(Sql(op.Sql));
        }

        // Procedure operations run after table operations (a procedure may reference tables).
        foreach (var op in plan.Procedures)
            operations.Add(Sql(op.Sql));

        // Function drops come after the procedures, the mirror of the creates: a procedure the same migration
        // drops or rewrites stops referring to the function before the function goes. Procedures are not
        // schema-bound, so SQL Server would not refuse the other order - this one simply never leaves a
        // deployed procedure calling a function that no longer exists, even between two statements.
        foreach (var op in plan.Functions)
        {
            if (op.Kind == FunctionOpKind.Drop)
                operations.Add(Sql(op.Sql));
        }

        // A catalog is dropped after every index that lived in it, which the drops above have already done.
        foreach (var op in plan.FullText)
        {
            if (op.Kind == FullTextOpKind.DropCatalog)
                operations.Add(Sql(op.Sql, suppressTransaction: true));
        }

        // Schemes before functions, and both after every table that sat on them has been dropped: Compute
        // already orders the drops that way.
        foreach (var op in plan.Partition)
        {
            if (op.IsDrop)
                operations.Add(Sql(op.Sql));
        }

        return operations;
    }

    // A full-text index whose declaration did not change can still need a rebuild: when EF drops the index it
    // is keyed on — a DropPrimaryKey for an IsClustered(false) swap, a DropUniqueConstraint, a DropIndex to
    // re-create the index with other columns — SQL Server refuses that drop while the full-text index is keyed
    // on it (error 3723). Such an index is dropped ahead of EF's operations and created again behind them.
    // Only GetDifferences can see EF's operations, and only a model EF already finds different reaches here,
    // so HasDifferences answers true in every case this adds an operation to.
    private static List<FullTextIndexDefinition> RebuiltByKeyIndexDrop(IReadOnlyList<MigrationOperation> tableOperations, Plan plan)
    {
        var rebuilt = new List<FullTextIndexDefinition>();
        if (plan.FullTextTargets.Count == 0)
            return rebuilt;

        var alreadyCreated = new HashSet<string>(
            plan.FullText.Where(o => o.Kind == FullTextOpKind.CreateIndex).Select(o => o.Name),
            StringComparer.OrdinalIgnoreCase);

        foreach (var (key, definition) in plan.FullTextTargets)
        {
            if (alreadyCreated.Contains(key.ToString()))
                continue;

            if (tableOperations.Any(o => DropsKeyIndex(o, key, definition.KeyIndex)))
                rebuilt.Add(definition);
        }

        return rebuilt;
    }

    private static bool DropsKeyIndex(MigrationOperation operation, TableKey table, string keyIndex)
    {
        var (schema, name, dropped) = operation switch
        {
            DropPrimaryKeyOperation op => (op.Schema, op.Table, op.Name),
            DropUniqueConstraintOperation op => (op.Schema, op.Table, op.Name),
            DropIndexOperation op => (op.Schema, op.Table, op.Name),
            _ => (null, null, null),
        };

        return dropped is not null
               && string.Equals(schema, table.Schema, StringComparison.OrdinalIgnoreCase)
               && string.Equals(name, table.Name, StringComparison.OrdinalIgnoreCase)
               && string.Equals(dropped, keyIndex, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Reports whether the two models differ, counting a partition function, scheme, boundary, columnstore
    /// index or procedure that was added, changed or removed as a difference.
    /// </summary>
    /// <exception cref="InvalidOperationException">The target model's declarations disagree with each other or with the registered SQL generator.</exception>
    /// <exception cref="NotSupportedException">A change was requested that SQL Server cannot make in place.</exception>
    public override bool HasDifferences(IRelationalModel? source, IRelationalModel? target)
    {
        // EF's two entry points share only the protected Diff method: HasDifferences is Diff(...).Any()
        // and GetDifferences sorts what Diff produced. Overriding GetDifferences alone therefore left
        // `dotnet ef migrations has-pending-model-changes` answering the wrong question — it reported a
        // model as up to date while the next `migrations add` would have written a migration, and that
        // command is what teams gate CI on. The same blindness silenced the PendingModelChangesWarning
        // that Migrate() raises, and let `migrations remove` treat a procedure-only migration as one it
        // could revert.
        //
        // base.HasDifferences, never this.GetDifferences: the base deliberately stops at the first
        // difference and skips Sort, whose two topological sorts over the CreateTable and DropTable
        // graphs are the expensive half of producing a migration that nobody has asked for here.
        //
        // OR, never replace. A table-only change still has to report true, and an OR can only add a
        // difference, never mask one.
        //
        // OUR OBJECTS FIRST, and the order is load-bearing rather than a preference. Plan is partial - it
        // validates the target model, refuses a placement change and wraps each procedure script for
        // deployment - so a short-circuited base-first OR would skip those throws exactly when the base
        // already found a table change, while GetDifferences takes them every time. The two would then
        // agree on every boolean and disagree on whether they throw, which is the same defect as
        // disagreeing on the answer: the command succeeds, reports a difference for the wrong reason, and
        // the scaffold that follows fails instead. Our objects are annotation comparisons and the base is
        // a full relational diff, so this is also the cheaper half to run first.
        return Plan(source, target).Count > 0 || base.HasDifferences(source, target);
    }

    // One method, two callers. Two copies of these rules is how HasDifferences and GetDifferences would drift
    // apart again, and a HasDifferences that disagrees with the differ is worse than the defect it replaces:
    // the command answers "no changes" and the migration EF then scaffolds contradicts it. The
    // null-conditionals are load-bearing rather than defensive — Migrator.HasPendingModelChanges passes the
    // snapshot as the source, and a project has no snapshot until its first migration.
    private Plan Plan(IRelationalModel? source, IRelationalModel? target)
    {
        var sourcePartitions = PartitionLayout.FromModel(source?.Model);
        var targetPartitions = PartitionLayout.FromModel(target?.Model);
        var sourceCatalogs = FullTextLayout.FromModel(source?.Model);
        var targetCatalogs = FullTextLayout.FromModel(target?.Model);
        var sourceTables = CodeFirstTableLayout.Read(source);
        var targetTables = CodeFirstTableLayout.Read(target);

        if (target is not null)
        {
            targetTables.Validate(target, targetPartitions, targetCatalogs);
            RequireGeneratorFor(targetTables);
        }

        // EF matches a source table to a target table through the entity types mapped to them, not through
        // the (schema, name) pair — that is what lets it scaffold a ToTable change as a RenameTableOperation
        // instead of a drop and a create. The layouts are keyed by (schema, name), so the same matching is
        // rebuilt here: by name first, by shared entity type for what is left. Without it a renamed table
        // slips both guards below — the placement guard finds no partner to compare, and the columnstore
        // diff reads the rename as one table dropped and another created.
        var tableMap = MapTables(sourceTables, targetTables);

        RefusePlacementChanges(sourceTables, targetTables, tableMap);

        var partition = PartitionDiff.Compute(sourcePartitions, targetPartitions);

        var columnstore = ColumnstoreDiff.Compute(
            SurvivingColumnstore(sourceTables, targetTables, tableMap),
            targetTables.Columnstore.Values);

        var fullText = FullTextDiff.Compute(
            sourceCatalogs,
            targetCatalogs,
            SurvivingFullText(sourceTables, targetTables, tableMap),
            targetTables.FullText.Values);

        var functions = FunctionDiff.Compute(
            DatabaseScriptModelExtensions.GetFunctionScripts(source?.Model),
            DatabaseScriptModelExtensions.GetFunctionScripts(target?.Model));

        var procedures = StoredProcedureDiff.Compute(
            StoredProcedureModelExtensions.GetStoredProcedureScripts(source?.Model),
            StoredProcedureModelExtensions.GetStoredProcedureScripts(target?.Model));

        return new Plan(partition, columnstore, fullText, functions, procedures, targetTables.Placements, targetTables.FullText);
    }

    private void RequireGeneratorFor(CodeFirstTableLayout targetTables)
    {
        if (_migrationsSqlGenerator is CodeFirstDatabaseObjectsMigrationsSqlGenerator)
            return;

        // The differ can annotate the CreateTableOperation all it likes; only the generator can turn the
        // annotation into an ON clause. With the provider's own generator the annotation is ignored, the
        // table is created on the default filegroup, the migration applies cleanly and nothing ever says
        // so — the one failure mode this whole feature must not have.
        if (targetTables.Placements.Count > 0)
        {
            var table = targetTables.Placements.Values.First();
            throw new InvalidOperationException(
                $"Table '{table.Table}' is placed on partition scheme '{table.Scheme}', but the registered " +
                $"IMigrationsSqlGenerator is {_migrationsSqlGenerator.GetType().Name}, which would create the table " +
                "on the default filegroup without the ON clause and report success. Configure the context with " +
                "UseCodeFirstDatabaseObjects(), which replaces both the differ and the SQL generator, instead of " +
                "ReplaceService<IMigrationsModelDiffer, ...>() alone.");
        }

        // Full-text is the other object that needs the generator: the scaffold keeps the SuppressTransaction
        // annotation but not the flag, and only the generator turns the annotation back into the flag. Without
        // it the scaffolded migration runs CREATE FULLTEXT inside the transaction and fails at database update.
        if (targetTables.FullText.Count > 0)
        {
            var (table, _) = targetTables.FullText.First();
            throw new InvalidOperationException(
                $"Table '{table}' declares a full-text index, but the registered IMigrationsSqlGenerator is " +
                $"{_migrationsSqlGenerator.GetType().Name}, which would run the full-text statements of the scaffolded " +
                "migration inside the migration's transaction, where SQL Server refuses them. Configure the context with " +
                "UseCodeFirstDatabaseObjects(), which replaces both the differ and the SQL generator, instead of " +
                "ReplaceService<IMigrationsModelDiffer, ...>() alone.");
        }
    }

    // Rebuilds EF's own source-to-target table matching over the layouts: identical (schema, name) first,
    // one shared entity type name for what is left. A source table with no partner is being dropped.
    private static Dictionary<TableKey, TableKey> MapTables(CodeFirstTableLayout sourceTables, CodeFirstTableLayout targetTables)
    {
        var map = new Dictionary<TableKey, TableKey>();
        var unmatchedTargets = new HashSet<TableKey>(targetTables.Tables);

        foreach (var key in sourceTables.Tables)
        {
            if (targetTables.Tables.Contains(key))
            {
                map[key] = key;
                unmatchedTargets.Remove(key);
            }
        }

        foreach (var key in sourceTables.Tables)
        {
            if (map.ContainsKey(key))
                continue;

            var entityTypes = sourceTables.EntityTypeNames[key];
            var candidates = unmatchedTargets.Where(t => targetTables.EntityTypeNames[t].Overlaps(entityTypes)).ToList();

            // Exactly one candidate is EF's rename. More than one is a shape EF resolves with information
            // the layouts do not carry, so the match is left unmade — which errs toward reading the table as
            // dropped and re-created, the reading whose worst case is a guarded, re-runnable CREATE.
            if (candidates.Count == 1)
            {
                map[key] = candidates[0];
                unmatchedTargets.Remove(candidates[0]);
            }
        }

        return map;
    }

    // The source side of the columnstore diff: one definition per source table that still exists in the
    // target. A table with no partner is being dropped, and DROP TABLE takes the index with it — a DROP
    // INDEX after it would fail. A renamed table is translated to its new identity when the target still
    // declares an index on it, so an index that merely followed its table diffs as unchanged (or renamed)
    // instead of as dropped here and created there; when the target declares none, the definition keeps its
    // old identity, because the DROP INDEX it produces runs before the RenameTableOperation does.
    private static IEnumerable<ClusteredColumnstoreIndexDefinition> SurvivingColumnstore(
        CodeFirstTableLayout sourceTables,
        CodeFirstTableLayout targetTables,
        IReadOnlyDictionary<TableKey, TableKey> tableMap)
    {
        foreach (var (key, definition) in sourceTables.Columnstore)
        {
            if (!tableMap.TryGetValue(key, out var targetKey))
                continue;

            if (targetKey != key && targetTables.Columnstore.ContainsKey(targetKey))
                yield return definition with { Schema = targetKey.Schema, Table = targetKey.Name };
            else
                yield return definition;
        }
    }

    // The source side of the full-text diff. A dropped table takes its index along, as with the columnstore.
    // A renamed table is different from the columnstore case in one respect: a changed full-text index is a
    // DROP and a CREATE, and the DROP runs before the RenameTableOperation while the CREATE runs after it, so
    // the two must name different tables. The definition is therefore translated to the new identity only
    // when the target declares the same index there — where it reads as unchanged and produces nothing — and
    // kept under its old identity otherwise, where the diff reads the old table as losing its index and the
    // new one as gaining it, which is exactly the two statements in the order the rename needs. A key index
    // that follows EF's naming convention changes with the table name, so an index keyed on the primary key
    // takes this route on every rename; one keyed on an explicitly named unique index does not.
    private static IEnumerable<FullTextIndexDefinition> SurvivingFullText(
        CodeFirstTableLayout sourceTables,
        CodeFirstTableLayout targetTables,
        IReadOnlyDictionary<TableKey, TableKey> tableMap)
    {
        foreach (var (key, definition) in sourceTables.FullText)
        {
            if (!tableMap.TryGetValue(key, out var targetKey))
                continue;

            if (targetKey != key
                && targetTables.FullText.TryGetValue(targetKey, out var renamed)
                && string.Equals(renamed.Signature, definition.Signature, StringComparison.Ordinal))
                yield return definition with { Schema = targetKey.Schema, Table = targetKey.Name };
            else
                yield return definition;
        }
    }

    private static void RefusePlacementChanges(
        CodeFirstTableLayout sourceTables,
        CodeFirstTableLayout targetTables,
        IReadOnlyDictionary<TableKey, TableKey> tableMap)
    {
        foreach (var (sourceKey, targetKey) in tableMap)
        {
            sourceTables.Placements.TryGetValue(sourceKey, out var before);
            targetTables.Placements.TryGetValue(targetKey, out var after);

            if (before is null && after is null)
                continue;

            // The scheme must stand still. The partitioning column may be recognised by its property OR by
            // its column name, because either can be renamed while the placement itself never moves: rename
            // the property and the column stays where it was, the placement with it; rename the column and
            // sp_rename keeps the partitioning, which follows the column, not the column's name.
            if (before is not null && after is not null
                && string.Equals(before.Scheme, after.Scheme, StringComparison.OrdinalIgnoreCase)
                && (string.Equals(before.Property, after.Property, StringComparison.Ordinal)
                    || string.Equals(before.Column, after.Column, StringComparison.OrdinalIgnoreCase)))
                continue;

            // Placement is decided by CREATE TABLE. Changing it afterwards means rebuilding the clustered
            // index (or the heap) onto the new scheme, over however many rows the table holds, with the
            // nonclustered indexes following — a migration whose cost and downtime depend on the data, which
            // is exactly what a scaffolded migration cannot judge.
            var renamed = sourceKey == targetKey ? "" : $", renamed to '{targetKey}' in the model,";
            throw new NotSupportedException(
                $"Table '{sourceKey}'{renamed} is placed on {Describe(before)} in the snapshot and on {Describe(after)} in the model. " +
                "A table's partition scheme and column are fixed by CREATE TABLE; SQL Server moves a table only by " +
                "rebuilding its clustered index, so write that migration by hand (CREATE CLUSTERED INDEX ... WITH " +
                "(DROP_EXISTING = ON) ON [scheme]([column]), or a copy into a new table), then bring the model in line.");
        }

        static string Describe(TablePlacement? placement)
            => placement is null ? "no partition scheme" : $"partition scheme '{placement.Scheme}' by column '{placement.Column}'";
    }

    // SuppressTransaction is the property EF's migrator reads; the annotation is what survives the scaffold. Both
    // are set, so the operation behaves the same whether it is executed straight from the differ (a test, or
    // Migrate() over pending model changes) or after a round trip through a scaffolded migration.
    private static SqlOperation Sql(string sql, bool suppressTransaction = false)
    {
        var operation = new SqlOperation { Sql = sql, SuppressTransaction = suppressTransaction };
        if (suppressTransaction)
            operation[CodeFirstAnnotations.SuppressTransaction] = true;
        return operation;
    }
}

/// <summary>Everything this package adds to one diff, computed once and read by both entry points.</summary>
internal sealed record Plan(
    IReadOnlyList<PartitionOp> Partition,
    IReadOnlyList<ColumnstoreOp> Columnstore,
    IReadOnlyList<FullTextOp> FullText,
    IReadOnlyList<FunctionOp> Functions,
    IReadOnlyList<ProcOp> Procedures,
    IReadOnlyDictionary<TableKey, TablePlacement> Placements,
    IReadOnlyDictionary<TableKey, FullTextIndexDefinition> FullTextTargets)
{
    /// <summary>The number of operations this package will add to the migration. Placements add annotations, not operations.</summary>
    public int Count => Partition.Count + Columnstore.Count + FullText.Count + Functions.Count + Procedures.Count;
}
