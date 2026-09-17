using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Raptor21.EF.Extensions.Migrations;

/// <summary>A table's identity as the differ matches it: the schema and name a <c>CreateTableOperation</c> carries.</summary>
internal readonly record struct TableKey(string? Schema, string Name)
{
    public static TableKey Of(ITable table) => new(table.Schema, table.Name);

    public override string ToString() => string.IsNullOrEmpty(Schema) ? Name : $"{Schema}.{Name}";
}

/// <summary>
/// A table's placement on a partition scheme: the property the annotation names and the column it resolves
/// to. Both travel, because either can be renamed while the other — and the placement itself — stands still.
/// </summary>
internal sealed record TablePlacement(TableKey Table, string Scheme, string Property, string Column);

/// <summary>
/// What the entity-level annotations say about the tables of one relational model: which tables sit on a
/// partition scheme, which carry a clustered columnstore index and which carry a full-text index. Entity
/// annotations are not copied onto <see cref="ITable"/> by the provider, so this walks each table's entity
/// type mappings to find them.
/// </summary>
internal sealed class CodeFirstTableLayout
{
    private static readonly CodeFirstTableLayout EmptyLayout = new(new HashSet<TableKey>(), [], [], [], []);

    private CodeFirstTableLayout(
        HashSet<TableKey> tables,
        Dictionary<TableKey, TablePlacement> placements,
        Dictionary<TableKey, ClusteredColumnstoreIndexDefinition> columnstore,
        Dictionary<TableKey, FullTextIndexDefinition> fullText,
        Dictionary<TableKey, IReadOnlySet<string>> entityTypeNames)
    {
        Tables = tables;
        Placements = placements;
        Columnstore = columnstore;
        FullText = fullText;
        EntityTypeNames = entityTypeNames;
    }

    /// <summary>Every table the model maps and migrations manage, placed or not.</summary>
    public IReadOnlySet<TableKey> Tables { get; }

    /// <summary>The tables placed on a partition scheme.</summary>
    public IReadOnlyDictionary<TableKey, TablePlacement> Placements { get; }

    /// <summary>The tables carrying a clustered columnstore index.</summary>
    public IReadOnlyDictionary<TableKey, ClusteredColumnstoreIndexDefinition> Columnstore { get; }

    /// <summary>The tables carrying a full-text index, resolved to column names and a key index name.</summary>
    public IReadOnlyDictionary<TableKey, FullTextIndexDefinition> FullText { get; }

    /// <summary>
    /// The names of the entity types mapped to each table. EF matches a source table to a target table
    /// through these, not through the table's own name — that is how a <c>ToTable</c> change becomes a
    /// <c>RenameTableOperation</c> — and the differ rebuilds the same matching from this set.
    /// </summary>
    public IReadOnlyDictionary<TableKey, IReadOnlySet<string>> EntityTypeNames { get; }

    /// <summary>Reads the layout of a model; a <see langword="null"/> model — no snapshot yet — reads as empty.</summary>
    /// <exception cref="InvalidOperationException">
    /// The partition property does not exist or maps to no column of the table, or two entity types sharing a
    /// table declare different placements or indexes.
    /// </exception>
    public static CodeFirstTableLayout Read(IRelationalModel? model)
    {
        if (model is null)
            return EmptyLayout;

        var tables = new HashSet<TableKey>();
        var placements = new Dictionary<TableKey, TablePlacement>();
        var columnstore = new Dictionary<TableKey, ClusteredColumnstoreIndexDefinition>();
        var fullText = new Dictionary<TableKey, FullTextIndexDefinition>();
        var entityTypeNames = new Dictionary<TableKey, IReadOnlySet<string>>();

        foreach (var table in model.Tables)
        {
            if (table.IsExcludedFromMigrations)
                continue;

            var key = TableKey.Of(table);
            tables.Add(key);

            var names = new HashSet<string>(StringComparer.Ordinal);
            entityTypeNames[key] = names;

            foreach (var mapping in table.EntityTypeMappings)
            {
                names.Add(mapping.TypeBase.Name);

                if (mapping.TypeBase is not IEntityType entityType)
                    continue;

                if (entityType[CodeFirstAnnotations.PartitionScheme] is string scheme
                    || entityType[CodeFirstAnnotations.PartitionColumn] is string)
                {
                    var placement = ResolvePlacement(table, key, entityType);
                    if (placements.TryGetValue(key, out var existing) && existing != placement)
                        throw new InvalidOperationException(
                            $"Table '{key}' is placed on partition scheme '{existing.Scheme}'({existing.Column}) by one " +
                            $"entity type and on '{placement.Scheme}'({placement.Column}) by '{entityType.DisplayName()}'. " +
                            "Entity types sharing a table must agree on its placement; declare it once, on the root type.");
                    placements[key] = placement;
                }

                if (entityType[CodeFirstAnnotations.ClusteredColumnstoreIndex] is string indexName)
                {
                    var definition = new ClusteredColumnstoreIndexDefinition(table.Schema, table.Name, indexName);
                    if (columnstore.TryGetValue(key, out var existing) && existing != definition)
                        throw new InvalidOperationException(
                            $"Table '{key}' declares clustered columnstore index '{existing.IndexName}' on one entity type " +
                            $"and '{indexName}' on '{entityType.DisplayName()}'. A table has one clustered index; declare it " +
                            "once, on the root type.");
                    columnstore[key] = definition;
                }

                if (entityType[CodeFirstAnnotations.FullTextIndex] is string serialized)
                {
                    var definition = ResolveFullText(table, key, entityType, serialized);
                    if (fullText.TryGetValue(key, out var existing) && existing != definition)
                        throw new InvalidOperationException(
                            $"Table '{key}' declares one full-text index on one entity type and a different one on " +
                            $"'{entityType.DisplayName()}'. A table has one full-text index; declare it once, on the root type.");
                    fullText[key] = definition;
                }
            }
        }

        return new CodeFirstTableLayout(tables, placements, columnstore, fullText, entityTypeNames);
    }

    /// <summary>
    /// Checks the declarations of a target model against each other, so that a mistake fails at
    /// <c>migrations add</c> with the fix in the message rather than at <c>database update</c> with a SQL
    /// Server error number.
    /// </summary>
    /// <exception cref="InvalidOperationException">A declaration refers to something undeclared, or two declarations cannot coexist.</exception>
    public void Validate(IRelationalModel model, PartitionLayout partitions, FullTextLayout fullTextCatalogs)
    {
        foreach (var scheme in partitions.Schemes.Values)
        {
            if (!partitions.Functions.ContainsKey(scheme.FunctionName))
                throw new InvalidOperationException(
                    $"Partition scheme '{scheme.Name}' maps partition function '{scheme.FunctionName}', which is not declared. " +
                    $"Call modelBuilder.HasPartitionFunction(\"{scheme.FunctionName}\", ...) or correct the name.");
        }

        foreach (var table in model.Tables)
        {
            var key = TableKey.Of(table);
            Placements.TryGetValue(key, out var placement);
            Columnstore.TryGetValue(key, out var columnstore);
            FullText.TryGetValue(key, out var fullText);

            if (placement is not null)
            {
                if (!partitions.Schemes.ContainsKey(placement.Scheme))
                    throw new InvalidOperationException(
                        $"Table '{key}' is placed on partition scheme '{placement.Scheme}', which is not declared. " +
                        $"Call modelBuilder.HasPartitionScheme(\"{placement.Scheme}\", \"<function>\") or correct the name in OnPartitionScheme.");

                ValidateUniqueKeysCarryPartitionColumn(table, key, placement);
            }

            if (columnstore is not null)
                ValidateNoOtherClusteredIndex(table, key, columnstore);

            if (fullText is not null)
                ValidateFullText(table, key, fullText, fullTextCatalogs);
        }
    }

    // Everything SQL Server would refuse at CREATE FULLTEXT INDEX, refused here at migrations add instead: the
    // catalog must exist (error 7642), the key index must be unique, single-column and non-nullable (7653,
    // 7654), and each column must be a character or xml type (7670 — a varbinary(max) or image column can be
    // indexed too, but only with a TYPE COLUMN naming the document's extension, which this version does not
    // declare).
    private static void ValidateFullText(ITable table, TableKey key, FullTextIndexDefinition fullText, FullTextLayout catalogs)
    {
        if (!catalogs.Catalogs.Contains(fullText.Catalog))
            throw new InvalidOperationException(
                $"Table '{key}' declares a full-text index on catalog '{fullText.Catalog}', which is not declared. " +
                $"Call modelBuilder.HasFullTextCatalog(\"{fullText.Catalog}\") or correct the name in HasFullTextIndex.");

        var keyColumns = KeyIndexColumns(table, fullText.KeyIndex)
            ?? throw new InvalidOperationException(
                $"Table '{key}' declares a full-text index keyed on '{fullText.KeyIndex}', which is not a primary key, unique " +
                "constraint or unique index of the table. Name one with the keyIndex argument of HasFullTextIndex, or leave " +
                "it out to use the primary key.");

        if (keyColumns.Count != 1)
            throw new InvalidOperationException(
                $"Table '{key}' declares a full-text index keyed on '{fullText.KeyIndex}', which has {keyColumns.Count} columns. " +
                "SQL Server keys a full-text index on a single-column unique index; add one and name it with the keyIndex " +
                "argument of HasFullTextIndex.");

        if (keyColumns[0].IsNullable)
            throw new InvalidOperationException(
                $"Table '{key}' declares a full-text index keyed on '{fullText.KeyIndex}', whose column '{keyColumns[0].Name}' is " +
                "nullable. SQL Server keys a full-text index on a non-nullable column; make it required or key on another index.");

        foreach (var column in fullText.Columns)
        {
            var storeType = table.Columns.First(c => string.Equals(c.Name, column.Name, StringComparison.OrdinalIgnoreCase)).StoreType;
            var baseType = storeType.Split('(')[0].Trim().ToLowerInvariant();
            if (baseType is "char" or "nchar" or "varchar" or "nvarchar" or "text" or "ntext" or "xml")
                continue;

            var hint = baseType is "varbinary" or "image"
                ? "a binary column needs a TYPE COLUMN naming each document's extension, which this package does not declare"
                : "SQL Server full-text indexes character and xml columns";
            throw new InvalidOperationException(
                $"Table '{key}' declares a full-text index over column '{column.Name}' of type '{storeType}'; {hint}. " +
                "Remove the column from HasFullTextIndex.");
        }
    }

    private static IReadOnlyList<IColumn>? KeyIndexColumns(ITable table, string keyIndex)
    {
        if (table.PrimaryKey is { } primaryKey && Named(primaryKey.Name))
            return primaryKey.Columns;

        foreach (var constraint in table.UniqueConstraints)
        {
            if (Named(constraint.Name))
                return constraint.Columns;
        }

        foreach (var index in table.Indexes)
        {
            if (index.IsUnique && Named(index.Name))
                return index.Columns;
        }

        return null;

        bool Named(string name) => string.Equals(name, keyIndex, StringComparison.OrdinalIgnoreCase);
    }

    // SQL Server requires the partitioning column in the key of every unique index aligned to the scheme
    // (error 1908), and every index EF creates is aligned because EF never writes an ON clause for one. A
    // non-unique index is not affected: the engine adds the partitioning column to it on its own.
    private static void ValidateUniqueKeysCarryPartitionColumn(ITable table, TableKey key, TablePlacement placement)
    {
        if (table.PrimaryKey is { } primaryKey && !Contains(primaryKey.Columns, placement.Column))
            Refuse(primaryKey.Name, "primary key");

        foreach (var constraint in table.UniqueConstraints)
        {
            if (!constraint.GetIsPrimaryKey() && !Contains(constraint.Columns, placement.Column))
                Refuse(constraint.Name, "unique constraint");
        }

        foreach (var index in table.Indexes)
        {
            if (index.IsUnique && !Contains(index.Columns, placement.Column))
                Refuse(index.Name, "unique index");
        }

        void Refuse(string name, string kind)
            => throw new InvalidOperationException(
                $"Table '{key}' is partitioned by column '{placement.Column}', but its {kind} '{name}' does not include " +
                "that column. SQL Server requires the partitioning column in the key of every unique index aligned to " +
                "the partition scheme. Add the column to the key, or make the index non-unique.");
    }

    // A table has one clustered index. EF makes the primary key clustered unless told otherwise, and the
    // resulting CREATE TABLE would succeed and only the columnstore statement after it would fail — at
    // database update, with the table already created. Refusing here, at migrations add, is the cheap moment.
    private static void ValidateNoOtherClusteredIndex(ITable table, TableKey key, ClusteredColumnstoreIndexDefinition columnstore)
    {
        // Clustering is read through the provider's own IsClustered(storeObject) extensions, exactly as
        // SqlServerAnnotationProvider reads it when it writes the DDL: the FIRST mapped key or index decides,
        // and an absent annotation falls back to the shared-object root key. Reading the raw annotation off
        // EVERY mapped key looked equivalent and was not — an owned type or a split entity type sharing the
        // table maps a second IKey onto the same primary key constraint, that key carries no annotation of
        // its own, and "no annotation" read as "clustered" refused a model the provider deploys happily.
        var storeObject = StoreObjectIdentifier.Table(table.Name, table.Schema);

        if (table.PrimaryKey is { } primaryKey
            && (primaryKey.MappedKeys.First().IsClustered(storeObject) ?? true))
        {
            throw new InvalidOperationException(
                $"Table '{key}' declares clustered columnstore index '{columnstore.IndexName}', but its primary key " +
                $"'{primaryKey.Name}' is clustered too, and a table has one clustered index. Either declare the entity " +
                "HasNoKey(), or keep the key nonclustered with HasKey(...).IsClustered(false).");
        }

        foreach (var constraint in table.UniqueConstraints)
        {
            if (!constraint.GetIsPrimaryKey()
                && constraint.MappedKeys.First().IsClustered(storeObject) == true)
                throw new InvalidOperationException(
                    $"Table '{key}' declares clustered columnstore index '{columnstore.IndexName}', but its unique " +
                    $"constraint '{constraint.Name}' is clustered too, and a table has one clustered index. Declare the " +
                    "alternate key with IsClustered(false).");
        }

        foreach (var index in table.Indexes)
        {
            if (index.MappedIndexes.First().IsClustered(storeObject) == true)
                throw new InvalidOperationException(
                    $"Table '{key}' declares clustered columnstore index '{columnstore.IndexName}', but its index " +
                    $"'{index.Name}' is clustered too, and a table has one clustered index. Remove IsClustered() from " +
                    "the index.");
        }
    }

    private static TablePlacement ResolvePlacement(ITable table, TableKey key, IEntityType entityType)
    {
        var scheme = entityType[CodeFirstAnnotations.PartitionScheme] as string;
        var propertyName = entityType[CodeFirstAnnotations.PartitionColumn] as string;

        if (string.IsNullOrWhiteSpace(scheme) || string.IsNullOrWhiteSpace(propertyName))
            throw new InvalidOperationException(
                $"Table '{key}' carries only one half of a partition placement (scheme '{scheme}', property '{propertyName}'). " +
                "Declare both through OnPartitionScheme(scheme, column).");

        var property = entityType.FindProperty(propertyName)
            ?? throw new InvalidOperationException(
                $"Table '{key}' is partitioned by property '{propertyName}', which '{entityType.DisplayName()}' does not have. " +
                "Name a mapped property of the entity in OnPartitionScheme.");

        var storeObject = StoreObjectIdentifier.Table(table.Name, table.Schema);
        var column = property.GetColumnName(storeObject)
            ?? throw new InvalidOperationException(
                $"Table '{key}' is partitioned by property '{propertyName}', which maps to no column of that table. " +
                "The partitioning column must be one of the table's own columns.");

        return new TablePlacement(key, scheme.Trim(), propertyName.Trim(), column);
    }

    // Property names become column names here, and an absent key index becomes the primary key's name — the
    // one point where the declaration and the table it describes are both in hand. The snapshot side goes
    // through the same resolution, so a renamed column diffs as a changed index rather than as noise.
    private static FullTextIndexDefinition ResolveFullText(ITable table, TableKey key, IEntityType entityType, string serialized)
    {
        FullTextIndexDeclaration declaration;
        try
        {
            declaration = FullTextIndexDeclaration.Parse(serialized);
        }
        catch (FormatException ex)
        {
            throw new InvalidOperationException($"Table '{key}': {ex.Message}", ex);
        }

        var storeObject = StoreObjectIdentifier.Table(table.Name, table.Schema);
        var columns = new List<FullTextColumn>(declaration.Columns.Count);
        foreach (var column in declaration.Columns)
        {
            var property = entityType.FindProperty(column.Name)
                ?? throw new InvalidOperationException(
                    $"Table '{key}' declares a full-text index over property '{column.Name}', which '{entityType.DisplayName()}' does not have. " +
                    "Name mapped properties of the entity in HasFullTextIndex.");

            var columnName = property.GetColumnName(storeObject)
                ?? throw new InvalidOperationException(
                    $"Table '{key}' declares a full-text index over property '{column.Name}', which maps to no column of that table. " +
                    "A full-text column must be one of the table's own columns.");

            columns.Add(new FullTextColumn(columnName, column.Language));
        }

        var keyIndex = declaration.KeyIndex
            ?? table.PrimaryKey?.Name
            ?? throw new InvalidOperationException(
                $"Table '{key}' declares a full-text index keyed on its primary key, and has none. Declare a key with HasKey, " +
                "or name a unique single-column index with the keyIndex argument of HasFullTextIndex.");

        return new FullTextIndexDefinition(table.Schema, table.Name, declaration.Catalog, keyIndex, declaration.ChangeTracking, columns);
    }

    private static bool Contains(IReadOnlyList<IColumn> columns, string column)
        => columns.Any(c => string.Equals(c.Name, column, StringComparison.OrdinalIgnoreCase));
}
