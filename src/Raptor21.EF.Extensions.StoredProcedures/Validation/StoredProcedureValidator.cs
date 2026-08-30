using Microsoft.Data.SqlClient;
using Raptor21.EF.Extensions.StoredProcedures.Contracts;

namespace Raptor21.EF.Extensions.StoredProcedures.Validation;

/// <summary>Validates stored procedures against .NET contracts. Fails fast on any mismatch.</summary>
public static class StoredProcedureValidator
{
    private const string GetParamsSql = """
        SELECT p.name, TYPE_NAME(p.user_type_id) AS type_name, p.max_length, p.precision, p.scale, p.is_output
        FROM sys.parameters p
        INNER JOIN sys.objects o ON p.object_id = o.object_id
        INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
        WHERE s.name = @schema AND o.name = @name AND o.type = 'P' AND p.parameter_id > 0
        ORDER BY p.parameter_id;
        """;

    private const string FirstResultSetSql = """
        SELECT name, system_type_name, is_nullable
        FROM sys.dm_exec_describe_first_result_set_for_object(OBJECT_ID(@fullName), 0)
        ORDER BY column_ordinal;
        """;

    /// <summary>Validates all contracts against the database. Throws InvalidOperationException on first error.</summary>
    public static async Task ValidateAsync(
        string connectionString,
        IEnumerable<IStoredProcedureContract> contracts,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(contracts);

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        foreach (var contract in contracts)
            await ValidateOneAsync(conn, contract, ct).ConfigureAwait(false);
    }

    private static async Task ValidateOneAsync(SqlConnection conn, IStoredProcedureContract contract, CancellationToken ct)
    {
        var fullName = $"[{contract.Schema}].[{contract.Name}]";

        // 1. Procedure must exist
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT OBJECT_ID(@fullName, 'P')";
            cmd.Parameters.AddWithValue("@fullName", fullName);
            var id = await cmd.ExecuteScalarAsync(ct).ConfigureAwait(false);
            if (id is null or DBNull || id is int and 0)
                throw new InvalidOperationException($"Stored procedure {fullName} does not exist.");
        }

        // 2. Return: SQL Server RETURN is always int
        if (contract.Return.Kind == ReturnKind.ReturnValue)
        {
            var rt = contract.Return.SqlType;
            if (!string.Equals(rt.SqlTypeName, "int", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{fullName}: Contract specifies return type {rt.SqlTypeName}; SQL Server RETURN must be int.");
        }

        // 3. Parameters
        var dbParams = new List<(string Name, string TypeName, int MaxLength, byte Precision, byte Scale, bool IsOutput)>();
        await using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = GetParamsSql;
            cmd.Parameters.AddWithValue("@schema", contract.Schema);
            cmd.Parameters.AddWithValue("@name", contract.Name);
            await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var name = reader.GetString(0);
                var typeName = reader.GetString(1);
                var maxLen = reader.IsDBNull(2) ? -1 : reader.GetInt16(2);
                var prec = reader.IsDBNull(3) ? (byte)0 : (byte)reader.GetByte(3);
                var scale = reader.IsDBNull(4) ? (byte)0 : (byte)reader.GetByte(4);
                var isOut = reader.GetBoolean(5);
                dbParams.Add((name, typeName, maxLen, prec, scale, isOut));
            }
        }

        var contractParams = contract.Parameters;
        if (dbParams.Count != contractParams.Count)
            throw new InvalidOperationException($"{fullName}: Parameter count mismatch. Expected {contractParams.Count}, found {dbParams.Count}.");

        for (var i = 0; i < contractParams.Count; i++)
        {
            var cp = contractParams[i];
            var dp = dbParams[i];
            if (!string.Equals(cp.Name, dp.Name, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"{fullName}: Parameter[{i}] name mismatch. Expected {cp.Name}, found {dp.Name}.");
            if (cp.IsOutput != dp.IsOutput)
                throw new InvalidOperationException($"{fullName}: Parameter {cp.Name} output flag mismatch.");
            CompareSqlType(cp.Name, cp.SqlType, dp.TypeName, dp.MaxLength, dp.Precision, dp.Scale, fullName);
        }

        if (contract.ResultColumns is { Count: > 0 } columns)
        {
            var dbCols = new List<(string Name, string SystemTypeName, bool IsNullable)>();
            await using (var cmd = conn.CreateCommand())
            {
                cmd.CommandText = FirstResultSetSql;
                cmd.Parameters.AddWithValue("@fullName", fullName);
                await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
                while (await reader.ReadAsync(ct).ConfigureAwait(false))
                {
                    dbCols.Add((reader.GetString(0), reader.GetString(1), reader.GetBoolean(2)));
                }
            }

            if (dbCols.Count != columns.Count)
                throw new InvalidOperationException($"{fullName}: Result set column count mismatch. Expected {columns.Count}, found {dbCols.Count}.");

            for (var i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                var db = dbCols[i];
                if (!string.Equals(col.Name, db.Name, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidOperationException($"{fullName}: Result column[{i}] name mismatch. Expected {col.Name}, found {db.Name}.");
                ValidateSqlToDotNetType(fullName, col.Name, db.SystemTypeName, db.IsNullable, col.DotNetType);
            }
        }
    }

    private static void CompareSqlType(string paramName, SqlTypeSpec contract, string dbTypeName, int dbMaxLen, byte dbPrec, byte dbScale, string fullName)
    {
        var ct = contract.SqlTypeName.Trim();
        var db = dbTypeName.Trim();
        if (!string.Equals(ct, db, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"{fullName}: Parameter {paramName} type mismatch. Expected {ct}, found {db}.");

        if (contract.MaxLength.HasValue && contract.MaxLength.Value != dbMaxLen)
            throw new InvalidOperationException($"{fullName}: Parameter {paramName} max_length mismatch. Expected {contract.MaxLength}, found {dbMaxLen}.");
        if (contract.Precision.HasValue && contract.Precision.Value != dbPrec)
            throw new InvalidOperationException($"{fullName}: Parameter {paramName} precision mismatch. Expected {contract.Precision}, found {dbPrec}.");
        if (contract.Scale.HasValue && contract.Scale.Value != dbScale)
            throw new InvalidOperationException($"{fullName}: Parameter {paramName} scale mismatch. Expected {contract.Scale}, found {dbScale}.");
    }

    private static void ValidateSqlToDotNetType(string fullName, string columnName, string systemTypeName, bool isNullable, Type dotNetType)
    {
        var sqlType = systemTypeName.Split('(')[0].Trim().ToLowerInvariant();
        // Nullability is only enforceable for value types: reflection cannot distinguish a reference type's
        // nullable annotation (string vs string?, byte[] vs byte[]? are the same runtime Type), so IsNullableType
        // reports every reference type as nullable. Enforcing that would reject any NOT NULL string/byte[] column,
        // which is a false positive. The generated readers are null-safe for reference types regardless, so we
        // only check the nullability match for value types.
        if (dotNetType.IsValueType)
        {
            var expectedNullable = IsNullableType(dotNetType);
            if (isNullable && !expectedNullable)
                throw new InvalidOperationException($"{fullName}: Result column {columnName} is NULL in SQL but contract type {dotNetType.Name} is non-nullable.");
            if (!isNullable && expectedNullable)
                throw new InvalidOperationException($"{fullName}: Result column {columnName} is NOT NULL in SQL but contract type {dotNetType.Name} is nullable.");
        }

        var allowed = GetAllowedDotNetTypesForSqlType(sqlType);
        if (allowed.Count == 0)
            throw new InvalidOperationException($"{fullName}: Result column {columnName} has unsupported SQL type {systemTypeName}.");
        if (!allowed.Contains(dotNetType))
            throw new InvalidOperationException($"{fullName}: Result column {columnName} SQL type {systemTypeName} is not compatible with .NET type {dotNetType.Name}. Allowed: {string.Join(", ", allowed.Select(t => t.Name))}.");
    }

    private static bool IsNullableType(Type t)
    {
        if (!t.IsValueType) return true;
        return Nullable.GetUnderlyingType(t) is not null;
    }

    private static HashSet<Type> GetAllowedDotNetTypesForSqlType(string sqlType)
    {
        return sqlType switch
        {
            "tinyint" => [typeof(byte), typeof(byte?)],
            "smallint" => [typeof(short), typeof(short?)],
            "int" => [typeof(int), typeof(int?)],
            "bigint" => [typeof(long), typeof(long?)],
            "bit" => [typeof(bool), typeof(bool?)],
            "varchar" or "nvarchar" or "char" or "nchar" or "text" or "ntext" => [typeof(string)],
            "binary" or "varbinary" or "image" => [typeof(byte[])],
            "datetime" or "datetime2" or "smalldatetime" or "date" or "time" => [typeof(DateTime), typeof(DateTime?), typeof(DateTimeOffset), typeof(DateTimeOffset?)],
            "uniqueidentifier" => [typeof(Guid), typeof(Guid?)],
            "decimal" or "numeric" => [typeof(decimal), typeof(decimal?)],
            "real" => [typeof(float), typeof(float?)],
            "float" => [typeof(double), typeof(double?)],
            "money" or "smallmoney" => [typeof(decimal), typeof(decimal?)],
            _ => []
        };
    }
}
