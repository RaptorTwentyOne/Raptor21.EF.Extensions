using System.Data;
using System.Data.Common;
using System.Diagnostics.CodeAnalysis;
using Microsoft.Data.SqlClient;
using Raptor21.EF.Extensions.StoredProcedures.Execution;
using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Tests;

/// <summary>
/// The ownership half of <see cref="SqlConnectionLease"/>: who disposes what, and what a lease refuses.
/// </summary>
/// <remarks>
/// All of it runs with no server, because the two facts that matter are observable on a connection that
/// was never opened. Disposing a <see cref="SqlConnection"/> clears its <c>ConnectionString</c> to the
/// empty string — the user's options are dropped and the getter falls back to <c>""</c> — so "the owned
/// connection was disposed" and "the borrowed connection was left alone" are both plain assertions rather
/// than mocks. Disposal is also observable as the <c>Component.Disposed</c> event, which is what the
/// failed-open case below counts.
///
/// What is NOT reachable here is every arm of <c>Borrow</c> that needs a real <see cref="SqlTransaction"/>:
/// it has no public constructor and cannot be obtained without a server, so the completed-transaction and
/// wrong-connection guards are proved by the sample's live borrowing step instead.
/// </remarks>
public class SqlConnectionLeaseTests
{
    private const string ConnectionString = "Server=nowhere;Database=x";

    [Fact]
    public async Task Own_DisposesTheConnection()
    {
        var conn = new SqlConnection(ConnectionString);

        await using (var lease = SqlConnectionLease.Own(conn))
        {
            Assert.True(lease.OwnsConnection);
            Assert.Same(conn, lease.Connection);

            // A connection this library opened is inside no transaction, so an owned lease can only ever
            // carry a null one — which is what makes BuildCommand's unconditional assignment a no-op for
            // every consumer who never borrows.
            Assert.Null(lease.Transaction);
        }

        // The old generated prologue was `await using var __conn = ...`; this is that statement's exact
        // effect, now reached through the lease's release handle instead.
        Assert.Equal(string.Empty, conn.ConnectionString);
    }

    [Fact]
    public async Task Borrow_NeverDisposesNeverOpensNeverCloses()
    {
        var conn = new SqlConnection(ConnectionString);

        // The connection reports both of the things that could go wrong, rather than only where it ended
        // up: StateChange fires on every transition it makes, and Disposed fires once if it is disposed.
        // A lease that opened the connection and closed it again would leave the final state exactly as
        // it found it, and only the transition log would know.
        var transitions = new List<ConnectionState>();
        conn.StateChange += (_, e) => transitions.Add(e.CurrentState);
        var disposals = 0;
        conn.Disposed += (_, _) => disposals++;

        await using (var lease = SqlConnectionLease.Borrow(conn))
        {
            Assert.False(lease.OwnsConnection);
            Assert.Same(conn, lease.Connection);
            Assert.Null(lease.Transaction);
        }

        // The crux of the whole design, as two assertions: the connection string survives, so nothing
        // disposed it, and the state never left Closed, so nothing opened it and nothing closed it. A
        // borrowed connection is handed over open by its owner and handed back untouched.
        Assert.Equal(ConnectionString, conn.ConnectionString);
        Assert.Equal(ConnectionState.Closed, conn.State);
        Assert.Empty(transitions);
        Assert.Equal(0, disposals);

        // "And it is not re-opened when it was already open" is the same fact, and this is as close as an
        // offline test can stand to it: a connection cannot be opened without a server, so the borrowed
        // one here is closed rather than open. What makes that enough is that neither Borrow nor
        // DisposeAsync reads ConnectionState at all — there is no branch on it to take a different route
        // for an open connection — so the open case executes these same statements, which the empty
        // transition log proves do nothing. A lease that opened would also have been caught twice over:
        // the transition would have been recorded, and this connection string names a server that is not
        // there. The live case is the sample's borrowing step, which hands over a genuinely open one.
    }

    [Fact]
    public async Task Borrow_WithReleaseHandle_RunsItExactlyOnce()
    {
        var conn = new SqlConnection(ConnectionString);
        var release = new CountingRelease();

        await using (var lease = SqlConnectionLease.Borrow(conn, null, release))
        {
            Assert.False(lease.OwnsConnection);
            Assert.Equal(0, release.Count);
        }

        // The provider's own undo — Entity Framework's reference-counted close is the case this exists
        // for — runs once, and the connection is still not disposed by us.
        Assert.Equal(1, release.Count);
        Assert.Equal(ConnectionString, conn.ConnectionString);

        // And the hazard that follows from the lease being a struct, stated rather than hidden: a caller
        // who copies a lease and disposes both copies runs the release twice. The generated body has a
        // single `await using` and cannot, but a hand-written release should be idempotent anyway.
        var copy = SqlConnectionLease.Borrow(conn, null, release);
        await copy.DisposeAsync();
        await copy.DisposeAsync();
        Assert.Equal(3, release.Count);
    }

    [Fact]
    public async Task Default_IsSafeToDisposeAndNamesItselfOnUse()
    {
        // A struct is default-constructible whatever the library wants, so the degenerate value has to
        // answer for itself: disposing it does nothing, and reading its connection says where leases
        // actually come from instead of throwing a NullReferenceException from inside BuildCommand.
        await default(SqlConnectionLease).DisposeAsync();

        var lease = default(SqlConnectionLease);
        Assert.False(lease.OwnsConnection);
        Assert.Null(lease.Transaction);

        var ex = Assert.Throws<InvalidOperationException>(() => lease.Connection);
        Assert.Contains("ISqlConnectionProvider.LeaseAsync", ex.Message);
    }

    [Fact]
    public void Borrow_NonSqlConnection_ThrowsNamingTheProviderAndTheReason()
    {
        var ex = Assert.Throws<ArgumentException>(() => SqlConnectionLease.Borrow(new FakeDbConnection()));

        // The narrowing happens once, at the door, and says which provider was handed over and why no
        // other one can work — rather than surfacing later as an InvalidCastException inside the
        // parameter loop, where the type that was actually wrong no longer appears.
        Assert.Equal("connection", ex.ParamName);
        Assert.Contains(typeof(FakeDbConnection).FullName!, ex.Message);
        Assert.Contains("SqlDbType", ex.Message);
    }

    [Fact]
    public void Borrow_NonSqlTransaction_ThrowsTheSameWay()
    {
        var conn = new SqlConnection(ConnectionString);

        var ex = Assert.Throws<ArgumentException>(
            () => SqlConnectionLease.Borrow(conn, new FakeDbTransaction()));

        Assert.Equal("transaction", ex.ParamName);
        Assert.Contains(typeof(FakeDbTransaction).FullName!, ex.Message);

        // The connection arm passed, so the refusal is the transaction's alone — and the connection is
        // still untouched, because Borrow performs no I/O in any arm.
        Assert.Equal(ConnectionState.Closed, conn.State);
    }

    [Fact]
    public void Borrow_NullConnection_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => SqlConnectionLease.Borrow(null!));
    }

    [Fact]
    public async Task OpenOwnedAsync_FailedOpen_DisposesTheConnectionAndRethrows()
    {
        // No connection string, so SqlClient refuses client-side and no socket is ever opened.
        var conn = new SqlConnection();
        var disposals = 0;
        conn.Disposed += (_, _) => disposals++;

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            async () => await SqlConnectionLease.OpenOwnedAsync(conn));

        // The statement this replaced declared the connection with `await using` BEFORE opening it, so a
        // failed open still disposed it. That is reproduced deliberately: the lease does not exist yet on
        // this path, so OpenOwnedAsync's catch is the only place that can.
        Assert.Equal(1, disposals);
    }

    [Fact]
    public async Task DefaultLeaseAsync_CallsCreateExactlyOnceAndOwnsWhatItOpened()
    {
        // A provider that implements the one member it always had. It compiles unchanged and behaves
        // unchanged, because the default LeaseAsync IS the old prologue: Create(), then OpenAsync, then
        // ownership. Dispatch through the default interface member is what this pins.
        var provider = new CreateOnlyProvider();
        ISqlConnectionProvider asInterface = provider;

        await Assert.ThrowsAnyAsync<InvalidOperationException>(async () => await asInterface.LeaseAsync());

        // One connection created — the default body calls Create() once — and the failed open disposed it,
        // which is the no-leak guarantee on the path a consumer with a bad connection string actually hits.
        Assert.Single(provider.Created);
        Assert.Equal(1, provider.Disposals);
    }

    [Fact]
    public async Task BorrowingProvider_LeaseAsync_NeitherCreatesNorOpensAConnection()
    {
        var conn = new SqlConnection(ConnectionString);
        var release = new CountingRelease();
        var provider = new BorrowingProvider(conn, release);
        ISqlConnectionProvider asInterface = provider;

        var transitions = new List<ConnectionState>();
        conn.StateChange += (_, e) => transitions.Add(e.CurrentState);
        var disposals = 0;
        conn.Disposed += (_, _) => disposals++;

        await using (var lease = await asInterface.LeaseAsync())
        {
            // Reaching this line is itself an assertion. The override is the whole of what a borrowing
            // provider writes, and if it had not taken effect — if the default body had run instead — the
            // Create() below would have thrown NotSupportedException here rather than returning a lease.
            // That is the shape UnitOfWorkSqlConnectionProvider takes in Sampa.Framework.Data, for the
            // same reason: a provider that must not create a connection says so instead of quietly
            // handing back one that is outside the caller's transaction.
            Assert.Same(conn, lease.Connection);
            Assert.False(lease.OwnsConnection);
            Assert.Equal(0, release.Count);
        }

        // Through the provider surface a generated body actually calls, the borrowed connection is left
        // exactly as it arrived: never created here, never opened, never closed, never disposed. Only the
        // provider's own undo ran, once — which on the Entity Framework path is the close that matches the
        // open the provider itself performed, and nothing else.
        Assert.Equal(1, release.Count);
        Assert.Equal(ConnectionString, conn.ConnectionString);
        Assert.Equal(ConnectionState.Closed, conn.State);
        Assert.Empty(transitions);
        Assert.Equal(0, disposals);
    }

    /// <summary>Counts how often a lease ran the release handle it was given.</summary>
    private sealed class CountingRelease : IAsyncDisposable
    {
        public int Count { get; private set; }

        public ValueTask DisposeAsync()
        {
            Count++;
            return default;
        }
    }

    /// <summary>An <see cref="ISqlConnectionProvider"/> that implements <c>Create</c> and nothing else.</summary>
    private sealed class CreateOnlyProvider : ISqlConnectionProvider
    {
        public List<SqlConnection> Created { get; } = [];

        public int Disposals { get; private set; }

        public SqlConnection Create()
        {
            var connection = new SqlConnection();
            connection.Disposed += (_, _) => Disposals++;
            Created.Add(connection);
            return connection;
        }
    }

    /// <summary>
    /// The other half of the interface: a provider that overrides <c>LeaseAsync</c> to borrow, and whose
    /// <c>Create</c> refuses rather than handing back a connection of its own.
    /// </summary>
    private sealed class BorrowingProvider(SqlConnection connection, IAsyncDisposable release) : ISqlConnectionProvider
    {
        public SqlConnection Create() =>
            throw new NotSupportedException("This provider borrows a connection and never creates one.");

        public ValueTask<SqlConnectionLease> LeaseAsync(CancellationToken cancellationToken = default) =>
            new(SqlConnectionLease.Borrow(connection, null, release));
    }

    /// <summary>A non-SqlClient <see cref="DbConnection"/>, so the narrowing in <c>Borrow</c> has something to refuse.</summary>
    private sealed class FakeDbConnection : DbConnection
    {
        [AllowNull]
        public override string ConnectionString { get; set; } = "";

        public override string Database => "";

        public override string DataSource => "";

        public override string ServerVersion => "";

        public override ConnectionState State => ConnectionState.Closed;

        public override void ChangeDatabase(string databaseName) => throw new NotSupportedException();

        public override void Close() => throw new NotSupportedException();

        public override void Open() => throw new NotSupportedException();

        protected override DbCommand CreateDbCommand() => throw new NotSupportedException();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) => throw new NotSupportedException();
    }

    /// <summary>The transaction half of the same refusal.</summary>
    private sealed class FakeDbTransaction : DbTransaction
    {
        public override IsolationLevel IsolationLevel => IsolationLevel.ReadCommitted;

        protected override DbConnection? DbConnection => null;

        public override void Commit() => throw new NotSupportedException();

        public override void Rollback() => throw new NotSupportedException();
    }
}
