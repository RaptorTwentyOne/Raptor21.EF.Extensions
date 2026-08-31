using System.Data;
using System.Data.Common;
using Microsoft.Data.SqlClient;
using Raptor21.EF.Extensions.StoredProcedures.Execution;
using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Tests;

/// <summary>
/// What the executor does once the command has already run: <c>CollectOutputs</c> over a finished
/// parameter collection, and <c>MaterialiseRowsAsync</c> over an open reader.
/// </summary>
/// <remarks>
/// Both helpers take the abstract ADO.NET types — <see cref="DbParameterCollection"/> and
/// <see cref="DbDataReader"/> — rather than SqlClient's <c>SqlParameterCollection</c> and
/// <c>SqlDataReader</c>, which are sealed or have no accessible constructor. That is the whole reason
/// anything after <c>Execute*Async</c> is reachable offline. The production types derive from these two
/// and pass straight through, so these are the same code paths a real call takes; only the source of the
/// values differs.
/// </remarks>
public class StoredProcedureExecutorResultTests
{
    private static readonly string[] ProductColumns = ["Id", "Sku", "Price", "UpdatedUtc"];

    private static readonly DateTime Stamp = new(2026, 8, 30, 12, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void CollectOutputs_SkipsTheReturnSlotAtIndexZero()
    {
        // A RETURN value the server really could have left behind, so that a loop starting at zero would
        // fail as a wrong output value rather than as an off-by-one count.
        var parameters = new FakeDbParameterCollection(
            FakeDbParameterCollection.ReturnParameter(99),
            new SqlParameter { ParameterName = "@out", Direction = ParameterDirection.Output, Value = 7 });

        var outputs = StoredProcedureExecutor.CollectOutputs(parameters);

        // The skip is positional and belt-and-braces: ReturnValue is neither Output nor InputOutput, so
        // the direction filter one line below would have excluded the slot anyway. What starting at index
        // 1 really pins is the convention the whole executor shares — BuildCommand always puts the RETURN
        // parameter first, and every read of a RETURN value goes straight to Parameters[0].
        var only = Assert.Single(outputs);
        Assert.Equal(7, only);
    }

    [Fact]
    public void CollectOutputs_ReturnsOnlyOutputParametersInCollectionOrder()
    {
        var parameters = FakeDbParameterCollection.WithReturnSlot(
            new SqlParameter { ParameterName = "@in", Direction = ParameterDirection.Input, Value = 1 },
            new SqlParameter { ParameterName = "@out1", Direction = ParameterDirection.Output, Value = "a" },
            new SqlParameter { ParameterName = "@in2", Direction = ParameterDirection.Input, Value = 2 },
            new SqlParameter { ParameterName = "@out2", Direction = ParameterDirection.Output, Value = "b" });

        var outputs = StoredProcedureExecutor.CollectOutputs(parameters);

        // The list index is the Nth OUTPUT parameter, not the Nth contract parameter: inputs are dropped
        // rather than left as holes. That is exactly the assumption the generator encodes when it reads
        // the k-th tuple element from __outs[k - 1], and the only thing it ever checks is that the two
        // counts agree — OutputArityMismatch compares how many parameters are marked Output against how
        // many tuple elements follow the RETURN value, and nothing anywhere compares their order or their
        // types. Reorder two OUTPUT parameters in a signature and the values land in the wrong tuple
        // elements in silence, unless their types happen to differ enough for a cast to throw.
        Assert.Equal(new object?[] { "a", "b" }, outputs);
    }

    [Fact]
    public void CollectOutputs_DbNullValue_BecomesNull()
    {
        var parameters = FakeDbParameterCollection.WithReturnSlot(
            new SqlParameter { ParameterName = "@out", Direction = ParameterDirection.Output, Value = DBNull.Value });

        var outputs = StoredProcedureExecutor.CollectOutputs(parameters);

        // This normalisation is what the generated conversions are written against. A nullable tuple
        // element becomes `__outs[i] is null ? (T?)null : (T)__outs[i]!`, which only reads as "the
        // procedure returned NULL" because DBNull never reaches it. A non-nullable element gets
        // `(int)__outs[i]!` instead, so the same NULL unboxes a null reference and surfaces as a
        // NullReferenceException out of auto-generated source naming neither the procedure nor the
        // parameter. Nothing stops a NULL arriving in a non-nullable slot — the same gap
        // FromDataRecordTests pins on the result-set side.
        Assert.Null(Assert.Single(outputs));
    }

    [Fact]
    public void CollectOutputs_InputOutputParameter_IsCollected()
    {
        var parameters = FakeDbParameterCollection.WithReturnSlot(
            new SqlParameter { ParameterName = "@io", Direction = ParameterDirection.InputOutput, Value = 5 });

        var outputs = StoredProcedureExecutor.CollectOutputs(parameters);

        // The InputOutput arm of the filter works, and nothing this library builds can reach it:
        // ProcParamSpec carries a single IsOutput bool and CreateParameter turns it into Output or Input
        // and nothing else, which BuildCommand_OutputSpec_BindsDirectionOutputNeverInputOutput pins from
        // the other side. Read together the two say the arm is live code that is dead in practice — it
        // costs nothing, but a reader should not take it as evidence that INPUT/OUTPUT is supported.
        Assert.Equal(5, Assert.Single(outputs));
    }

    [Fact]
    public void CollectOutputs_NoOutputParameters_ReturnsEmptyList()
    {
        var parameters = FakeDbParameterCollection.WithReturnSlot(
            new SqlParameter { ParameterName = "@a", Direction = ParameterDirection.Input, Value = 1 },
            new SqlParameter { ParameterName = "@b", Direction = ParameterDirection.Input, Value = 2 });

        var outputs = StoredProcedureExecutor.CollectOutputs(parameters);

        // Empty, not null, and allocated unconditionally: a caller can destructure the result of
        // ExecuteWithOutputsAsync without a null check, at the price of one list per call even when there
        // is nothing to put in it. No generated call site pays that price — a method with no OUTPUT
        // parameters takes the ExecuteReturnInt32Async path instead — so it lands only on someone calling
        // the executor directly.
        Assert.Empty(outputs);
    }

    [Fact]
    public async Task MaterialiseRowsAsync_ReturnsOneRowPerReadInReaderOrder()
    {
        var reader = ProductReader(10, 20, 30);

        var rows = await StoredProcedureExecutor.MaterialiseRowsAsync<ProductRow>(reader, default);

        // One row per successful ReadAsync, appended in the order the reader served them: the list is a
        // faithful transcript of the result set, with nothing sorted, de-duplicated or filtered. It is
        // also fully buffered before the caller sees a single row, which is what allows the reader to be
        // closed in time for the RETURN value to be read — and equally means a large result set is
        // materialised whole whether the caller intends to consume all of it or not.
        Assert.Equal(new[] { 10, 20, 30 }, rows.Select(r => r.Id));
    }

    [Fact]
    public async Task MaterialiseRowsAsync_EmptyResultSet_ReturnsEmptyListAndStillCloses()
    {
        // Columns declared, no rows: a procedure whose SELECT matched nothing.
        var reader = new FakeDbDataReader(ProductColumns);

        var rows = await StoredProcedureExecutor.MaterialiseRowsAsync<ProductRow>(reader, default);

        // The close sits after the loop rather than inside it, so an empty result set is drained and
        // closed exactly like a full one. That matters because the RETURN value of an empty result set is
        // every bit as interesting as that of a populated one — "how many rows did you not find" is a
        // perfectly ordinary thing for a procedure to report — and the caller reads it the moment this
        // method returns.
        Assert.Empty(rows);
        Assert.Equal(1, reader.CloseCallCount);
    }

    [Fact]
    public async Task MaterialiseRowsAsync_ClosesTheReaderExactlyOnceBeforeReturning()
    {
        var reader = ProductReader(10, 20);

        await StoredProcedureExecutor.MaterialiseRowsAsync<ProductRow>(reader, default);

        // Load bearing and redundant-looking, which is the combination that gets refactored away.
        // SqlClient only populates the RETURN and OUTPUT parameters once the TDS stream has been drained
        // and the reader closed, and ExecuteReturnResultSetAsync reads cmd.Parameters[0] on the very next
        // line. Move the close after that read — or delete it and let the caller's `await using` do it at
        // the end of the method — and the RETURN value silently becomes 0, because the read is
        // `is int i ? i : 0` and an unpopulated slot is null. Not one assertion about the returned rows
        // would notice.
        Assert.Equal(1, reader.CloseCallCount);
        Assert.Equal(0, reader.RowsReadAfterClose);
    }

    [Fact]
    public async Task MaterialiseRowsAsync_SecondResultSet_IsDrainedButNeverMaterialised()
    {
        var reader = ProductReader(10, 20)
            .AddResultSet(ProductColumns, [99, "SKU-99", 9.00m, Stamp]);

        var rows = await StoredProcedureExecutor.MaterialiseRowsAsync<ProductRow>(reader, default);

        // Only the first result set becomes rows: a contract carries a single ColumnSpec list and
        // ExecuteReturnResultSetAsync returns a single IReadOnlyList<TRow>, so a second SELECT is a shape
        // the library cannot express rather than a check it forgot. Worth knowing before someone adds one
        // to a working procedure and wonders where the extra rows went.
        Assert.Equal(new[] { 10, 20 }, rows.Select(r => r.Id));

        // But the reader IS advanced past it, and that is not tidiness. ADO.NET surfaces an error the
        // procedure raises after its last result set only when the reader is advanced; CloseAsync reads
        // the same bytes and swallows what it finds. Without the drain a RAISERROR after the SELECT came
        // back as a successful call with rows attached. Deleting this assertion and the loop it guards
        // restores that, and no assertion about the returned rows would notice.
        Assert.True(reader.NextResultCallCount > 0);
    }

    [Fact]
    public async Task MaterialiseRowsAsync_ErrorRaisedAfterTheLastResultSet_ReachesTheCaller()
    {
        var boom = new InvalidOperationException("RAISERROR after the SELECT");
        var reader = ProductReader(10, 20).FailOnNextResult(boom);

        // The defect this test exists for: the rows arrived, so every assertion about them passed, and
        // the procedure had failed. SqlDataReader reports a post-result-set error when the reader is
        // advanced and swallows it when the reader is merely closed, so a materialiser that only closed
        // returned a successful call for a failed procedure - the one failure shape that no caller can
        // defend against, because there is nothing to catch.
        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StoredProcedureExecutor.MaterialiseRowsAsync<ProductRow>(reader, default));

        Assert.Same(boom, thrown);

        // And the reader is still closed on the way out. On a borrowed connection an unclosed reader
        // hands the connection back unusable, so the caller's next command would fail with something
        // unrelated and bury this error.
        Assert.Equal(1, reader.CloseCallCount);
    }

    [Fact]
    public async Task MaterialiseRowsAsync_ForwardsTheCancellationTokenToReadAsync()
    {
        var reader = ProductReader(10, 20, 30);
        using var cts = new CancellationTokenSource();

        await StoredProcedureExecutor.MaterialiseRowsAsync<ProductRow>(reader, cts.Token);

        // A live source's token is never equal to CancellationToken.None, so this separates forwarding
        // the argument from quietly dropping it. Between rows is the only place this method can be
        // cancelled at all: the token reaches ReadAsync and nothing else.
        Assert.Equal(cts.Token, reader.LastReadToken);
    }

    [Fact]
    public async Task MaterialiseRowsAsync_AlreadyCancelledToken_ThrowsBeforeAnyRow()
    {
        var reader = ProductReader(10, 20, 30);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => StoredProcedureExecutor.MaterialiseRowsAsync<ProductRow>(reader, cts.Token));

        // The cancellation escapes from the very first ReadAsync, so nothing was materialised and the
        // reader is still parked before its first row rather than left half-drained. The close is skipped
        // on this path — recorded rather than endorsed: it is no leak, because ExecuteReturnResultSetAsync
        // holds the reader in an `await using`, and whether a finally belongs here is arguable either way.
        // It is also where cancellation stops being on offer regardless, since CloseAsync takes no token:
        // the final drain, the part that discards whatever the procedure had not yet sent, cannot be
        // cancelled by anything the caller passes.
        Assert.Equal(0, reader.CloseCallCount);
        Assert.True(reader.Read());
        Assert.Equal(10, reader.GetInt32(reader.GetOrdinal("Id")));
    }

    /// <summary>
    /// A reader over <see cref="ProductRow"/>'s columns whose rows differ only in Id, so asserting on the
    /// Ids alone says everything about which rows arrived and in what order. Every case here is about the
    /// loop rather than about the values, and <c>FromDataRecordTests</c> already owns the column reads.
    /// </summary>
    private static FakeDbDataReader ProductReader(params int[] ids)
    {
        var rows = ids.Select(id => new object?[] { id, "SKU-" + id, 1.00m, Stamp }).ToArray();
        return new FakeDbDataReader(ProductColumns, rows);
    }
}
