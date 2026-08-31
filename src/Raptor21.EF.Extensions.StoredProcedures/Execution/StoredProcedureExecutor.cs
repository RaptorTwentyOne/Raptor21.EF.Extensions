using System.Data;
using System.Data.Common;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Raptor21.EF.Extensions.StoredProcedures.Contracts;

namespace Raptor21.EF.Extensions.StoredProcedures.Execution;

/// <summary>Contract-driven executor: builds SqlCommand from IStoredProcedureContract only (no string literals for proc/params).</summary>
public sealed class StoredProcedureExecutor : IStoredProcedureExecutor
{
    public async Task<int> ExecuteReturnInt32Async(SqlConnectionLease lease, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken = default)
    {
        var value = await ExecuteReturnValueAsync(lease, contract, parameterValues, cancellationToken).ConfigureAwait(false);
        return Convert.ToInt32(value);
    }

    public async Task<short> ExecuteReturnInt16Async(SqlConnectionLease lease, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken = default)
    {
        var value = await ExecuteReturnValueAsync(lease, contract, parameterValues, cancellationToken).ConfigureAwait(false);
        return Convert.ToInt16(value);
    }

    public async Task ExecuteNonQueryAsync(SqlConnectionLease lease, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken = default)
    {
        var fullName = $"[{contract.Schema}].[{contract.Name}]";
        await using var cmd = BuildCommand(lease, contract, parameterValues, fullName);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<(int ReturnValue, IReadOnlyList<object?> Outputs)> ExecuteWithOutputsAsync(SqlConnectionLease lease, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken = default)
    {
        var fullName = $"[{contract.Schema}].[{contract.Name}]";
        await using var cmd = BuildCommand(lease, contract, parameterValues, fullName);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        var returnValue = cmd.Parameters[0].Value is int i ? i : 0;
        return (returnValue, CollectOutputs(cmd.Parameters));
    }

    public async Task<(int ReturnValue, IReadOnlyList<TRow> Rows)> ExecuteReturnResultSetAsync<TRow>(
        SqlConnectionLease lease,
        IStoredProcedureContract contract,
        object?[] parameterValues,
        CancellationToken cancellationToken = default)
        where TRow : IFromDataRecord<TRow>
    {
        var fullName = $"[{contract.Schema}].[{contract.Name}]";
        if (contract.ResultColumns is null || contract.ResultColumns.Count == 0)
            throw new InvalidOperationException($"{fullName}: Contract has no ResultColumns; use ExecuteReturnInt32Async or set ResultColumns for result set procedures.");

        await using var cmd = BuildCommand(lease, contract, parameterValues, fullName);
        await using var reader = await cmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var rows = await MaterialiseRowsAsync<TRow>(reader, cancellationToken).ConfigureAwait(false);
        var returnValue = cmd.Parameters[0].Value is int i ? i : 0;
        return (returnValue, rows);
    }

    internal static SqlCommand BuildCommand(SqlConnectionLease lease, IStoredProcedureContract contract, object?[] parameterValues, string fullName)
    {
        // Everything decidable from the arguments alone is decided before the command exists, so a caller
        // who passed a null or the wrong number of values is told which argument was wrong instead of
        // being handed a dereference, and no half-built SqlCommand is abandoned undisposed on the way out.
        // Reading the lease's connection first is what the connection guard became: a default lease is the
        // one shape that carries none, and it says so by name rather than by NullReferenceException.
        var connection = lease.Connection;
        ArgumentNullException.ThrowIfNull(contract);
        ArgumentNullException.ThrowIfNull(parameterValues);

        var parameters = contract.Parameters;
        if (parameterValues.Length != parameters.Count)
            throw new ArgumentException($"Parameter count mismatch for {fullName}: contract has {parameters.Count}, got {parameterValues.Length}.", nameof(parameterValues));

        var cmd = connection.CreateCommand();

        // The transaction travels with the connection or the command is refused: SqlClient rejects a
        // command on a connection with a pending local transaction whose Transaction is unset, before the
        // server ever sees it. Assigning null is a no-op, which is why this is unconditional and why an
        // owned lease — which is inside no transaction, having just been opened here — is unaffected. It
        // is the same shape EmbeddedScriptApplier already uses for its own optional SqlTransaction.
        cmd.Transaction = lease.Transaction;

        cmd.CommandText = fullName;
        cmd.CommandType = CommandType.StoredProcedure;
        cmd.Parameters.Add(CreateReturnParameter());

        for (var i = 0; i < parameters.Count; i++)
        {
            var p = parameters[i];
            var value = parameterValues[i];
            cmd.Parameters.Add(CreateParameter(p, value));
        }
        return cmd;
    }

    // The two helpers below take the abstract ADO.NET types, not SqlParameterCollection and SqlDataReader:
    // the SqlClient types are sealed or have no accessible constructor, so nothing after Execute*Async could
    // otherwise be reached without a server. The production types derive from these and pass straight through.

    internal static List<object?> CollectOutputs(DbParameterCollection parameters)
    {
        // Output values in parameter (= contract / method declaration) order; index 0 is the RETURN parameter.
        var outputs = new List<object?>();
        for (var p = 1; p < parameters.Count; p++)
        {
            var param = parameters[p];
            if (param.Direction is ParameterDirection.Output or ParameterDirection.InputOutput)
                outputs.Add(param.Value is DBNull ? null : param.Value);
        }
        return outputs;
    }

    internal static async Task<List<TRow>> MaterialiseRowsAsync<TRow>(DbDataReader reader, CancellationToken ct)
        where TRow : IFromDataRecord<TRow>
    {
        var rows = new List<TRow>();
        while (await reader.ReadAsync(ct).ConfigureAwait(false))
            rows.Add(TRow.FromDataRecord(reader));
        try
        {
            // Advance past the rows before closing, and do it for the errors rather than for the data. A
            // procedure that fails AFTER its last result set — a RAISERROR, or a constraint violation in a
            // statement that follows the SELECT — reports that failure when the reader is advanced, and
            // never when it is merely closed: CloseAsync drains the same bytes and swallows what it finds.
            // Without this loop the call returned its rows and looked successful while the procedure had
            // actually failed, which is the worst shape a data-access bug can take. Any further result
            // sets are discarded on purpose — the contract materialises the first and the validator
            // describes only the first — so this reads them for their errors alone.
            while (await reader.NextResultAsync(ct).ConfigureAwait(false))
            {
            }
        }
        finally
        {
            // The close belongs here rather than back at the call site: SQL Server only populates the RETURN
            // parameter once the reader is closed, and the caller reads it the moment this returns. On a
            // borrowed connection it stops being hygiene and becomes correctness — the connection is handed
            // back to its owner the instant the lease is disposed, and an owner holding a connection with an
            // open reader cannot run its next command. A refactor that streams rows lazily, or returns before
            // this line, breaks the RETURN value and every subsequent query in the caller's unit of work.
            //
            // In finally, because the drain above can throw: the reader still has to be closed, or a
            // borrowed connection goes back to its owner unusable and the real error is buried under the
            // next command's failure.
            await reader.CloseAsync().ConfigureAwait(false);
        }

        return rows;
    }

    private static async Task<object> ExecuteReturnValueAsync(SqlConnectionLease lease, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken)
    {
        var fullName = $"[{contract.Schema}].[{contract.Name}]";
        await using var cmd = BuildCommand(lease, contract, parameterValues, fullName);
        await cmd.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return cmd.Parameters[0].Value ?? 0;
    }

    internal static SqlParameter CreateReturnParameter()
    {
        return new SqlParameter
        {
            ParameterName = "@return",
            Direction = ParameterDirection.ReturnValue,
            SqlDbType = SqlDbType.Int
        };
    }

    internal static SqlParameter CreateParameter(ProcParamSpec spec, object? value)
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

    /// <summary>
    /// Applies the fractional-seconds digits of a datetime2, time or datetimeoffset parameter.
    /// </summary>
    /// <remarks>
    /// Only when the contract states them. SqlClient defaults an unstated scale to 7, which is what the
    /// server would use for an undecorated declaration anyway, so saying nothing is the correct answer
    /// rather than a gap - and writing 0 there would silently truncate every value to whole seconds.
    /// </remarks>
    private static void ApplyFractionalSecondsScale(SqlParameter p, SqlTypeSpec spec, int? firstArgument)
    {
        var scale = spec.Scale ?? (firstArgument is >= 0 and <= 7 ? (byte)firstArgument.Value : (byte?)null);
        if (scale.HasValue)
            p.Scale = scale.Value;
    }

    internal static void ApplySqlType(SqlParameter p, SqlTypeSpec spec)
    {
        // A default SqlTypeSpec carries a null name and is reachable from any hand-written contract.
        // Dereferencing it below would surface as a bare NullReferenceException naming neither the
        // parameter nor the type, which is the one thing every message in this method is careful to say.
        if (spec.SqlTypeName is null)
            throw new ArgumentException($"Parameter {p.ParameterName} carries no SQL type name.", nameof(spec));

        // One normalisation for the whole table. The name arrives as a hand-typed [Sql(..., "TypeName")]
        // string, so "binary(16)" is as legal to write as "binary"; matching some names with StartsWith
        // and others with equality used to let the decorated form of an exactly-matched name miss its arm
        // and bind as a varchar. Stripping the arguments first also removes the ordering hazard that made
        // "datetimeoffset" answer to a StartsWith("DATETIME") test and lose its offset on the way out.
        var (name, firstArgument, secondArgument) = ParseSqlTypeName(spec.SqlTypeName);

        switch (name)
        {
            case "VARCHAR":
                p.SqlDbType = SqlDbType.VarChar;
                p.Size = spec.MaxLength ?? firstArgument ?? 0;
                break;
            case "NVARCHAR":
                p.SqlDbType = SqlDbType.NVarChar;
                p.Size = spec.MaxLength ?? firstArgument ?? 0;
                break;
            case "CHAR":
                p.SqlDbType = SqlDbType.Char;
                p.Size = spec.MaxLength ?? firstArgument ?? 0;
                break;
            case "NCHAR":
                p.SqlDbType = SqlDbType.NChar;
                p.Size = spec.MaxLength ?? firstArgument ?? 0;
                break;

            // The large-value types take no length in T-SQL, so nothing here sets a Size and SqlClient
            // infers one from the value.
            case "TEXT":
                p.SqlDbType = SqlDbType.Text;
                break;
            case "NTEXT":
                p.SqlDbType = SqlDbType.NText;
                break;
            case "IMAGE":
                p.SqlDbType = SqlDbType.Image;
                break;
            case "XML":
                p.SqlDbType = SqlDbType.Xml;
                break;

            // "integer" is SQL Server's own synonym for "int" and the one alias worth carrying: it is
            // legal in a CREATE PROCEDURE, so a contract transcribed from a script can spell it that way.
            case "INT" or "INTEGER":
                p.SqlDbType = SqlDbType.Int;
                break;
            case "SMALLINT":
                p.SqlDbType = SqlDbType.SmallInt;
                break;
            case "BIGINT":
                p.SqlDbType = SqlDbType.BigInt;
                break;
            case "TINYINT":
                p.SqlDbType = SqlDbType.TinyInt;
                break;
            case "BIT":
                p.SqlDbType = SqlDbType.Bit;
                break;

            case "DECIMAL" or "NUMERIC":
                p.SqlDbType = SqlDbType.Decimal;
                // A precision or scale spelled into the name is read only when the spec did not carry one
                // separately, and only within SQL Server's own limit of 38: a wider number is a typo, and
                // the declared default is a safer reading of it than a silently truncated byte.
                p.Precision = spec.Precision ?? (byte)(firstArgument is > 0 and <= 38 ? firstArgument.GetValueOrDefault() : 18);
                p.Scale = spec.Scale ?? (byte)(secondArgument is >= 0 and <= 38 ? secondArgument.GetValueOrDefault() : 0);
                break;
            case "MONEY":
                p.SqlDbType = SqlDbType.Money;
                break;
            case "SMALLMONEY":
                p.SqlDbType = SqlDbType.SmallMoney;
                break;
            case "REAL":
                p.SqlDbType = SqlDbType.Real;
                break;
            case "FLOAT":
                p.SqlDbType = SqlDbType.Float;
                break;

            case "DATETIMEOFFSET":
                p.SqlDbType = SqlDbType.DateTimeOffset;
                ApplyFractionalSecondsScale(p, spec, firstArgument);
                break;
            // datetime2 is a superset of datetime's range, so those two collapse without losing anything.
            // date does NOT join them: it is its own SqlDbType, and sending a datetime2 for it makes the
            // server convert on every call - harmless for the value, needless for a type that exists.
            case "DATETIME" or "DATETIME2":
                p.SqlDbType = SqlDbType.DateTime2;
                ApplyFractionalSecondsScale(p, spec, firstArgument);
                break;
            case "DATE":
                p.SqlDbType = SqlDbType.Date;
                break;
            case "SMALLDATETIME":
                p.SqlDbType = SqlDbType.SmallDateTime;
                break;
            case "TIME":
                p.SqlDbType = SqlDbType.Time;
                ApplyFractionalSecondsScale(p, spec, firstArgument);
                break;

            case "UNIQUEIDENTIFIER":
                p.SqlDbType = SqlDbType.UniqueIdentifier;
                break;

            // Both names bind as VarBinary, so a fixed-length binary(16) travels as a variable-length
            // value and the server pads it on assignment. -1 is MAX, and it is this table's one default
            // size that is not 0.
            case "BINARY" or "VARBINARY":
                p.SqlDbType = SqlDbType.VarBinary;
                p.Size = spec.MaxLength ?? firstArgument ?? -1;
                break;

            default:
                // Falling back to varchar here was silent data corruption: a sql_variant or a table type
                // reached the server stringified with nothing logged anywhere. Validation does not catch
                // it either, because CompareSqlType checks the contract's type name against the catalog
                // rather than against this table, so a contract naming the parameter's real type passes
                // validation and then arrives here with nowhere to go.
                throw new ArgumentException(
                    $"Parameter {p.ParameterName} declares SQL type '{spec.SqlTypeName}', which has no SqlDbType mapping.",
                    nameof(spec));
        }
    }

    // Splits a hand-written type name into the base name every arm above matches on and the arguments of
    // a trailing "(...)", which are what a decorated name carries instead of spec.MaxLength/Precision/Scale.
    private static (string Name, int? FirstArgument, int? SecondArgument) ParseSqlTypeName(string sqlTypeName)
    {
        // ToUpperInvariant rather than ToUpper: a Turkish locale uppercases "int" to "İNT", which would
        // miss every arm of the table on the one machine where it is hardest to notice.
        var text = sqlTypeName.Trim().ToUpperInvariant();

        var open = text.IndexOf('(');
        if (open < 0 || !text.EndsWith(")", StringComparison.Ordinal))
            return (text, null, null);

        var name = text.Substring(0, open).TrimEnd();
        var arguments = text.Substring(open + 1, text.Length - open - 2);
        var comma = arguments.IndexOf(',');
        if (comma < 0)
            return (name, ParseTypeArgument(arguments), null);

        return (name, ParseTypeArgument(arguments.Substring(0, comma)), ParseTypeArgument(arguments.Substring(comma + 1)));
    }

    // One length, precision or scale argument. MAX is SQL Server's spelling of the -1 SqlParameter.Size
    // wants, and anything else that is not a plain number is treated as absent rather than as a zero.
    private static int? ParseTypeArgument(string argument)
    {
        var text = argument.Trim();
        if (text == "MAX")
            return -1;
        return int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ? value : null;
    }
}
