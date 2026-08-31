using Microsoft.Data.SqlClient;

namespace Raptor21.EF.Extensions.StoredProcedures.Execution;

/// <summary>
/// Supplies the <see cref="SqlConnection"/> a generated stored-procedure group method runs on, so the
/// caller never repeats CreateConnection/OpenAsync boilerplate. By default the generated method body
/// owns that connection — it is created, opened and disposed for the one call — and a provider that
/// borrows a connection somebody else owns says so by overriding <see cref="LeaseAsync"/>.
/// </summary>
public interface ISqlConnectionProvider
{
    /// <summary>Creates a new, unopened <see cref="SqlConnection"/>.</summary>
    SqlConnection Create();

    /// <summary>
    /// Hands a generated method body the connection it should run on, and the transaction that
    /// connection is inside. The default creates one from <see cref="Create"/>, opens it, and gives
    /// the body ownership of it — which is what every call did before this member existed, and what
    /// every call still does unless a provider overrides this.
    ///
    /// A provider that borrows an existing connection — one an ORM or a unit of work already owns —
    /// overrides this and returns <see cref="SqlConnectionLease.Borrow"/> instead. The body will then
    /// not open it, not close it and not dispose it.
    /// </summary>
    /// <param name="cancellationToken">Cancels the open the default implementation performs.</param>
    ValueTask<SqlConnectionLease> LeaseAsync(CancellationToken cancellationToken = default)
        => SqlConnectionLease.OpenOwnedAsync(Create(), cancellationToken);
}
