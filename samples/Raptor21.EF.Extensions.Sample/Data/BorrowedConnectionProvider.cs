using Microsoft.Data.SqlClient;
using Raptor21.EF.Extensions.StoredProcedures.Execution;

namespace Raptor21.EF.Extensions.Sample.Data;

/// <summary>
/// Runs generated procedure calls on a connection and transaction the caller already owns, instead of
/// opening one per call.
/// </summary>
/// <remarks>
/// <para>
/// This is the whole of what borrowing takes: override <see cref="LeaseAsync"/> and hand back a
/// <see cref="SqlConnectionLease.Borrow"/>.
/// The generated body then does not open the connection, does not close it and does not dispose it - it
/// disposes the lease, and a borrowed lease's disposal is a null check. The transaction travels with the
/// connection because the pair is read together: a command whose transaction belongs to a different
/// connection is refused, and a command with no transaction on a connection that has one is refused too.
/// </para>
/// <para>
/// No release handle is passed, because this sample performs its own open and its own close around the
/// whole block. A provider that had to do something to make the connection usable - Entity Framework's
/// reference-counted <c>OpenConnectionAsync</c> is the case that matters - passes its own undo as the
/// third argument instead, and the lease runs it exactly once, on the ordinary path and the exception
/// path alike.
/// </para>
/// <para>
/// <see cref="Create"/> throws rather than quietly handing back a second connection: a provider that
/// exists to avoid one should not be able to produce one by accident. Generated bodies never call it -
/// they go through <see cref="LeaseAsync"/> - so the only way to reach it is by asking this type for
/// something it does not do.
/// </para>
/// </remarks>
public sealed class BorrowedConnectionProvider(SqlConnection connection, SqlTransaction? transaction = null)
    : ISqlConnectionProvider
{
    public SqlConnection Create() =>
        throw new NotSupportedException(
            $"{nameof(BorrowedConnectionProvider)} borrows a connection and never creates one. " +
            "Generated procedure bodies call LeaseAsync; register SampleConnectionProvider instead if a call is meant to run on its own connection.");

    public ValueTask<SqlConnectionLease> LeaseAsync(CancellationToken cancellationToken = default) =>
        new(SqlConnectionLease.Borrow(connection, transaction));
}
