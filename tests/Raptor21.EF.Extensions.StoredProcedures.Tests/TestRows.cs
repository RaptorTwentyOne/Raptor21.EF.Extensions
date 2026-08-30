using System.Data;
using Raptor21.EF.Extensions.StoredProcedures.Execution;

namespace Raptor21.EF.Extensions.StoredProcedures.Tests;

// Rows written the way StoredProcedureGenerator.EmitRow writes them: every column is resolved by name
// through GetOrdinal at the point of use, nullable value members and reference members are IsDBNull
// guarded, and non-nullable value members are read straight through the typed getter with no guard at
// all. Because the ordinal expression is textually duplicated on both sides of a guard, a guarded column
// costs two lookups per row and an unguarded one costs one — a fact the tests measure rather than assume.
//
// These are hand-written rather than produced by running the generator here: Generator.Tests already owns
// generator coverage and says so in its csproj, and the house rule is one test project per production
// project.
//
// The generator emits its rows under `#nullable disable`, which is what lets `IsDBNull(o) ? null :
// GetString(o)` bind to a non-nullable `string` member. This project is nullable-enabled, so the
// reference-type branch needs `null!` to say the same thing: identical runtime behaviour, warning-clean
// build.

/// <summary>Covers the int, string, decimal and nullable-DateTime read paths; mirrors <c>TestContracts.GetBySku</c>.</summary>
internal sealed record ProductRow(int Id, string Sku, decimal Price, DateTime? UpdatedUtc)
    : IFromDataRecord<ProductRow>
{
    public static ProductRow FromDataRecord(IDataRecord record) => new(
        record.GetInt32(record.GetOrdinal("Id")),
        record.IsDBNull(record.GetOrdinal("Sku")) ? null! : record.GetString(record.GetOrdinal("Sku")),
        record.GetDecimal(record.GetOrdinal("Price")),
        record.IsDBNull(record.GetOrdinal("UpdatedUtc")) ? (DateTime?)null : record.GetDateTime(record.GetOrdinal("UpdatedUtc")));
}

/// <summary>
/// Covers the byte[] and Guid read paths plus a nullable value column. byte[] is the one type the
/// generator reads through <c>GetValue</c> and an unboxing cast, and Guid is the one type it reaches
/// through its fallback rather than its SpecialType switch.
/// </summary>
internal sealed record BlobRow(Guid Key, byte[] Payload, int? Version) : IFromDataRecord<BlobRow>
{
    public static BlobRow FromDataRecord(IDataRecord record) => new(
        record.GetGuid(record.GetOrdinal("Key")),
        record.IsDBNull(record.GetOrdinal("Payload")) ? null! : (byte[])record.GetValue(record.GetOrdinal("Payload")),
        record.IsDBNull(record.GetOrdinal("Version")) ? (int?)null : record.GetInt32(record.GetOrdinal("Version")));
}

/// <summary>
/// Names a column the reader will not have, for the result-set-drift case. Id is read first, exactly as
/// member order dictates, so the failure arrives from the second column and not from the first.
/// </summary>
internal sealed record DriftedRow(int Id, string MissingCol) : IFromDataRecord<DriftedRow>
{
    public static DriftedRow FromDataRecord(IDataRecord record) => new(
        record.GetInt32(record.GetOrdinal("Id")),
        record.IsDBNull(record.GetOrdinal("MissingCol")) ? null! : record.GetString(record.GetOrdinal("MissingCol")));
}
