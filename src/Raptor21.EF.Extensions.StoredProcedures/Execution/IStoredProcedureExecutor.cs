using Microsoft.Data.SqlClient;
using Raptor21.EF.Extensions.StoredProcedures.Contracts;

namespace Raptor21.EF.Extensions.StoredProcedures.Execution;

/// <summary>Executes stored procedures via contracts only (no procedure/parameter name strings at call site).</summary>
public interface IStoredProcedureExecutor
{
    /// <summary>Executes the procedure and returns the RETURN value as int. Parameter values must match contract.Parameters order.</summary>
    Task<int> ExecuteReturnInt32Async(SqlConnection connection, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken = default);

    /// <summary>Executes the procedure and returns the RETURN value as short. Parameter values must match contract.Parameters order.</summary>
    Task<short> ExecuteReturnInt16Async(SqlConnection connection, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken = default);

    /// <summary>Executes the procedure without reading a RETURN value or result set (fire-and-forget non-query). Parameter values must match contract.Parameters order.</summary>
    Task ExecuteNonQueryAsync(SqlConnection connection, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken = default);

    /// <summary>Executes the procedure and returns the RETURN value plus OUTPUT/INPUT-OUTPUT parameter values (in parameter declaration order). Parameter values must match contract.Parameters order.</summary>
    Task<(int ReturnValue, IReadOnlyList<object?> Outputs)> ExecuteWithOutputsAsync(SqlConnection connection, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken = default);

    /// <summary>Executes the procedure and returns RETURN value + first result set as typed rows. Contract.ResultColumns must be set. TRow must implement IFromDataRecord&lt;TRow&gt; (explicit column read, no reflection).</summary>
    Task<(int ReturnValue, IReadOnlyList<TRow> Rows)> ExecuteReturnResultSetAsync<TRow>(
        SqlConnection connection,
        IStoredProcedureContract contract,
        object?[] parameterValues,
        CancellationToken cancellationToken = default)
        where TRow : IFromDataRecord<TRow>;
}
