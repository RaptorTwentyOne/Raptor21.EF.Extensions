namespace Raptor21.EF.Extensions.Sample.Data;

/// <summary>
/// A result shape, not a table. EF's designated way to declare one is a keyless entity type, which is
/// what a team arriving from <c>FromSqlRaw</c> already has, and it is what
/// <c>[SqlRow(Entity = typeof(ProductListResult))]</c> binds <c>ProductListRow</c> to.
/// </summary>
/// <remarks>
/// <para>
/// The view named by <c>ToView</c> in <c>CatalogDbContext.OnModelCreating</c> is never queried by
/// this sample, never created by a migration - EF excludes views from migrations - and need not exist in
/// the database at all. It is there to say "this is not a table". The generator reads only the property
/// list, the column names, the column types and the nullability.
/// </para>
/// <para>
/// Nothing else in the model maps to <c>vw_ProductList</c>, this type has no base type, no derived types
/// and no owner, and it declares no complex or split mapping. That is the whole of the rule the binding
/// requires: its columns are exclusively its own. On a shared target - table splitting, a TPH hierarchy,
/// an owner and its owned type - a column is nullable whenever any entity mapped there is optional while
/// the snapshot still records nullability per property, so the generator refuses to guess rather than
/// emit a read that throws on the first row.
/// </para>
/// </remarks>
public sealed class ProductListResult
{
    public int Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public string Sku { get; set; } = string.Empty;
}
