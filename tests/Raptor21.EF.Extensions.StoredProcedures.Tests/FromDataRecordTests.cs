using System.Data;
using Raptor21.EF.Extensions.StoredProcedures.Execution;
using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Tests;

/// <summary>
/// Covers row materialisation: <c>IFromDataRecord&lt;TSelf&gt;.FromDataRecord</c> against a hand-written
/// record. This is the one seam the design already got right — the contract is
/// <see cref="IDataRecord"/>, an interface, so no production change was needed to reach any of it and no
/// server is involved. The rows are the <c>TestRows</c> fixtures, written exactly as
/// <c>StoredProcedureGenerator.EmitRow</c> writes them.
/// </summary>
public class FromDataRecordTests
{
    private static readonly string[] ProductColumns = ["Id", "Sku", "Price", "UpdatedUtc"];

    private static readonly string[] BlobColumns = ["Key", "Payload", "Version"];

    private static readonly DateTime Stamp = new(2026, 8, 30, 12, 30, 0, DateTimeKind.Utc);

    private static readonly Guid Key = new("6f9619ff-8b86-d011-b42d-00c04fc964ff");

    [Fact]
    public void FromDataRecord_NonNullableValueColumn_ReadsThroughTypedGetter()
    {
        var reader = ReaderAt(ProductColumns, [7, "SKU-1", 1.50m, Stamp]);

        var row = ProductRow.FromDataRecord(reader);

        Assert.Equal(7, row.Id);
        Assert.Equal(1.50m, row.Price);
    }

    [Fact]
    public void FromDataRecord_NullableValueColumn_DbNullBecomesNull()
    {
        var reader = ReaderAt(ProductColumns, [7, "SKU-1", 1.50m, DBNull.Value]);

        var row = ProductRow.FromDataRecord(reader);

        // The emitted shape is `IsDBNull(ord) ? (DateTime?)null : GetDateTime(ord)`, so for a NULL cell the
        // typed getter is never reached and the property carries no value rather than default(DateTime).
        Assert.Null(row.UpdatedUtc);
    }

    [Fact]
    public void FromDataRecord_NonNullableReferenceColumn_DbNullStillBecomesNull()
    {
        var reader = ReaderAt(ProductColumns, [7, DBNull.Value, 1.50m, Stamp]);

        var row = ProductRow.FromDataRecord(reader);

        // Surprising, but it is the contract and it should be written down. The generator emits its rows
        // under `#nullable disable` and gives every reference member the same unconditional IsDBNull guard
        // whatever its annotation, and StoredProcedureValidator deliberately declines to enforce
        // reference-type nullability because reflection cannot tell `string` from `string?`. Between the
        // two, nothing anywhere stops a NULL from a NOT NULL column landing in a non-nullable property.
        Assert.Null(row.Sku);
    }

    [Fact]
    public void FromDataRecord_NonNullableValueColumnWithDbNull_Throws()
    {
        var reader = ReaderAt(ProductColumns, [DBNull.Value, "SKU-1", 1.50m, Stamp]);

        // Non-nullable value members get no IsDBNull guard at all, so the unboxing cast inside GetInt32 is
        // the only thing between a NULL cell and a wrong answer. Throwing is the better of the two
        // outcomes, but it means correctness here rests entirely on the validator having compared the
        // contract against the live schema at startup — a drifted column is a runtime cast error, not a
        // diagnosed mismatch.
        Assert.Throws<InvalidCastException>(() => ProductRow.FromDataRecord(reader));
    }

    [Fact]
    public void FromDataRecord_ByteArrayColumn_UsesGetValueCastAndIsDbNullGuarded()
    {
        var reader = new FakeDbDataReader(
            BlobColumns,
            [Key, new byte[] { 1, 2, 3 }, 4],
            [Key, DBNull.Value, DBNull.Value]);

        Assert.True(reader.Read());
        var withPayload = BlobRow.FromDataRecord(reader);
        Assert.True(reader.Read());
        var withoutPayload = BlobRow.FromDataRecord(reader);

        // byte[] is the one column kind the generator reads through GetValue and an unboxing cast instead
        // of a typed getter, so a provider handing back anything else fails as a bare InvalidCastException
        // naming neither the column nor the procedure. The guard around it is the ordinary reference-member
        // one, so a NULL varbinary is null rather than an empty array.
        Assert.Equal(new byte[] { 1, 2, 3 }, withPayload.Payload);
        Assert.Null(withoutPayload.Payload);
    }

    [Fact]
    public void FromDataRecord_GuidColumn_UsesGetGuid()
    {
        var reader = ReaderAt(BlobColumns, [Key, new byte[] { 9 }, 1]);

        var row = BlobRow.FromDataRecord(reader);

        // Guid reaches GetGuid through the generator's fallback rather than through its SpecialType switch,
        // so it is the one read path no switch arm covers and it earns a case of its own.
        Assert.Equal(Key, row.Key);
    }

    [Fact]
    public void FromDataRecord_ResolvesColumnsByNameNotPosition()
    {
        var reader = ReaderAt(["UpdatedUtc", "Price", "Sku", "Id"], [Stamp, 9.99m, "SKU-2", 42]);

        var row = ProductRow.FromDataRecord(reader);

        // Materialisation is completely order-independent — every column is found by name through
        // GetOrdinal. StoredProcedureValidator does not agree: it walks ResultColumns against the catalog
        // strictly positionally and raises "Result column[i] name mismatch", so this very procedure would
        // be rejected at startup even though the reader would have materialised it perfectly. The two
        // halves of the library disagree about what a result set is; see StoredProcedureValidatorTypeTests.
        Assert.Equal(42, row.Id);
        Assert.Equal("SKU-2", row.Sku);
        Assert.Equal(9.99m, row.Price);
        Assert.Equal(Stamp, row.UpdatedUtc);
    }

    [Fact]
    public void FromDataRecord_MissingColumn_ThrowsWithNoProcedureContext()
    {
        var reader = ReaderAt(["Id"], [5]);

        var ex = Assert.Throws<IndexOutOfRangeException>(() => DriftedRow.FromDataRecord(reader));

        Assert.Contains("MissingCol", ex.Message);

        // And nothing beyond the column name. Every diagnostic the executor raises itself is prefixed with
        // the bracketed "[schema].[name]" it builds before touching the connection, so the absence of a
        // bracket is the absence of any procedure context at all. That is the shape result-set drift takes:
        // ExecuteReturnResultSetAsync reads contract.ResultColumns exactly once, only to check it is not
        // empty, and never compares it to the reader — so a changed SELECT list surfaces as a bare index
        // error out of auto-generated source that names neither the procedure nor the row type.
        Assert.DoesNotContain("[", ex.Message);
    }

    [Fact]
    public void FromDataRecord_ResolvesTheOrdinalOncePerColumnPerRow()
    {
        var reader = new FakeDbDataReader(
            ProductColumns,
            [1, "a", 1m, Stamp],
            [2, "b", 2m, Stamp],
            [3, "c", 3m, Stamp]);

        // Cleared so the counts below are attributable to the act alone, and stay honest if the arrange
        // ever grows a lookup of its own.
        reader.GetOrdinalCalls.Clear();

        while (reader.Read())
            ProductRow.FromDataRecord(reader);

        // The cost, pinned as a fact rather than left to be assumed: an N-column, M-row result performs
        // N*M ordinal lookups, doubling for every column that carries an IsDBNull guard — that is, for
        // every reference member and every nullable value member — because the generator writes the ordinal
        // expression out again on both sides of the conditional instead of hoisting it into a local. A
        // future hoisting optimisation should move these numbers, and should have to come here and say so.
        Assert.Equal(3, reader.GetOrdinalCalls["Id"]);
        Assert.Equal(3, reader.GetOrdinalCalls["Price"]);
        Assert.Equal(6, reader.GetOrdinalCalls["Sku"]);
        Assert.Equal(6, reader.GetOrdinalCalls["UpdatedUtc"]);
    }

    [Fact]
    public void FromDataRecord_ReceivesTheReaderAsIDataRecord()
    {
        IDataRecord record = ReaderAt(ProductColumns, [11, "SKU-3", 3.25m, Stamp]);

        var row = Materialise<ProductRow>(record);

        // The materialiser was reached through the static-abstract member on IFromDataRecord<TSelf>,
        // resolved from the type parameter's constraint rather than from the concrete type — a constrained
        // call bound at compile time. Nothing reflected over ProductRow to get here, which is the whole
        // reason a consumer can publish NativeAOT, and the reason a hand-written IDataRecord is a
        // sufficient test double for the entire read path.
        Assert.Equal(11, row.Id);
        Assert.Equal("SKU-3", row.Sku);
    }

    /// <summary>
    /// Calls the materialiser the way <c>StoredProcedureExecutor.ExecuteReturnResultSetAsync</c> does:
    /// through the <c>TRow : IFromDataRecord&lt;TRow&gt;</c> constraint, never through the concrete type.
    /// </summary>
    private static TRow Materialise<TRow>(IDataRecord record) where TRow : IFromDataRecord<TRow> =>
        TRow.FromDataRecord(record);

    /// <summary>
    /// Builds a single-row reader already advanced onto that row. The fake starts before the first row
    /// exactly as a real reader does, and every test here materialises from a positioned reader, so the
    /// advance is factored out rather than repeated in each arrange.
    /// </summary>
    private static FakeDbDataReader ReaderAt(string[] columns, params object?[][] rows)
    {
        var reader = new FakeDbDataReader(columns, rows);
        Assert.True(reader.Read());
        return reader;
    }
}
