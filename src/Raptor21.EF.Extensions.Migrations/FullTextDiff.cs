using Microsoft.EntityFrameworkCore.Metadata;

namespace Raptor21.EF.Extensions.Migrations;

/// <summary>The kind of migration step a full-text diff produces.</summary>
public enum FullTextOpKind
{
    /// <summary>A catalog is new → <c>CREATE FULLTEXT CATALOG</c>, guarded by <c>IF NOT EXISTS</c>.</summary>
    CreateCatalog,

    /// <summary>A catalog was removed → <c>DROP FULLTEXT CATALOG</c>, guarded by <c>IF EXISTS</c>.</summary>
    DropCatalog,

    /// <summary>A table gained a full-text index, or its index changed → <c>CREATE FULLTEXT INDEX</c>, guarded by <c>IF NOT EXISTS</c>.</summary>
    CreateIndex,

    /// <summary>A table lost its full-text index, or its index changed → <c>DROP FULLTEXT INDEX</c>, guarded by <c>IF EXISTS</c>.</summary>
    DropIndex,
}

/// <summary>A single full-text migration step: the object it concerns, what it does and the T-SQL that does it.</summary>
/// <param name="Name">The catalog name, or the qualified table name for an index step.</param>
/// <param name="Kind">What the step does.</param>
/// <param name="Sql">The statement to run — outside a transaction, which SQL Server requires of every full-text DDL statement.</param>
public sealed record FullTextOp(string Name, FullTextOpKind Kind, string Sql)
{
    /// <summary>Whether this step removes an object.</summary>
    public bool IsDrop => Kind is FullTextOpKind.DropCatalog or FullTextOpKind.DropIndex;
}

/// <summary>Every full-text catalog a model declares. Names compare case-insensitively, as SQL Server compares them by default.</summary>
public sealed class FullTextLayout
{
    /// <summary>A layout with no catalogs — what a model without declarations, or no model at all, reads as.</summary>
    public static FullTextLayout Empty { get; } = new([]);

    /// <summary>Creates a layout from explicit catalog names.</summary>
    public FullTextLayout(IEnumerable<string> catalogs)
    {
        ArgumentNullException.ThrowIfNull(catalogs);

        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var catalog in catalogs)
        {
            if (!set.Add(catalog))
                throw new ArgumentException($"Full-text catalog '{catalog}' is declared twice.", nameof(catalogs));
        }

        Catalogs = set;
    }

    /// <summary>The catalog names.</summary>
    public IReadOnlySet<string> Catalogs { get; }

    /// <summary>
    /// Reads the declarations off a model's annotations. A <see langword="null"/> model — the snapshot of a
    /// project before its first migration — reads as <see cref="Empty"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">One catalog is declared under two casings.</exception>
    public static FullTextLayout FromModel(IModel? model)
    {
        if (model is null)
            return Empty;

        var catalogs = new List<string>();
        foreach (var annotation in model.GetAnnotations())
        {
            if (!annotation.Name.StartsWith(CodeFirstAnnotations.FullTextCatalogPrefix, StringComparison.Ordinal))
                continue;

            var name = annotation.Name[CodeFirstAnnotations.FullTextCatalogPrefix.Length..];
            if (catalogs.Contains(name, StringComparer.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    $"Full-text catalog '{name}' is declared more than once under different casings. SQL Server compares " +
                    "the names case-insensitively; declare the catalog once.");

            catalogs.Add(name);
        }

        return new FullTextLayout(catalogs);
    }
}

/// <summary>
/// Pure diff between two full-text declarations — catalogs by name, indexes by table. Symmetric like
/// <see cref="ColumnstoreDiff"/>, so the <c>Down</c> direction falls out of the same rule. A changed index is
/// a drop and a create: SQL Server can add or drop one column of a full-text index in place, but every other
/// change — the catalog, the key index, the tracking mode — is a rebuild anyway, and a repopulation follows
/// either way.
/// </summary>
/// <remarks>
/// The caller decides what a table's absence means. <see cref="CodeFirstDatabaseObjectsModelDiffer"/> feeds
/// only tables that still exist in the target on the source side, because a dropped table takes its full-text
/// index with it. The steps come out in the one order that is valid on its own — index drops, catalog creates,
/// index creates, catalog drops — and the differ spreads them around EF's operations from there.
/// </remarks>
public static class FullTextDiff
{
    /// <summary>Computes the steps that turn the source declarations into the target declarations.</summary>
    /// <exception cref="ArgumentException">A table appears twice on one side.</exception>
    public static IReadOnlyList<FullTextOp> Compute(
        FullTextLayout sourceCatalogs,
        FullTextLayout targetCatalogs,
        IEnumerable<FullTextIndexDefinition> sourceIndexes,
        IEnumerable<FullTextIndexDefinition> targetIndexes)
    {
        ArgumentNullException.ThrowIfNull(sourceCatalogs);
        ArgumentNullException.ThrowIfNull(targetCatalogs);
        ArgumentNullException.ThrowIfNull(sourceIndexes);
        ArgumentNullException.ThrowIfNull(targetIndexes);

        var before = Index(sourceIndexes, nameof(sourceIndexes));
        var after = Index(targetIndexes, nameof(targetIndexes));

        var tables = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in before.Keys) tables.Add(k);
        foreach (var k in after.Keys) tables.Add(k);

        var drops = new List<FullTextOp>();
        var creates = new List<FullTextOp>();
        foreach (var table in tables)
        {
            var inSource = before.TryGetValue(table, out var old);
            var inTarget = after.TryGetValue(table, out var current);

            if (inTarget && !inSource)
                creates.Add(CreateIndex(current!));
            else if (inSource && !inTarget)
                drops.Add(DropIndex(old!));
            else if (!string.Equals(old!.Signature, current!.Signature, StringComparison.Ordinal))
            {
                drops.Add(DropIndex(old));
                creates.Add(CreateIndex(current));
            }
        }

        var ops = new List<FullTextOp>(drops);

        foreach (var catalog in targetCatalogs.Catalogs.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            if (!sourceCatalogs.Catalogs.Contains(catalog))
                ops.Add(new FullTextOp(catalog, FullTextOpKind.CreateCatalog, CreateCatalogSql(catalog)));
        }

        ops.AddRange(creates);

        foreach (var catalog in sourceCatalogs.Catalogs.OrderBy(c => c, StringComparer.OrdinalIgnoreCase))
        {
            if (!targetCatalogs.Catalogs.Contains(catalog))
                ops.Add(new FullTextOp(catalog, FullTextOpKind.DropCatalog, DropCatalogSql(catalog)));
        }

        return ops;
    }

    /// <summary>The step that creates <paramref name="index"/> if its table has no full-text index yet.</summary>
    public static FullTextOp CreateIndex(FullTextIndexDefinition index)
    {
        ArgumentNullException.ThrowIfNull(index);
        return new FullTextOp(Key(index.Schema, index.Table), FullTextOpKind.CreateIndex, CreateIndexSql(index));
    }

    /// <summary>The step that drops the full-text index of <paramref name="index"/>'s table if it has one.</summary>
    public static FullTextOp DropIndex(FullTextIndexDefinition index)
    {
        ArgumentNullException.ThrowIfNull(index);
        return new FullTextOp(Key(index.Schema, index.Table), FullTextOpKind.DropIndex, DropIndexSql(index));
    }

    private static string CreateCatalogSql(string catalog)
        => $"IF NOT EXISTS ({CatalogExists(catalog)})\n" +
           $"    CREATE FULLTEXT CATALOG {SqlText.Delimit(catalog)};";

    private static string DropCatalogSql(string catalog)
        => $"IF EXISTS ({CatalogExists(catalog)})\n" +
           $"    DROP FULLTEXT CATALOG {SqlText.Delimit(catalog)};";

    // A table has at most one full-text index, so existence is asked of the table rather than of a name —
    // sys.fulltext_indexes has no name column to ask about.
    private static string CreateIndexSql(FullTextIndexDefinition index)
    {
        var columns = string.Join(", ", index.Columns.Select(c =>
            c.Language is null
                ? SqlText.Delimit(c.Name)
                : $"{SqlText.Delimit(c.Name)} LANGUAGE {c.Language.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));

        return $"IF NOT EXISTS ({IndexExists(index.Schema, index.Table)})\n" +
               $"    CREATE FULLTEXT INDEX ON {SqlText.Qualified(index.Schema, index.Table)} ({columns}) " +
               $"KEY INDEX {SqlText.Delimit(index.KeyIndex)} ON {SqlText.Delimit(index.Catalog)} " +
               $"WITH CHANGE_TRACKING {FullTextIndexDeclaration.Spell(index.ChangeTracking)};";
    }

    private static string DropIndexSql(FullTextIndexDefinition index)
        => $"IF EXISTS ({IndexExists(index.Schema, index.Table)})\n" +
           $"    DROP FULLTEXT INDEX ON {SqlText.Qualified(index.Schema, index.Table)};";

    private static string CatalogExists(string catalog)
        => $"SELECT 1 FROM sys.fulltext_catalogs WHERE name = {SqlText.NLiteral(catalog)}";

    private static string IndexExists(string? schema, string table)
        => $"SELECT 1 FROM sys.fulltext_indexes WHERE object_id = OBJECT_ID({SqlText.NLiteral(SqlText.Qualified(schema, table))})";

    private static Dictionary<string, FullTextIndexDefinition> Index(IEnumerable<FullTextIndexDefinition> definitions, string parameterName)
    {
        var result = new Dictionary<string, FullTextIndexDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var definition in definitions)
        {
            var key = Key(definition.Schema, definition.Table);
            if (!result.TryAdd(key, definition))
                throw new ArgumentException($"Table '{key}' declares more than one full-text index.", parameterName);
        }

        return result;
    }

    private static string Key(string? schema, string table) => string.IsNullOrEmpty(schema) ? table : $"{schema}.{table}";
}
