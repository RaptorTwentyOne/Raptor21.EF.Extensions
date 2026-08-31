using Microsoft.Data.SqlClient;
using Raptor21.EF.Extensions.StoredProcedures.Contracts;
using Raptor21.EF.Extensions.StoredProcedures.Execution;
using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Tests;

/// <summary>
/// The transaction's route to the command: that every execute path builds through <c>BuildCommand</c>,
/// that <c>BuildCommand</c> takes the connection from the lease and from nowhere else, and that a lease
/// carrying no transaction produces exactly the command this library produced before leases existed.
/// </summary>
/// <remarks>
/// One half of the claim is not assertable offline and is not pretended to be. <c>cmd.Transaction =
/// lease.Transaction</c> with a NON-null transaction needs a real <see cref="SqlTransaction"/>, which has
/// no public constructor and cannot be obtained without a server; a hand-forged one does not help either,
/// because a transaction whose <c>Connection</c> is null reads back as null from <c>SqlCommand</c> and is
/// refused by <c>Borrow</c> for the same reason. That half is proved by the sample's live borrowing step.
///
/// Everything around it is provable here, and it is what makes the one line hard to lose: the assignment
/// sits on the single path all five methods take, and the values it reads cannot come from anywhere else.
/// </remarks>
public class StoredProcedureExecutorTransactionTests
{
    // The five methods of IStoredProcedureExecutor, named as strings so the theory data stays
    // serializable and each case appears in the runner under the path it covers.
    private const string Int32Path = "ExecuteReturnInt32Async";
    private const string Int16Path = "ExecuteReturnInt16Async";
    private const string NonQueryPath = "ExecuteNonQueryAsync";
    private const string OutputsPath = "ExecuteWithOutputsAsync";
    private const string ResultSetPath = "ExecuteReturnResultSetAsync";

    [Theory]
    [InlineData(Int32Path)]
    [InlineData(Int16Path)]
    [InlineData(NonQueryPath)]
    [InlineData(OutputsPath)]
    [InlineData(ResultSetPath)]
    public async Task EveryExecutePath_BuildsItsCommandThroughBuildCommand(string path)
    {
        var executor = new StoredProcedureExecutor();
        using var connection = new SqlConnection();

        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => Invoke(executor, path, SqlConnectionLease.Own(connection), UnmappableParameter, new object?[] { 1 }));

        // This message can only come from ApplySqlType, which is reached from CreateParameter, which is
        // reached from BuildCommand's parameter loop and from nowhere else in the assembly. That loop runs
        // after connection.CreateCommand() and after `cmd.Transaction = lease.Transaction`, so a path that
        // arrives here is a path that built its command in the one place the transaction is assigned.
        //
        // That is what "the transaction reaches the command on every executor path" reduces to offline:
        // not five assignments to check, but the absence of a second command-building path for a future
        // execute method to be written on. The two RETURN-value paths reach it through the private
        // ExecuteReturnValueAsync, which is the one indirection in the file and the one place a fifth
        // path could have been added without noticing.
        Assert.Equal("spec", ex.ParamName);
        Assert.Contains("@Bad", ex.Message);
        Assert.Contains("sql_variant", ex.Message);
    }

    [Theory]
    [InlineData(Int32Path)]
    [InlineData(Int16Path)]
    [InlineData(NonQueryPath)]
    [InlineData(OutputsPath)]
    [InlineData(ResultSetPath)]
    public async Task EveryExecutePath_TakesItsConnectionFromTheLease(string path)
    {
        var executor = new StoredProcedureExecutor();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => Invoke(executor, path, default, UnmappableParameter, new object?[] { 1 }));

        // A default lease is the one shape that carries no connection, so this message on all five paths
        // is the executor having no other source for one: no field, no factory, no fallback to a
        // connection string. It is the sharper half of the pairing the lease exists to enforce, because a
        // connection can no longer arrive without the transaction it is inside travelling beside it.
        //
        // On the result-set path it also outruns that path's own ResultColumns guard, which is why the
        // probe contract below carries a column: what is asserted here is the lease's message, not that
        // guard's.
        Assert.Contains("ISqlConnectionProvider.LeaseAsync", ex.Message);
    }

    [Fact]
    public void ExecutorSurface_TakesTheConnectionAndTransactionAsOneValueEverywhere()
    {
        var methods = typeof(IStoredProcedureExecutor).GetMethods();

        Assert.NotEmpty(methods);
        foreach (var method in methods)
        {
            var parameters = method.GetParameters();

            // Every method takes the pair, first, as one value. The alternative shape — a connection plus
            // an optional SqlTransaction — carries the same information but makes the bug representable
            // again one level down: a future execute path can forward the connection and forget the
            // transaction, and nothing would object. A lease cannot be half-passed.
            Assert.Equal(typeof(SqlConnectionLease), parameters[0].ParameterType);

            // And no method takes either half on its own, which is the rule the assertion above is only
            // one instance of. Reflection is fine here: the trimming and AOT guarantees belong to the
            // runtime library, and nothing in this project ships to a consumer.
            Assert.DoesNotContain(parameters, p => p.ParameterType == typeof(SqlConnection) || p.ParameterType == typeof(SqlTransaction));
        }
    }

    [Fact]
    public void BorrowedLeaseOutsideATransaction_ProducesTheSameCommandAsAnOwnedOne()
    {
        var values = new object?[] { "SKU-9", "Widget", 9.99m, 42 };

        using var ownedCommand = StoredProcedureExecutor.BuildCommand(
            SqlConnectionLease.Own(new SqlConnection()), TestContracts.Upsert, values, "[dbo].[Product_Upsert]");
        using var borrowedCommand = StoredProcedureExecutor.BuildCommand(
            SqlConnectionLease.Borrow(new SqlConnection()), TestContracts.Upsert, values, "[dbo].[Product_Upsert]");

        // Borrowing a connection that is inside no transaction is today's behaviour with a different
        // owner, and this is that stated as an equality rather than as a claim: same command text, same
        // command type, and the same parameters down to type, direction, size, precision, scale and value.
        // The only thing borrowing changes is who disposes the connection afterwards, which no property of
        // the command can see.
        Assert.Equal(ownedCommand.CommandText, borrowedCommand.CommandText);
        Assert.Equal(ownedCommand.CommandType, borrowedCommand.CommandType);
        Assert.Equal(Describe(ownedCommand), Describe(borrowedCommand));

        // Null on both sides, for two different reasons that both hold: an owned connection was opened by
        // this library a statement earlier and is inside nothing, and this borrowed one was handed over
        // with no transaction. Assigning null is a no-op, which is why BuildCommand's assignment is
        // unconditional and why the ordinary call site pays nothing for it.
        Assert.Null(ownedCommand.Transaction);
        Assert.Null(borrowedCommand.Transaction);
    }

    /// <summary>
    /// A contract whose one parameter declares a SQL type with no <c>SqlDbType</c>, so that binding it
    /// fails inside the parameter loop — after the command exists and after the transaction is assigned.
    /// </summary>
    /// <remarks>
    /// The result column is not decoration: without it <c>ExecuteReturnResultSetAsync</c> refuses the
    /// contract before <c>BuildCommand</c> is ever called, and the theory would be asserting that path's
    /// own guard rather than the shared one. Every other path ignores the columns entirely.
    /// </remarks>
    private static GeneratedContract UnmappableParameter => TestContracts.Contract(
        "dbo",
        "P",
        [new ProcParamSpec("@Bad", new SqlTypeSpec("sql_variant"))],
        [new ColumnSpec("Id", typeof(int))]);

    /// <summary>Calls one of the five executor methods by name, through the interface a generated body uses.</summary>
    /// <remarks>
    /// A switch over names rather than a <c>TheoryData</c> of delegates, so the theory data stays
    /// serializable and the runner can name each case. The arms have different result types and every one
    /// of them converts to <see cref="Task"/>, which is all a guard assertion needs.
    /// </remarks>
    private static Task Invoke(
        IStoredProcedureExecutor executor,
        string path,
        SqlConnectionLease lease,
        IStoredProcedureContract contract,
        object?[] values) => path switch
        {
            Int32Path => executor.ExecuteReturnInt32Async(lease, contract, values),
            Int16Path => executor.ExecuteReturnInt16Async(lease, contract, values),
            NonQueryPath => executor.ExecuteNonQueryAsync(lease, contract, values),
            OutputsPath => executor.ExecuteWithOutputsAsync(lease, contract, values),
            ResultSetPath => executor.ExecuteReturnResultSetAsync<ProductRow>(lease, contract, values),
            _ => throw new ArgumentOutOfRangeException(nameof(path), path, "Not an IStoredProcedureExecutor method."),
        };

    /// <summary>Every observable property of every parameter on a finished command, as comparable text.</summary>
    private static string[] Describe(SqlCommand command) =>
        command.Parameters
            .Cast<SqlParameter>()
            .Select(p => string.Join("|", p.ParameterName, p.SqlDbType, p.Direction, p.Size, p.Precision, p.Scale, p.Value ?? "(null)"))
            .ToArray();
}
