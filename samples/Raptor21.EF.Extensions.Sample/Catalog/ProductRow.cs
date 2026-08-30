using Raptor21.EF.Extensions.StoredProcedures.Generated;

namespace Raptor21.EF.Extensions.Sample.Catalog;

/// <summary>
/// A result row. The generator emits <c>FromDataRecord</c> with one <c>GetOrdinal</c> per column, so
/// materialisation costs no reflection and survives trimming.
/// </summary>
[SqlRow]
public partial record ProductRow(
    [SqlColumn("Id")] int Id,
    string Sku,
    string Name,
    decimal Price,
    DateTime UpdatedUtc);
