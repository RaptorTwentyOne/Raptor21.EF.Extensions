using System.Reflection;
using Raptor21.EF.Extensions.StoredProcedures.Contracts;
using Raptor21.EF.Extensions.StoredProcedures.Scripts;
using Raptor21.EF.Extensions.StoredProcedures.Validation;

namespace Raptor21.EF.Extensions.StoredProcedures;

/// <summary>
/// Public API: applies embedded SQL scripts idempotently, then validates all stored procedure contracts.
/// Fails fast (throws) on first error. Use at app startup (e.g. ASP.NET Core host).
/// </summary>
/// <remarks>
/// Usage:
/// <code>
/// await StoredProcedureSchemaManager.ApplyAndValidateAsync(
///     connectionString,
///     typeof(MyAssembly.Marker).Assembly,
///     "MyLib.DbScripts.",
///     new[] { LoadUserDataContract.Instance, OtherContract.Instance },
///     useTransaction: true,
///     cancellationToken);
/// </code>
/// </remarks>
public static class StoredProcedureSchemaManager
{
    /// <summary>
    /// Applies all embedded .sql scripts from the assembly under the given resource prefix, then validates every contract.
    /// Throws on first error (script apply failure or validation mismatch).
    /// </summary>
    /// <param name="connectionString">SQL Server connection string.</param>
    /// <param name="assemblyWithScripts">Assembly containing embedded .sql resources (e.g. typeof(AnyTypeInAssembly).Assembly).</param>
    /// <param name="resourcePrefix">Resource name prefix (e.g. MyLib.DbScripts.).</param>
    /// <param name="contracts">Stored procedure contracts to validate after scripts are applied.</param>
    /// <param name="useTransaction">If true, all script batches run in a single transaction (default true).</param>
    /// <param name="ct">Cancellation token.</param>
    public static async Task ApplyAndValidateAsync(
        string connectionString,
        Assembly assemblyWithScripts,
        string resourcePrefix,
        IEnumerable<IStoredProcedureContract> contracts,
        bool useTransaction = true,
        CancellationToken ct = default)
    {
        // The applier and the validator guard these arguments too, but under their own parameter names: a
        // null assembly is reported as "assembly", which appears nowhere in the signature the caller wrote
        // against. The contracts guard stays first because it is the one argument the delegates would not
        // reject until the scripts had already been applied to a live database.
        ArgumentNullException.ThrowIfNull(contracts);
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionString);
        ArgumentNullException.ThrowIfNull(assemblyWithScripts);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePrefix);

        await EmbeddedScriptApplier.ApplyEmbeddedScriptsAsync(connectionString, assemblyWithScripts, resourcePrefix, useTransaction, ct).ConfigureAwait(false);
        await StoredProcedureValidator.ValidateAsync(connectionString, contracts, ct).ConfigureAwait(false);
    }
}
