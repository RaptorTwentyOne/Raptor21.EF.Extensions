using Raptor21.EF.Extensions.StoredProcedures.Generated;

namespace Raptor21.EF.Extensions.Sample.Catalog;

/// <summary>
/// Every method here is a declaration, not an implementation. The generator writes the body, the
/// parameter binding and an <c>IStoredProcedureContract</c> per method, then lists them all in
/// <c>Raptor21.EF.Extensions.Sample.Generated.GeneratedProcedureRegistry.All</c>.
/// </summary>
[StoredProcedureGroup]
public partial class CatalogProcedures
{
    /// <summary>Insert or update by SKU. Returns the RETURN value and the row id via an OUTPUT parameter.</summary>
    [StoredProcedure("dbo.Product_Upsert")]
    public partial Task<(int ReturnValue, int Id)> UpsertAsync(
        [Sql("@Sku", 32)] string sku,
        [Sql("@Name", 128)] string name,
        [Sql("@Price", Precision = 18, Scale = 2)] decimal price,
        [Sql("@Id", "int", Output = true)] int id,
        CancellationToken ct = default);

    /// <summary>One result set, typed rows.</summary>
    [StoredProcedure("dbo.Product_GetBySku")]
    public partial Task<IReadOnlyList<ProductRow>> GetBySkuAsync(
        [Sql("@Sku", 32)] string sku,
        CancellationToken ct = default);

    /// <summary>Result set plus the procedure's RETURN value (here: the total row count).</summary>
    [StoredProcedure("dbo.Product_List")]
    public partial Task<(int ReturnValue, IReadOnlyList<ProductRow> Rows)> ListAsync(
        [Sql("@Take", "int")] int take,
        CancellationToken ct = default);

    /// <summary>No result set, no RETURN value.</summary>
    [StoredProcedure("dbo.Product_TouchStamp")]
    public partial Task TouchStampAsync(
        [Sql("@Id", "int")] int id,
        CancellationToken ct = default);
}
