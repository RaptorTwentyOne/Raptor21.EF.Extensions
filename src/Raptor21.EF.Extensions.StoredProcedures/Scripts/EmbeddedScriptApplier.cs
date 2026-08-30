using System.Collections.Frozen;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.SqlClient;

namespace Raptor21.EF.Extensions.StoredProcedures.Scripts;

/// <summary>Applies embedded .sql scripts idempotently using a history table.</summary>
public sealed class EmbeddedScriptApplier
{
    private const string HistoryTableName = "dbo.__DbScriptHistory";

    //lang=sql
    private const string CreateHistoryTableSql = """
        IF OBJECT_ID('dbo.__DbScriptHistory', 'U') IS NULL
        CREATE TABLE dbo.__DbScriptHistory (
            ScriptName nvarchar(400) NOT NULL PRIMARY KEY,
            Sha256 char(64) NOT NULL,
            AppliedAt datetime2 NOT NULL
        );
        """;

    /// <summary>
    /// Applies all embedded .sql resources under the given prefix. Scripts are split by GO and run in a single transaction.
    /// </summary>
    /// <param name="connectionString">SQL Server connection string.</param>
    /// <param name="assembly">Assembly containing embedded .sql resources.</param>
    /// <param name="resourcePrefix">Resource name prefix (e.g. MyLib.DbScripts.).</param>
    /// <param name="useTransaction">If true, all scripts run in one transaction (default true).</param>
    /// <param name="ct">Cancellation token.</param>
    /// <exception cref="InvalidOperationException">A matching resource has no stream, or two resources resolve to the same script name.</exception>
    public static async Task ApplyEmbeddedScriptsAsync(
        string connectionString,
        Assembly assembly,
        string resourcePrefix,
        bool useTransaction = true,
        CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePrefix);

        var scripts = LoadScripts(assembly, resourcePrefix);
        if (scripts.Count == 0)
            return;

        await using var conn = new SqlConnection(connectionString);
        await conn.OpenAsync(ct).ConfigureAwait(false);

        if (useTransaction)
        {
            await using var tx = conn.BeginTransaction();
            try
            {
                await EnsureHistoryTableAsync(conn, tx, ct).ConfigureAwait(false);
                foreach (var (name, content) in scripts)
                    await ApplyOneAsync(conn, tx, name, content, ct).ConfigureAwait(false);
                await tx.CommitAsync(ct).ConfigureAwait(false);
            }
            catch
            {
                await tx.RollbackAsync(ct).ConfigureAwait(false);
                throw;
            }
        }
        else
        {
            await EnsureHistoryTableAsync(conn, null, ct).ConfigureAwait(false);
            foreach (var (name, content) in scripts)
                await ApplyOneAsync(conn, null, name, content, ct).ConfigureAwait(false);
        }
    }

    internal static FrozenDictionary<string, string> LoadScripts(Assembly assembly, string resourcePrefix)
    {
        var prefix = resourcePrefix.TrimEnd('.') + ".";
        var names = assembly.GetManifestResourceNames();

        // OrdinalIgnoreCase, matching dbo.__DbScriptHistory.ScriptName, whose primary key lives under the
        // database collation and is case-insensitive by default. Ordinal here would let two resources
        // differing only in case both load and then contend for one history row, so each would re-run on
        // every deployment. Losing one silently was worse; refusing both, below, costs nothing.
        var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // Which resource claimed each script name, so a collision can name BOTH files. Naming only the
        // loser leaves the developer hunting for the one it collided with.
        var origins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var name in names)
        {
            if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !name.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                continue;

            // GetManifestResourceStream is documented to return null, and the assembly is a caller-supplied
            // parameter, so the null-forgiving operator that used to stand here turned a reachable failure
            // into an ArgumentNullException naming "stream" - a message mentioning neither the resource nor
            // where it came from, thrown from a method whose entire job is loading resources.
            using var stream = assembly.GetManifestResourceStream(name)
                ?? throw new InvalidOperationException(
                    $"Embedded resource '{name}' is listed by assembly '{assembly.FullName}' but has no stream.");
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var content = reader.ReadToEnd();
            var scriptName = name[prefix.Length..];

            // TryAdd and not an indexer assignment: two resources can reach the same script name, whether
            // by differing only in case or by DbScripts/Sub/A.sql flattening onto DbScripts.Sub.A.sql. That
            // key is the primary key in dbo.__DbScriptHistory, so a script lost here is one that never runs
            // and never looks missing - which surfaces far away as the validator reporting a procedure
            // absent whose .sql file is plainly in the repository.
            if (!dict.TryAdd(scriptName, content))
                throw new InvalidOperationException(
                    $"Embedded resources '{origins[scriptName]}' and '{name}' in assembly '{assembly.FullName}' " +
                    $"both yield script name '{scriptName}'.");

            origins[scriptName] = name;
        }

        return dict.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    private static async Task EnsureHistoryTableAsync(SqlConnection conn, SqlTransaction? tx, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = CreateHistoryTableSql;
        cmd.CommandType = System.Data.CommandType.Text;
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    private static async Task ApplyOneAsync(SqlConnection conn, SqlTransaction? tx, string scriptName, string content, CancellationToken ct)
    {
        var hash = ComputeSha256(content);
        if (await IsAlreadyAppliedAsync(conn, tx, scriptName, hash, ct).ConfigureAwait(false))
            return;

        var batches = SqlBatch.Split(content);
        foreach (var batch in batches)
        {
            if (string.IsNullOrWhiteSpace(batch))
                continue;
            await using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = batch;
            cmd.CommandType = System.Data.CommandType.Text;
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
        }

        await RecordAppliedAsync(conn, tx, scriptName, hash, ct).ConfigureAwait(false);
    }

    private static async Task<bool> IsAlreadyAppliedAsync(SqlConnection conn, SqlTransaction? tx, string scriptName, string hash, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT 1 FROM dbo.__DbScriptHistory WHERE ScriptName = @name AND Sha256 = @hash";
        cmd.Parameters.AddWithValue("@name", scriptName);
        cmd.Parameters.AddWithValue("@hash", hash);
        await using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
        return await reader.ReadAsync(ct).ConfigureAwait(false);
    }

    private static async Task RecordAppliedAsync(SqlConnection conn, SqlTransaction? tx, string scriptName, string hash, CancellationToken ct)
    {
        await using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = """
            MERGE dbo.__DbScriptHistory AS t
            USING (SELECT @name AS n, @hash AS h, @at AS a) AS s ON t.ScriptName = s.n
            WHEN MATCHED THEN UPDATE SET Sha256 = s.h, AppliedAt = s.a
            WHEN NOT MATCHED THEN INSERT (ScriptName, Sha256, AppliedAt) VALUES (s.n, s.h, s.a);
            """;
        cmd.Parameters.AddWithValue("@name", scriptName);
        cmd.Parameters.AddWithValue("@hash", hash);
        cmd.Parameters.AddWithValue("@at", DateTime.UtcNow);
        await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
    }

    internal static string ComputeSha256(string content)
    {
        var bytes = Encoding.UTF8.GetBytes(content);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
