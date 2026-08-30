using Raptor21.EF.Extensions.StoredProcedures.Generated;

namespace Raptor21.EF.Extensions.Sample.Catalog;

/// <summary>
/// Every method here is a declaration, not an implementation. The generator writes the body, the
/// parameter binding and an <c>IStoredProcedureContract</c> per method, then lists them all in
/// <c>Raptor21.EF.Extensions.Sample.Generated.GeneratedProcedureRegistry.All</c>.
/// </summary>
/// <remarks>
/// <para>
/// The methods are split on purpose between the two ways of telling the generator what a
/// parameter is, so that building the sample proves both. <see cref="UpsertAsync"/>,
/// <see cref="GetBySkuAsync"/> and <see cref="ListViewAsync"/> carry no <c>[Sql]</c> at all: their
/// facts are read out of the <c>CREATE PROCEDURE</c> headers in <c>DbScripts</c>, which the package's
/// buildTransitive targets hand to the compiler as <c>AdditionalFiles</c>. A bare <c>string sku</c> is
/// looked up as <c>@sku</c>, matches <c>@Sku varchar(32)</c> ignoring case and is emitted with the
/// file's spelling, type and length; <c>decimal price</c> picks up <c>decimal(18,2)</c>; and
/// <c>int id</c> becomes an OUTPUT parameter because <c>Product_Upsert.sql</c> declares
/// <c>@Id int OUTPUT</c> - nothing in this file says so.
/// </para>
/// <para>
/// <see cref="ListAsync"/> and <see cref="TouchStampAsync"/> state every facet themselves, which is
/// what this whole file used to do and what still compiles with no script in sight.
/// </para>
/// <para>
/// Prefer the inferred form when the .sql is the source of truth and already travels with the
/// assembly, because a length or a precision repeated in C# is a second copy that goes quietly
/// wrong the day the procedure changes. Reach for an explicit <c>[Sql]</c> when the generator
/// cannot see the script - it is produced at deploy time, lives in another repository, or discovery
/// is off via <c>R21SqlScriptDiscovery=false</c> - when the C# parameter cannot be named after the
/// SQL one, since matching is by name and only by name, or when the declaration should pin the
/// procedure's shape rather than follow it: a stated facet wins over the file and the disagreement
/// is reported as SPG010 instead of being adopted in silence. The merge is per facet rather than
/// per parameter, so the two styles are one rule seen from either end and not a choice the file has
/// to make once for all of its methods.
/// </para>
/// <para>
/// The same choice exists one level up, for what a result ROW is rather than what a parameter is, and
/// the two are independent: <see cref="ListViewAsync"/> takes its parameter from the .sql header and
/// its row members from the EF model, and neither channel knows the other exists. There are two
/// spellings and both compile side by side in this file, on purpose, so that building the sample
/// proves each one.
/// </para>
/// <para>
/// Hand-write a positional record - <see cref="ProductRow"/>, used by <see cref="GetBySkuAsync"/> and
/// <see cref="ListAsync"/> - when the shape is the procedure's own: a projection or an aggregate no
/// entity type describes, a column that needs a <c>[SqlColumn]</c> rename, a member order chosen to
/// match an existing <c>SELECT</c>, an entity whose columns are not exclusively its own (table
/// splitting, an inheritance hierarchy, an owned type), or a value-converted property whose stored type
/// the generator would have to convert and cannot. This form is the floor: it needs no model, no script
/// and no inference, and it keeps working forever.
/// </para>
/// <para>
/// Bind the row to an entity - <see cref="ProductListRow"/>,
/// <c>[SqlRow(Entity = typeof(ProductListResult))]</c>, used by <see cref="ListViewAsync"/> - when the
/// procedure returns a shape the model already describes, so that the member names, the .NET types, the
/// column names and the nullability come from the one place they are already written down. Name the
/// entity by hand whenever the procedure's <c>FROM</c> clause does not name it, or names the wrong one:
/// <c>Product_ListView.sql</c> reads <c>FROM dbo.Product</c> and yet its row is
/// <c>ProductListResult</c>, a keyless <c>ToView</c> shape, which is exactly the case where a reader
/// that trusted the <c>FROM</c> alone would be confidently wrong.
/// </para>
/// </remarks>
[StoredProcedureGroup]
public partial class CatalogProcedures
{
    /// <summary>Insert or update by SKU. Returns the RETURN value and the row id via an OUTPUT parameter.</summary>
    [StoredProcedure("dbo.Product_Upsert")]
    public partial Task<(int ReturnValue, int Id)> UpsertAsync(
        string sku,
        string name,
        decimal price,
        int id,
        CancellationToken ct = default);

    /// <summary>One result set, typed rows.</summary>
    [StoredProcedure("dbo.Product_GetBySku")]
    public partial Task<IReadOnlyList<ProductRow>> GetBySkuAsync(
        string sku,
        CancellationToken ct = default);

    /// <summary>Result set plus the procedure's RETURN value (here: the total row count).</summary>
    [StoredProcedure("dbo.Product_List")]
    public partial Task<(int ReturnValue, IReadOnlyList<ProductRow> Rows)> ListAsync(
        [Sql("@Take", "int")] int take,
        CancellationToken ct = default);

    /// <summary>
    /// Rows whose shape is the model's, not this file's: <see cref="ProductListRow"/> takes its members
    /// from <c>ProductListResult</c> in the EF model snapshot. <c>take</c> carries no <c>[Sql]</c>,
    /// because <c>Product_ListView.sql</c>'s header already says <c>@Take int</c> - the two inference
    /// channels compose without knowing about each other.
    /// </summary>
    [StoredProcedure("dbo.Product_ListView")]
    public partial Task<IReadOnlyList<ProductListRow>> ListViewAsync(
        int take,
        CancellationToken ct = default);

    /// <summary>No result set, no RETURN value.</summary>
    [StoredProcedure("dbo.Product_TouchStamp")]
    public partial Task TouchStampAsync(
        [Sql("@Id", "int")] int id,
        CancellationToken ct = default);
}
