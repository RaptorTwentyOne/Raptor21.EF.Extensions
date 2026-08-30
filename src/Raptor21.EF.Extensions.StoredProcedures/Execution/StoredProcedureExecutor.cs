using System.Data;
using Microsoft.Data.SqlClient;
using Raptor21.EF.Extensions.StoredProcedures.Contracts;

namespace Raptor21.EF.Extensions.StoredProcedures.Execution;

/// <summary>Contract-driven executor: builds SqlCommand from IStoredProcedureContract only (no string literals for proc/params).</summary>
public sealed class StoredProcedureExecutor : IStoredProcedureExecutor
{
    public async Task<int> ExecuteReturnInt32Async(SqlConnection connection, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken = default)
    {
        var value = await ExecuteReturnValueAsync(connection, contract, parameterValues, cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value);
    }

    public async Task<short> ExecuteReturnInt16Async(SqlConnection connection, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken = default)
    {
        var value = await ExecuteReturnValueAsync(connection, contract, parameterValues, cancellationToken).ConfigureAwait(false);
        return Convert.ToInt16(value);
    }

    public async Task ExecuteNonQueryAsync(SqlConnection connection, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken = default)
    {
        var fullName = $"[{contract.Schema}].[{contract.Name}]";
        await using var cmd = BuildCommand(connection, contract, parameterValues, fullName);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int ReturnValue, IReadOnlyList<object?> Outputs)> ExecuteWithOutputsAsync(SqlConnection connection, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken = default)
    {
        var fullName = $"[{contract.Schema}].[{contract.Name}]";
        await using var cmd = BuildCommand(connection, contract, parameterValues, fullName);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var returnValue = cmd.Parameters[0].Value is int i ? i : 0;

        // Output values in parameter (= contract / method declaration) order; index 0 is the RETURN parameter.
        var outputs = new List<object?>();
        for (var p = 1; p < cmd.Parameters.Count; p++)
        {
            var param = cmd.Parameters[p];
            if (param.Direction is ParameterDirection.Output or ParameterDirection.InputOutput)
                outputs.Add(param.Value is DBNull ? null : param.Value);
        }

        return (returnValue, outputs);
    }

    public async Task<(int ReturnValue, IReadOnlyList<TRow> Rows)> ExecuteReturnResultSetAsync<TRow>(
        SqlConnection connection,
        IStoredProcedureContract contract,
        object?[] parameterValues,
        CancellationToken cancellationToken = default)
        where TRow : IFromDataRecord<TRow>
    {
        var fullName = $"[{contract.Schema}].[{contract.Name}]";
        if (contract.ResultColumns is null || contract.ResultColumns.Count == 0)
            throw new InvalidOperationException($"{fullName}: Contract has no ResultColumns; use ExecuteReturnInt32Async or set ResultColumns for result set procedures.");

        await using var cmd = BuildCommand(connection, contract, parameterValues, fullName);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = new List<TRow>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            rows.Add(TRow.FromDataRecord(reader));
        await reader.CloseAsync().ConfigureAwait(false);
        var returnValue = cmd.Parameters[0].Value is int i ? i : 0;
        return (returnValue, rows);
    }

    private static SqlCommand BuildCommand(SqlConnection connection, IStoredProcedureContract contract, object?[] parameterValues, string fullName)
    {
        var cmd = connection.CreateCommand();
        cmd.CommandText = fullName;
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.Parameters.Add(CreateReturnParameter());

        var parameters = contract.Parameters;
        if (parameterValues.Length != parameters.Count)
            throw new ArgumentException($"Parameter count mismatch for {fullName}: contract has {parameters.Count}, got {parameterValues.Length}.", nameof(parameterValues));

        for (var i = 0; i < parameters.Count; i++)
        {
            var p = parameters[i];
            var value = parameterValues[i];
            cmd.Parameters.Add(CreateParameter(p, value));
        }
        return cmd;
    }

    private static async Task<object> ExecuteReturnValueAsync(SqlConnection connection, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken)
    {
        var fullName = $"[{contract.Schema}].[{contract.Name}]";
        await using var cmd = BuildCommand(connection, contract, parameterValues, fullName);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return cmd.Parameters[0].Value ?? 0;
    }

    private static SqlParameter CreateReturnParameter()
    {
        return new SqlParameter
        {
            ParameterName = "@return",
            Direction = ParameterDirection.ReturnValue,
            SqlDbType = SqlDbType.Int
        };
    }

    private static SqlParameter CreateParameter(ProcParamSpec spec, object? value)
    {
        var p = new SqlParameter
        {
            ParameterName = spec.Name,
            Value = value ?? DBNull.Value,
            Direction = spec.IsOutput ? ParameterDirection.Output : ParameterDirection.Input
        };
        ApplySqlType(p, spec.SqlType);
        return p;
    }

    private static void ApplySqlType(SqlParameter p, SqlTypeSpec spec)
    {
        var name = spec.SqlTypeName.Trim().ToUpperInvariant();
        if (name.StartsWith("VARCHAR", StringComparison.Ordinal))
        {
            p.SqlDbType = SqlDbType.VarChar;
            p.Size = spec.MaxLength ?? 0;
        }
        else if (name.StartsWith("NVARCHAR", StringComparison.Ordinal))
        {
            p.SqlDbType = SqlDbType.NVarChar;
            p.Size = spec.MaxLength ?? 0;
        }
        else if (name.StartsWith("CHAR", StringComparison.Ordinal) && !name.StartsWith("NCHAR", StringComparison.Ordinal))
        {
            p.SqlDbType = SqlDbType.Char;
            p.Size = spec.MaxLength ?? 0;
        }
        else if (name.StartsWith("NCHAR", StringComparison.Ordinal))
        {
            p.SqlDbType = SqlDbType.NChar;
            p.Size = spec.MaxLength ?? 0;
        }
        else if (name == "INT")
            p.SqlDbType = SqlDbType.Int;
        else if (name == "SMALLINT")
            p.SqlDbType = SqlDbType.SmallInt;
        else if (name == "BIGINT")
            p.SqlDbType = SqlDbType.BigInt;
        else if (name == "TINYINT")
            p.SqlDbType = SqlDbType.TinyInt;
        else if (name == "BIT")
            p.SqlDbType = SqlDbType.Bit;
        else if (name.StartsWith("DECIMAL", StringComparison.Ordinal) || name.StartsWith("NUMERIC", StringComparison.Ordinal))
        {
            p.SqlDbType = SqlDbType.Decimal;
            p.Precision = spec.Precision ?? 18;
            p.Scale = spec.Scale ?? 0;
        }
        else if (name.StartsWith("DATETIME", StringComparison.Ordinal) || name == "DATE" || name == "TIME")
            p.SqlDbType = SqlDbType.DateTime2;
        else if (name == "UNIQUEIDENTIFIER")
            p.SqlDbType = SqlDbType.UniqueIdentifier;
        else if (name == "BINARY" || name.StartsWith("VARBINARY", StringComparison.Ordinal))
        {
            p.SqlDbType = SqlDbType.VarBinary;
            p.Size = spec.MaxLength ?? -1;
        }
        else
            p.SqlDbType = SqlDbType.VarChar;
    }
}
