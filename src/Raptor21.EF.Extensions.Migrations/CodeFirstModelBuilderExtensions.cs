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

    // `e => e.OccurredAt` arrives as a MemberExpression; a value-typed member arrives boxed, as
    // Convert(MemberExpression), because the lambda's return type is object?.
    private static string PropertyName<TEntity>(Expression<Func<TEntity, object?>> column)
    {
        var body = column.Body is UnaryExpression { NodeType: ExpressionType.Convert or ExpressionType.ConvertChecked } unary
            ? unary.Operand
            : column.Body;

        if (body is MemberExpression { Member: PropertyInfo or FieldInfo, Expression: ParameterExpression } member)
            return member.Member.Name;

        throw new ArgumentException(
            $"The partition column must be a single property access such as 'e => e.OccurredAt'; '{column}' is not one.",
            nameof(column));
    }
}
