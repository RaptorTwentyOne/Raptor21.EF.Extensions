namespace Raptor21.EF.Extensions.Migrations;

/// <summary>
/// The annotation keys under which physical-layout declarations travel on the EF model, and therefore
/// through the model snapshot. Model-level keys carry a serialized definition; entity-level keys carry the
/// name of the object the table participates in.
/// </summary>
/// <remarks>
/// Every key starts with <c>Raptor21:</c> so that it cannot collide with a provider's own annotations, and
/// none of them is one the provider's <c>IRelationalAnnotationProvider</c> knows, which is deliberate:
/// entity-level keys stay on the <c>IEntityType</c> and are never copied onto the relational model, so
/// EF's own differ neither emits an <c>AlterTableOperation</c> for them nor silently folds them into the
/// table's annotation bag. <see cref="CodeFirstDatabaseObjectsModelDiffer"/> reads them where they are.
/// </remarks>
public static class CodeFirstAnnotations
{
    /// <summary>
    /// Model-level prefix. <c>Raptor21:PartitionFunction:&lt;name&gt;</c> holds a
    /// <see cref="PartitionFunctionDefinition"/> serialized with <see cref="PartitionFunctionDefinition.Serialize"/>.
    /// </summary>
    public const string PartitionFunctionPrefix = "Raptor21:PartitionFunction:";

    /// <summary>
    /// Model-level prefix. <c>Raptor21:PartitionScheme:&lt;name&gt;</c> holds a
    /// <see cref="PartitionSchemeDefinition"/> serialized with <see cref="PartitionSchemeDefinition.Serialize"/>.
    /// </summary>
    public const string PartitionSchemePrefix = "Raptor21:PartitionScheme:";

    /// <summary>
    /// Entity-level: the name of the partition scheme the entity's table is created on. The same key is
    /// placed on the <c>CreateTableOperation</c> by the differ, where it carries the same value.
    /// </summary>
    public const string PartitionScheme = "Raptor21:Partition:Scheme";

    /// <summary>
    /// Entity-level: the <em>property</em> name of the partitioning column. The same key is placed on the
    /// <c>CreateTableOperation</c> by the differ, where it carries the <em>column</em> name instead — the
    /// differ is where the model is available to translate one into the other, and the SQL generator only
    /// ever sees operations.
    /// </summary>
    public const string PartitionColumn = "Raptor21:Partition:Column";

    /// <summary>Entity-level: the name of the clustered columnstore index created on the entity's table.</summary>
    public const string ClusteredColumnstoreIndex = "Raptor21:ClusteredColumnstoreIndex";

    /// <summary>
    /// Model-level prefix. <c>Raptor21:FullTextCatalog:&lt;name&gt;</c> declares a full-text catalog; the value is
    /// the format version, <c>v1</c>, because a catalog has nothing else to say about itself here.
    /// </summary>
    public const string FullTextCatalogPrefix = "Raptor21:FullTextCatalog:";

    /// <summary>
    /// Entity-level: a <see cref="FullTextIndexDeclaration"/> serialized with
    /// <see cref="FullTextIndexDeclaration.Serialize"/> — the catalog, the <em>property</em> names indexed, the key
    /// index and the change tracking. Property names, for the same reason as <see cref="PartitionColumn"/>.
    /// </summary>
    public const string FullTextIndex = "Raptor21:FullTextIndex";

    /// <summary>
    /// Operation-level, on a <c>SqlOperation</c>: <see langword="true"/> when the statement must run outside the
    /// migration's transaction. The differ sets <c>SqlOperation.SuppressTransaction</c> as well, but EF's C#
    /// scaffolder writes <c>migrationBuilder.Sql("...")</c> without that flag, so a scaffolded migration would lose
    /// it; annotations are scaffolded, and <see cref="CodeFirstDatabaseObjectsMigrationsSqlGenerator"/> restores the
    /// flag from this one when the migration runs.
    /// </summary>
    public const string SuppressTransaction = "Raptor21:SuppressTransaction";
}
