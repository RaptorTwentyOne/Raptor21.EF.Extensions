using Raptor21.EF.Extensions.StoredProcedures.Contracts;

namespace Raptor21.EF.Extensions.StoredProcedures.Execution;

/// <summary>Executes stored procedures via contracts only (no procedure/parameter name strings at call site).</summary>
/// <remarks>
/// Every method takes a <see cref="SqlConnectionLease"/> rather than a connection, because the
/// connection and the transaction it is inside cannot be read apart: a command whose transaction
/// belongs to another connection is refused, and a command on a connection with a pending local
/// transaction whose transaction is unset is refused too. The executor never disposes the lease — the
/// generated method body that obtained it owns it, and disposing it here would close a connection the
/// caller may still be using.
/// </remarks>
public interface IStoredProcedureExecutor
{
    /// <summary>Executes the procedure and returns the RETURN value as int. Parameter values must match contract.Parameters order.</summary>
    Task<int> ExecuteReturnInt32Async(SqlConnectionLease lease, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken = default);

    /// <summary>Executes the procedure and returns the RETURN value as short. Parameter values must match contract.Parameters order.</summary>
    Task<short> ExecuteReturnInt16Async(SqlConnectionLease lease, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken = default);

    /// <summary>Executes the procedure without reading a RETURN value or result set (fire-and-forget non-query). Parameter values must match contract.Parameters order.</summary>
    Task ExecuteNonQueryAsync(SqlConnectionLease lease, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken = default);

    /// <summary>Executes the procedure and returns the RETURN value plus OUTPUT/INPUT-OUTPUT parameter values (in parameter declaration order). Parameter values must match contract.Parameters order.</summary>
    Task<(int ReturnValue, IReadOnlyList<object?> Outputs)> ExecuteWithOutputsAsync(SqlConnectionLease lease, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken = default);

    /// <summary>Executes the procedure and returns RETURN value + first result set as typed rows. Contract.ResultColumns must be set. TRow must implement IFromDataRecord&lt;TRow&gt; (explicit column read, no reflection).</summary>
    Task<(int ReturnValue, IReadOnlyList<TRow> Rows)> ExecuteReturnResultSetAsync<TRow>(
        SqlConnectionLease lease,
        IStoredProcedureContract contract,
        object?[] parameterValues,
        CancellationToken cancellationToken = default)
        where TRow : IFromDataRecord<TRow>;
}
