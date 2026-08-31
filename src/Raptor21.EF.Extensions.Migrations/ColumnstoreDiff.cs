namespace Raptor21.EF.Extensions.Migrations;

/// <summary>A clustered columnstore index as declared on the model: the table it sits on and its name.</summary>
/// <param name="Schema">The table's schema, or <see langword="null"/> when the connection's default applies.</param>
/// <param name="Table">The table name.</param>
/// <param name="IndexName">The index name.</param>
public sealed record ClusteredColumnstoreIndexDefinition(string? Schema, string Table, string IndexName);

/// <summary>The kind of migration step a columnstore diff produces.</summary>
public enum ColumnstoreOpKind
{
    /// <summary>The table gained a clustered columnstore index → <c>CREATE CLUSTERED COLUMNSTORE INDEX</c>, guarded by <c>IF NOT EXISTS</c>.</summary>
    Create,

    /// <summary>The table lost its clustered columnstore index → <c>DROP INDEX IF EXISTS</c>.</summary>
    Drop,

    /// <summary>
    /// The index changed its name and nothing else → <c>sp_rename</c>, guarded by <c>IF EXISTS</c> on the old name.
    /// A rename is metadata; dropping and recreating a columnstore index rebuilds every row group.
    /// </summary>
    Rename,
}

/// <summary>A single columnstore migration step: the table and index it concerns, what it does and the T-SQL that does it.</summary>
/// <param name="Schema">The table's schema, or <see langword="null"/>.</param>
/// <param name="Table">The table name.</param>
/// <param name="IndexName">The index name the step leaves in place (for a rename, the new name).</param>
/// <param name="Kind">What the step does.</param>
/// <param name="Sql">The statement to run.</param>
public sealed record ColumnstoreOp(string? Schema, string Table, string IndexName, ColumnstoreOpKind Kind, string Sql);

/// <summary>
/// Pure diff between two sets of clustered columnstore declarations, matched by table. Symmetric like
/// <see cref="StoredProcedureDiff"/>, so the <c>Down</c> direction falls out of the same rule.
/// </summary>
/// <remarks>
/// The caller decides what a table's absence means. <see cref="CodeFirstDatabaseObjectsModelDiffer"/> feeds
/// only tables that still exist in the target on the source side, because a table that is being dropped takes
/// its index with it and a <c>DROP INDEX</c> against it would fail. A renamed table it translates to its new
/// identity when the target still declares an index there, so the diff compares one table rather than two;
/// when the target declares none, the definition keeps its old name, because the <c>DROP INDEX</c> that
/// produces runs before the rename does.
/// </remarks>
public static class ColumnstoreDiff
{
    /// <summary>Computes the steps that turn <paramref name="source"/> into <paramref name="target"/>, in table order.</summary>
    /// <exception cref="ArgumentException">A table appears twice on one side.</exception>
    public static IReadOnlyList<ColumnstoreOp> Compute(
        IEnumerable<ClusteredColumnstoreIndexDefinition> source,
        IEnumerable<ClusteredColumnstoreIndexDefinition> target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var before = Index(source, nameof(source));
        var after = Index(target, nameof(target));

        var keys = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in before.Keys) keys.Add(k);
        foreach (var k in after.Keys) keys.Add(k);

        var ops = new List<ColumnstoreOp>();
        foreach (var key in keys)
        {
            var inSource = before.TryGetValue(key, out var old);
            var inTarget = after.TryGetValue(key, out var current);

            if (inTarget && !inSource)
            {
                ops.Add(new ColumnstoreOp(current!.Schema, current.Table, current.IndexName, ColumnstoreOpKind.Create, CreateSql(current)));
            }
            else if (inSource && !inTarget)
            {
                ops.Add(new ColumnstoreOp(old!.Schema, old.Table, old.IndexName, ColumnstoreOpKind.Drop, DropSql(old)));
            }
            else if (!string.Equals(old!.IndexName, current!.IndexName, StringComparison.Ordinal))
            {
                ops.Add(new ColumnstoreOp(current.Schema, current.Table, current.IndexName, ColumnstoreOpKind.Rename, RenameSql(old, current)));
            }
        }

        return ops;
    }

    // The table is already on its partition scheme when this runs, because the differ places the statement
    // straight after the CREATE TABLE. An index created without an ON clause inherits the table's placement,
    // so the columnstore index — and every nonclustered index EF creates after it — comes out aligned to the
    // scheme without anything here having to know the scheme exists. That alignment is what lets a partition
    // be switched or truncated on its own later.
    private static string CreateSql(ClusteredColumnstoreIndexDefinition index)
        => $"IF NOT EXISTS ({IndexExists(index.Schema, index.Table, index.IndexName)})\n" +
           $"    CREATE CLUSTERED COLUMNSTORE INDEX {SqlText.Delimit(index.IndexName)} ON {SqlText.Qualified(index.Schema, index.Table)};";

    private static string DropSql(ClusteredColumnstoreIndexDefinition index)
        => $"DROP INDEX IF EXISTS {SqlText.Delimit(index.IndexName)} ON {SqlText.Qualified(index.Schema, index.Table)};";

    private static string RenameSql(ClusteredColumnstoreIndexDefinition old, ClusteredColumnstoreIndexDefinition current)
        => $"IF EXISTS ({IndexExists(old.Schema, old.Table, old.IndexName)})\n" +
           $"    EXEC sp_rename {SqlText.NLiteral($"{SqlText.Qualified(old.Schema, old.Table)}.{SqlText.Delimit(old.IndexName)}")}, " +
           $"{SqlText.NLiteral(current.IndexName)}, N'INDEX';";

    private static string IndexExists(string? schema, string table, string indexName)
        => $"SELECT 1 FROM sys.indexes WHERE name = {SqlText.NLiteral(indexName)} " +
           $"AND object_id = OBJECT_ID({SqlText.NLiteral(SqlText.Qualified(schema, table))})";

    private static Dictionary<string, ClusteredColumnstoreIndexDefinition> Index(
        IEnumerable<ClusteredColumnstoreIndexDefinition> definitions, string parameterName)
    {
        var result = new Dictionary<string, ClusteredColumnstoreIndexDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            var key = Key(definition.Schema, definition.Table);
            if (!result.TryAdd(key, definition))
                throw new ArgumentException($"Table '{key}' declares more than one clustered columnstore index.", parameterName);
        }

        return result;
    }

    private static string Key(string? schema, string table) => string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";
}
