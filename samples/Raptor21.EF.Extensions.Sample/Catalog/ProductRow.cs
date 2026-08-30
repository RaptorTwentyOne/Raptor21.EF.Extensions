using Raptor21.EF.Extensions.StoredProcedures.Generated;

namespace Raptor21.EF.Extensions.Sample.Catalog;

/// <summary>
/// A result row. The generator emits <c>FromDataRecord</c> with one <c>GetOrdinal</c> per column, so
/// materialisation costs no reflection and survives trimming.
/// </summary>
/// <remarks>
/// <para>
/// Written out by hand, and deliberately not converted to <c>[SqlRow(Entity = typeof(Product))]</c> -
/// this is the living proof that the positional form still works exactly as it always did, and it is
/// also the sharpest illustration of why the bound form imposes an ordering contract. The members here
/// are <c>Id, Sku, Name, Price, UpdatedUtc</c>, which is what <c>Product_GetBySku.sql</c> and
/// <c>Product_List.sql</c> select. The model's order for <c>Product</c> is
/// <c>Id, Name, Price, Sku, UpdatedUtc</c> - primary key first, then alphabetical - so binding this row
/// to the entity would swap <c>Sku</c> and <c>Name</c>, and since result columns are validated
/// POSITIONALLY the two procedures would have to be rewritten to match.
/// </para>
/// <para>
/// Reach for this form whenever the shape is the procedure's own rather than the model's: a projection
/// or an aggregate no entity type describes, a <c>[SqlColumn]</c> rename (which has no attachment point
/// on a bound row, because the generator owns the members there), a member order chosen to match a
/// <c>SELECT</c> that already exists, an entity whose columns are not exclusively its own, or a
/// value-converted property whose stored type the generator cannot convert. See
/// <see cref="ProductListRow"/> for the other side of the same choice.
/// </para>
/// </remarks>
[SqlRow]
public partial record ProductRow(
    [SqlColumn("Id")] int Id,
    string Sku,
    string Name,
    decimal Price,
    DateTime UpdatedUtc);
