using System.Reflection;
using Microsoft.Data.SqlClient;
using Raptor21.EF.Extensions.StoredProcedures.Contracts;
using Raptor21.EF.Extensions.StoredProcedures.Execution;
using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Tests;

/// <summary>
/// The part of <see cref="IStoredProcedureExecutor"/> reachable through its public surface with no
/// server: the contract and argument guards that run before the first byte of I/O, plus the statelessness
/// the README's singleton registration depends on.
/// </summary>
/// <remarks>
/// Every executor method is <c>async</c>, so a guard never throws at the call site — it faults the
/// returned task. That is why every case here uses <c>Assert.ThrowsAsync</c>: <c>Assert.Throws</c> would
/// watch the call return a task perfectly normally and report that nothing was thrown.
/// </remarks>
public class StoredProcedureExecutorGuardTests
{
    [Fact]
    public async Task ExecuteReturnResultSetAsync_ContractWithNullResultColumns_ThrowsBeforeTouchingTheConnection()
    {
        var executor = new StoredProcedureExecutor();
        var contract = TestContracts.Touch;  // a RETURN-value procedure, so ResultColumns is null
        using var connection = new SqlConnection();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteReturnResultSetAsync<ProductRow>(connection, contract, new object?[] { 1 }));

        // One value for one contract parameter, so the arity check cannot be what fired; and both of the
        // remaining candidates are InvalidOperationException, which is why the message is what tells them
        // apart. Had the guard not come first, BuildCommand would have succeeded against this unopened
        // connection — CreateCommand needs neither an open socket nor a connection string — and SqlClient
        // would have complained about the connection state instead, with neither fragment below in it.
        Assert.Contains("[dbo].[Product_TouchStamp]", ex.Message);
        Assert.Contains("ExecuteReturnInt32Async", ex.Message);
    }

    [Fact]
    public async Task ExecuteReturnResultSetAsync_ContractWithEmptyResultColumns_ThrowsTheSameWay()
    {
        var executor = new StoredProcedureExecutor();
        // Identical to TestContracts.Touch except that it carries an empty column list rather than null.
        var emptyColumns = TestContracts.Contract(
            "dbo",
            "Product_TouchStamp",
            [new ProcParamSpec("@Id", new SqlTypeSpec("int"))],
            Array.Empty<ColumnSpec>());
        using var connection = new SqlConnection();

        var emptyEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteReturnResultSetAsync<ProductRow>(connection, emptyColumns, new object?[] { 1 }));
        var nullEx = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteReturnResultSetAsync<ProductRow>(connection, TestContracts.Touch, new object?[] { 1 }));

        // The executor reads an empty ResultColumns as "no result set" rather than "a result set with no
        // columns", and says so in exactly the same words it uses for null. The validator reads the same
        // input the same way, through `is { Count: > 0 }`, but there the consequence is the opposite and
        // far quieter: an empty column list makes it skip result-set validation entirely instead of
        // rejecting the contract, so the two halves of the library agree on the meaning and disagree on
        // whether it is worth complaining about.
        Assert.Equal(nullEx.Message, emptyEx.Message);
        Assert.Contains("ExecuteReturnInt32Async", emptyEx.Message);
    }

    [Fact]
    public async Task ExecuteNonQueryAsync_ArityMismatch_FaultsWithArgumentExceptionNotAConnectionError()
    {
        var executor = new StoredProcedureExecutor();
        var contract = TestContracts.Upsert;  // four parameters, and one value is supplied below
        using var connection = new SqlConnection();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => executor.ExecuteNonQueryAsync(connection, contract, new object?[] { "s" }));

        // The arity check sits inside BuildCommand, before the command is ever executed, so it beats the
        // connection-state failure this same closed connection would otherwise have produced. That
        // ordering is the whole value of the check: a caller who passed the wrong number of values is
        // told so, instead of being sent to look at their connection.
        Assert.Equal("parameterValues", ex.ParamName);
        Assert.Contains("[dbo].[Product_Upsert]", ex.Message);
    }

    [Fact]
    public async Task ExecuteNonQueryAsync_ClosedConnection_FaultsWithNoProcedureContext()
    {
        var executor = new StoredProcedureExecutor();
        var contract = TestContracts.Touch;
        // A well-formed connection string that is never opened. SqlClient refuses to execute on a closed
        // connection client-side, so this host name is never resolved and no socket is opened; the short
        // timeout is belt and braces against a future provider that decided to connect on demand.
        using var connection = new SqlConnection("Server=nosuchhost.invalid;Database=x;Connect Timeout=1;");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteNonQueryAsync(connection, contract, new object?[] { 1 }));

        // The executor has no ConnectionState guard of its own and wraps nothing, so a caller who forgot
        // to open the connection gets SqlClient's message with no hint of which procedure was being run —
        // in contrast to the arity failure above, which names it. Only the absence of that context is
        // asserted: pinning SqlClient's exact wording would break this test on a provider upgrade that
        // changed nothing the library is responsible for.
        Assert.DoesNotContain("[dbo].", ex.Message);
    }

    [Fact]
    public void StoredProcedureExecutor_IsStatelessAndSafeAsASingleton()
    {
        var fields = typeof(StoredProcedureExecutor)
            .GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

        // The README registers the executor with AddSingleton, which is only correct while the class
        // carries no per-call state; an instance field added later would let one request's parameters or
        // command reach another. Reflection is fine here — the trimming and AOT guarantees belong to the
        // runtime library, and nothing in this project ships to a consumer.
        Assert.Empty(fields);
    }
}
