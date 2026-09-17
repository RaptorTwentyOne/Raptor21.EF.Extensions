using System.Linq.Expressions;
using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Raptor21.EF.Extensions.Migrations;

/// <summary>
/// Declares SQL Server physical layout on the model — partition functions, partition schemes, a table's
/// placement on a scheme and a clustered columnstore index — as annotations, so that the declarations travel
/// through the model snapshot and <see cref="CodeFirstDatabaseObjectsModelDiffer"/> can diff them into a
/// migration next to the tables.
/// </summary>
/// <remarks>
/// Nothing here validates against the rest of the model, because a scheme may be declared after the entity
/// that uses it and the model is not complete until <c>OnModelCreating</c> returns. The differ validates the
/// finished model and names what is missing.
/// </remarks>
public static class CodeFirstModelBuilderExtensions
{
    /// <summary>
    /// Declares a partition function whose boundaries are given as T-SQL literals — <c>'20260801'</c>,
    /// <c>100</c> — exactly as they will appear in <c>FOR VALUES (...)</c>. <see cref="PartitionBoundaries"/>
    /// produces them in a stable spelling. The name is the annotation key, so declaring the same name again
    /// replaces the earlier declaration — the last call wins.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="name">The function name.</param>
    /// <param name="sqlType">The input parameter type as T-SQL spells it, e.g. <c>datetime2(3)</c>.</param>
    /// <param name="range">Whether boundaries belong to the partition on their left or their right.</param>
    /// <param name="boundaryLiterals">Boundary values as T-SQL literals, in ascending order.</param>
    public static ModelBuilder HasPartitionFunction(
        this ModelBuilder modelBuilder,
        string name,
        string sqlType,
        PartitionRange range,
        IEnumerable<string> boundaryLiterals)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(boundaryLiterals);

        var definition = new PartitionFunctionDefinition(name, sqlType, range, boundaryLiterals.ToList());
        modelBuilder.HasAnnotation(CodeFirstAnnotations.PartitionFunctionPrefix + definition.Name, definition.Serialize());
        return modelBuilder;
    }

    /// <summary>
    /// Declares a partition function whose boundaries are given as T-SQL literals. This overload exists so
    /// that a <c>string[]</c> binds here rather than to the <c>params object[]</c> overload, where each string
    /// would be quoted as a value.
    /// </summary>
    /// <inheritdoc cref="HasPartitionFunction(ModelBuilder, string, string, PartitionRange, IEnumerable{string})"/>
    public static ModelBuilder HasPartitionFunction(
        this ModelBuilder modelBuilder,
        string name,
        string sqlType,
        PartitionRange range,
        string[] boundaryLiterals)
        => HasPartitionFunction(modelBuilder, name, sqlType, range, (IEnumerable<string>)boundaryLiterals);

    /// <summary>
    /// Declares a partition function whose boundaries are given as <em>values</em> — <c>DateOnly</c>,
    /// <c>DateTime</c>, integers, <c>decimal</c> or strings — each rendered with
    /// <see cref="PartitionBoundaries.Literal"/>. A string here is a value and is quoted; pass T-SQL you have
    /// already written through the <see cref="IEnumerable{T}"/> overload instead.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="name">The function name.</param>
    /// <param name="sqlType">The input parameter type as T-SQL spells it, e.g. <c>date</c> or <c>int</c>.</param>
    /// <param name="range">Whether boundaries belong to the partition on their left or their right.</param>
    /// <param name="boundaryValues">Boundary values, in ascending order.</param>
    public static ModelBuilder HasPartitionFunction(
        this ModelBuilder modelBuilder,
        string name,
        string sqlType,
        PartitionRange range,
        params object[] boundaryValues)
    {
        ArgumentNullException.ThrowIfNull(boundaryValues);
        return HasPartitionFunction(modelBuilder, name, sqlType, range, boundaryValues.Select(PartitionBoundaries.Literal));
    }

    /// <summary>
    /// Declares a partition scheme that maps <paramref name="functionName"/> with every partition on
    /// <paramref name="filegroup"/> (<c>ALL TO ([filegroup])</c>).
    /// </summary>
    public static ModelBuilder HasPartitionScheme(
        this ModelBuilder modelBuilder,
        string name,
        string functionName,
        string filegroup = "PRIMARY")
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);

        var definition = new PartitionSchemeDefinition(name, functionName, filegroup);
        modelBuilder.HasAnnotation(CodeFirstAnnotations.PartitionSchemePrefix + definition.Name, definition.Serialize());
        return modelBuilder;
    }

    /// <summary>
    /// Creates the entity's table on <paramref name="scheme"/>, partitioned by the column
    /// <paramref name="column"/> maps to: <c>CREATE TABLE ... ON [scheme]([column])</c>. Only a new table can
    /// be placed; moving an existing table onto, off or between schemes is refused by the differ, because it
    /// means rebuilding the clustered index over the data.
    /// </summary>
    public static EntityTypeBuilder<TEntity> OnPartitionScheme<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        string scheme,
        Expression<Func<TEntity, object?>> column)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(column);
        return OnPartitionScheme(entityTypeBuilder, scheme, PropertyName(column));
    }

    /// <summary>
    /// Creates the entity's table on <paramref name="scheme"/>, partitioned by the column the property named
    /// <paramref name="propertyName"/> maps to — the overload for a shadow property.
    /// </summary>
    /// <inheritdoc cref="OnPartitionScheme{TEntity}(EntityTypeBuilder{TEntity}, string, Expression{Func{TEntity, object?}})"/>
    public static EntityTypeBuilder<TEntity> OnPartitionScheme<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        string scheme,
        string propertyName)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(scheme);
        ArgumentException.ThrowIfNullOrWhiteSpace(propertyName);

        // The PROPERTY name is what goes on the model: it is what the snapshot can express, and the column
        // it maps to may change through HasColumnName without the placement changing. The differ translates
        // it to the column name at the one point where both the model and the operation are in hand.
        entityTypeBuilder.HasAnnotation(CodeFirstAnnotations.PartitionScheme, scheme.Trim());
        entityTypeBuilder.HasAnnotation(CodeFirstAnnotations.PartitionColumn, propertyName.Trim());
        return entityTypeBuilder;
    }

    /// <summary>
    /// Creates a clustered columnstore index named <paramref name="name"/> on the entity's table, right after
    /// the table and before any nonclustered index. The table must have no other clustered index: declare it
    /// <c>HasNoKey()</c>, or keep its key with <c>HasKey(...).IsClustered(false)</c>.
    /// </summary>
    public static EntityTypeBuilder<TEntity> HasClusteredColumnstoreIndex<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        string name)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        entityTypeBuilder.HasAnnotation(CodeFirstAnnotations.ClusteredColumnstoreIndex, name.Trim());
        return entityTypeBuilder;
    }

    /// <summary>
    /// Declares a full-text catalog: <c>CREATE FULLTEXT CATALOG [name]</c>, guarded by <c>IF NOT EXISTS</c>, so
    /// declaring a catalog the database already has is harmless. Every <see cref="HasFullTextIndex{TEntity}(EntityTypeBuilder{TEntity}, string, Expression{Func{TEntity, object?}}, string?, FullTextChangeTracking)"/>
    /// names a declared catalog; the differ refuses one that is not.
    /// </summary>
    /// <param name="modelBuilder">The model builder.</param>
    /// <param name="name">The catalog name.</param>
    public static ModelBuilder HasFullTextCatalog(this ModelBuilder modelBuilder, string name)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        if (name.Contains('|'))
            throw new ArgumentException($"'{name}' contains '|', which the serialized form uses as its separator.", nameof(name));

        modelBuilder.HasAnnotation(CodeFirstAnnotations.FullTextCatalogPrefix + name.Trim(), "v1");
        return modelBuilder;
    }

    /// <summary>
    /// Creates a full-text index on the entity's table over the given properties:
    /// <c>CREATE FULLTEXT INDEX ON [table] ([col], ...) KEY INDEX [key] ON [catalog] WITH CHANGE_TRACKING AUTO</c>,
    /// emitted after every index the migration creates on the table, outside the migration's transaction as
    /// SQL Server requires. The columns take the server's default word-breaker language; use the
    /// <see cref="FullTextColumn"/> overload to set one per column.
    /// </summary>
    /// <param name="entityTypeBuilder">The entity type builder.</param>
    /// <param name="catalog">A catalog declared with <see cref="HasFullTextCatalog"/>.</param>
    /// <param name="columns">One property (<c>c => c.Name</c>) or several (<c>c => new { c.Name, c.Email }</c>); character or xml columns only.</param>
    /// <param name="keyIndex">
    /// The name of the unique, single-column, non-nullable index the full-text index is keyed on;
    /// <see langword="null"/> for the primary key, which must then be a single column.
    /// </param>
    /// <param name="changeTracking">How the index is kept current; <see cref="FullTextChangeTracking.Auto"/> unless said otherwise.</param>
    public static EntityTypeBuilder<TEntity> HasFullTextIndex<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        string catalog,
        Expression<Func<TEntity, object?>> columns,
        string? keyIndex = null,
        FullTextChangeTracking changeTracking = FullTextChangeTracking.Auto)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(columns);
        return HasFullTextIndex(
            entityTypeBuilder, catalog, PropertyNames(columns).Select(n => new FullTextColumn(n)), keyIndex, changeTracking);
    }

    /// <summary>
    /// Creates a full-text index on the entity's table over the given columns, each named by its
    /// <em>property</em> and optionally carrying its word-breaker language as an LCID — the overload for a
    /// shadow property or a column whose language is not the server default.
    /// </summary>
    /// <inheritdoc cref="HasFullTextIndex{TEntity}(EntityTypeBuilder{TEntity}, string, Expression{Func{TEntity, object?}}, string?, FullTextChangeTracking)"/>
    public static EntityTypeBuilder<TEntity> HasFullTextIndex<TEntity>(
        this EntityTypeBuilder<TEntity> entityTypeBuilder,
        string catalog,
        IEnumerable<FullTextColumn> columns,
        string? keyIndex = null,
        FullTextChangeTracking changeTracking = FullTextChangeTracking.Auto)
        where TEntity : class
    {
        ArgumentNullException.ThrowIfNull(entityTypeBuilder);
        ArgumentNullException.ThrowIfNull(columns);

        // PROPERTY names, like the partition column: the snapshot can express them and the differ translates
        // them to column names once it has the table. The key index is a database name already — it is the
        // name EF gives the index, which a HasDatabaseName on the index changes, and the snapshot carries that.
        var declaration = new FullTextIndexDeclaration(catalog, columns.ToList(), keyIndex, changeTracking);
        entityTypeBuilder.HasAnnotation(CodeFirstAnnotations.FullTextIndex, declaration.Serialize());
        return entityTypeBuilder;
    }

    // `e => e.OccurredAt` arrives as a MemberExpression; a value-typed member arrives boxed, as
    // Convert(MemberExpression), because the lambda's return type is object?.
    private static string PropertyName<TEntity>(Expression<Func<TEntity, object?>> column)
    {
        var body = Unbox(column.Body);

        if (body is MemberExpression { Member: PropertyInfo or FieldInfo, Expression: ParameterExpression } member)
            return member.Member.Name;

        throw new ArgumentException(
            $"The partition column must be a single property access such as 'e => e.OccurredAt'; '{column}' is not one.",
            nameof(column));
    }

    // `c => c.Name` or `c => new { c.Name, c.Email }` — the two shapes EF's own HasIndex accepts.
    private static IReadOnlyList<string> PropertyNames<TEntity>(Expression<Func<TEntity, object?>> columns)
    {
        var body = Unbox(columns.Body);

        if (body is MemberExpression { Member: PropertyInfo or FieldInfo, Expression: ParameterExpression } member)
            return [member.Member.Name];

        if (body is NewExpression { Arguments.Count: > 0 } anonymous
            && anonymous.Arguments.All(a => Unbox(a) is MemberExpression { Member: PropertyInfo or FieldInfo, Expression: ParameterExpression }))
            return anonymous.Arguments.Select(a => ((MemberExpression)Unbox(a)).Member.Name).ToList();

        throw new ArgumentException(
            $"The full-text columns must be a property access such as 'c => c.Name' or an anonymous object of them such as " +
            $"'c => new {{ c.Name, c.Email }}'; '{columns}' is neither.",
            nameof(columns));
    }

    private static Expression Unbox(Expression expression)
        => expression is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary
            ? unary.Operand
            : expression;
}
