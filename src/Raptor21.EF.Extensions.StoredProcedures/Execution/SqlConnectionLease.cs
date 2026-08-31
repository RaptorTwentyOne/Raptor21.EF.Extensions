using System.Data.Common;
using Microsoft.Data.SqlClient;

namespace Raptor21.EF.Extensions.StoredProcedures.Execution;

/// <summary>
/// One generated call's claim on a connection, and the transaction that connection is inside.
/// A lease answers the only question the generated body has to ask — do I have to clean this up? —
/// and answers it without the body having to branch: disposing the lease releases whatever the
/// provider said needed releasing, which for a borrowed connection is nothing.
/// </summary>
public readonly struct SqlConnectionLease : IAsyncDisposable
{
    private readonly SqlConnection? _connection;
    private readonly SqlTransaction? _transaction;

    // The whole ownership decision, as one field. Own() puts the connection here, so disposing the
    // lease disposes it exactly as `await using var __conn` used to. Borrow() usually puts nothing
    // here, so disposal is a null check — the borrowed connection is not closed and not disposed.
    // A provider that had to DO something to make the connection usable (Entity Framework's
    // reference-counted open is the case this exists for) puts its own undo here instead, and gets
    // it run exactly once, on the ordinary path and on the exception path alike.
    private readonly IAsyncDisposable? _release;

    private SqlConnectionLease(SqlConnection? connection, SqlTransaction? transaction, IAsyncDisposable? release)
    {
        _connection = connection;
        _transaction = transaction;
        _release = release;
    }

    /// <summary>The connection every command in this call is built on.</summary>
    public SqlConnection Connection =>
        _connection ?? throw new InvalidOperationException(
            "A default SqlConnectionLease carries no connection. Leases come from ISqlConnectionProvider.LeaseAsync, " +
            "SqlConnectionLease.OpenOwnedAsync or SqlConnectionLease.Borrow.");

    /// <summary>The transaction the connection is inside, or null when it is inside none.</summary>
    public SqlTransaction? Transaction => _transaction;

    /// <summary>True when disposing this lease disposes the connection.</summary>
    public bool OwnsConnection => _connection is not null && ReferenceEquals(_release, _connection);

    /// <summary>Takes ownership of an already-open connection: disposing the lease disposes it.</summary>
    public static SqlConnectionLease Own(SqlConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        return new SqlConnectionLease(connection, null, connection);
    }

    /// <summary>Opens the connection and takes ownership of it. The default <see cref="ISqlConnectionProvider"/> path.</summary>
    public static async ValueTask<SqlConnectionLease> OpenOwnedAsync(SqlConnection connection, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // The old prologue declared the connection with `await using` BEFORE opening it, so a failed
            // open still disposed it. Reproduce that exactly: the lease does not exist yet, so this is
            // the only place that can.
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
        return Own(connection);
    }

    /// <summary>
    /// Runs on a connection somebody else owns. The lease never opens it, never closes it and never
    /// disposes it; the caller must hand it over open. <paramref name="release"/> is the provider's own
    /// undo, run once when the lease is disposed — pass null when there is nothing to undo.
    /// </summary>
    public static SqlConnectionLease Borrow(DbConnection connection, DbTransaction? transaction = null, IAsyncDisposable? release = null)
    {
        ArgumentNullException.ThrowIfNull(connection);

        // The one place the abstract ADO.NET types are narrowed. Everything downstream binds
        // SqlParameter and SqlDbType, which DbParameter.DbType cannot express, so this is a real
        // requirement rather than a convenience — and it says so here, naming the provider, instead
        // of surfacing as an InvalidCastException inside the parameter loop.
        if (connection is not SqlConnection sqlConnection)
            throw new ArgumentException(
                $"Stored procedure calls require a Microsoft.Data.SqlClient.SqlConnection; got {connection.GetType().FullName}. " +
                "Parameter binding sets SqlDbType, which no other ADO.NET provider has.",
                nameof(connection));

        if (transaction is null)
            return new SqlConnectionLease(sqlConnection, null, release);

        if (transaction is not SqlTransaction sqlTransaction)
            throw new ArgumentException(
                $"Stored procedure calls require a Microsoft.Data.SqlClient.SqlTransaction; got {transaction.GetType().FullName}.",
                nameof(transaction));

        // A completed transaction reports a null Connection and is the state a caller most easily
        // reaches by accident: it survives a Commit that did not clear the field it was read from.
        if (sqlTransaction.Connection is null)
            throw new ArgumentException(
                "The transaction has already been committed or rolled back.", nameof(transaction));

        if (!ReferenceEquals(sqlTransaction.Connection, sqlConnection))
            throw new ArgumentException(
                "The transaction belongs to a different connection than the one being borrowed.", nameof(transaction));

        return new SqlConnectionLease(sqlConnection, sqlTransaction, release);
    }

    /// <inheritdoc />
    public ValueTask DisposeAsync() => _release?.DisposeAsync() ?? default;
}
