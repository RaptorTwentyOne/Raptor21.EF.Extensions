using System.Data;
using System.Reflection;
using Microsoft.Data.SqlClient;
using Raptor21.EF.Extensions.StoredProcedures.Contracts;
using Raptor21.EF.Extensions.StoredProcedures.Execution;
using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator.Tests;

/// <summary>
/// What the emitted body does, rather than what it says: the generated code is compiled, loaded and run
/// against a provider that gives it an owned connection, one that lends it a connection it must leave
/// alone, and one whose connection fails to open.
/// </summary>
/// <remarks>
/// <see cref="GeneratedBodyShapeTests"/> pins the text of the one connection statement, which is what
/// stops the shape drifting. It cannot show what the statement does, and the whole borrowing design lives
/// in the difference: the same <c>await using</c> must dispose a connection the provider created and must
/// not touch one the provider borrowed. That is asserted here on the connection itself — its connection
/// string, its state, its <c>Disposed</c> and <c>StateChange</c> events — and not on a flag set by a fake.
///
/// The providers are the same two shapes a consumer writes. One implements <c>Create</c> and nothing
/// else, which is every provider that existed before leases did, and reaches
/// <c>ISqlConnectionProvider.LeaseAsync</c> through its default implementation; the other overrides
/// <c>LeaseAsync</c> and borrows. Neither is registered with a container and neither knows it is in a
/// test, so what passes here is what a consumer gets.
/// </remarks>
public class GeneratedBodyBehaviourTests
{
    private const string ConnectionString = "Server=nowhere;Database=x";

    private const string Source = """
        using System.Threading;
        using System.Threading.Tasks;
        using Raptor21.EF.Extensions.StoredProcedures.Generated;

        namespace Demo;

        [StoredProcedureGroup]
        public partial class P
        {
            [StoredProcedure("dbo.Touch")]
            public partial Task TouchAsync([Sql("@Id", "int")] int id, CancellationToken ct = default);

            [StoredProcedure("dbo.Count")]
            public partial Task<int> CountAsync([Sql("@Id", "int")] int id, CancellationToken ct = default);
        }
        """;

    // Generated, compiled and loaded once for the class. Running the generator and emitting an assembly is
    // the expensive part, and every case below drives these same two bodies with a different provider.
    private static readonly Assembly Generated = GeneratorTestHarness.RunAndLoad(Source);

    [Fact]
    public async Task ProviderWithOnlyCreate_IsCalledThroughTheDefaultLeaseAndItsConnectionIsDisposedWhenTheOpenFails()
    {
        // A provider written before leases existed, unedited: one member, no override. The generated body
        // calls LeaseAsync on it anyway, through the interface's default implementation, which is the
        // whole of the source-compatibility claim — and this is that claim executed rather than argued.
        var provider = new CreateOnlyProvider();
        var executor = new RecordingExecutor();
        var group = NewGroup(provider, executor);

        // The connection has no connection string, so SqlClient refuses to open it client-side: the open
        // fails immediately, with no server, no socket and no wait.
        await Assert.ThrowsAnyAsync<InvalidOperationException>(() => CallAsync(group, "TouchAsync", 7));

        // Create() ran once — one call, one connection, exactly as the two old statements did — and the
        // connection it produced was disposed on the way out. That is the guarantee the old prologue got
        // for free by declaring `await using var __conn` BEFORE opening it, and losing it would leak a
        // connection per failed open in the one situation where connections are already scarce.
        Assert.Single(provider.Created);
        Assert.Equal(1, provider.Disposals);

        // And nothing reached the executor, so the failure is the open, not something further in.
        Assert.Empty(executor.Calls);
    }

    [Fact]
    public async Task OwnedLease_TheConnectionSurvivesTheExecutorCallAndIsDisposedAfterIt()
    {
        var connection = new SqlConnection(ConnectionString);
        var provider = new OwningProvider(connection);
        var executor = new RecordingExecutor();
        var group = NewGroup(provider, executor);

        await CallAsync(group, "TouchAsync", 7);

        // One lease for one call, handed to the executor as it was given: the body neither replaces the
        // connection nor invents a transaction for it.
        Assert.Equal(1, provider.Leases);
        var call = Assert.Single(executor.Calls);
        Assert.Same(connection, call.Lease.Connection);
        Assert.True(call.Lease.OwnsConnection);
        Assert.Null(call.Lease.Transaction);

        // The contract and the values travelled unchanged beside it, so nothing about the lease disturbed
        // what the call was for.
        Assert.Equal("dbo", call.Contract.Schema);
        Assert.Equal("Touch", call.Contract.Name);
        Assert.Equal(new object?[] { 7 }, call.Values);

        // Ordering, which is the part a text assertion cannot see: the connection was alive while the
        // executor ran — a disposed SqlConnection reports an empty connection string — and dead once the
        // body returned. The `await using` scope really does span the call rather than ending at it.
        Assert.Equal(ConnectionString, call.ConnectionStringDuringCall);
        Assert.Equal(string.Empty, connection.ConnectionString);
    }

    [Fact]
    public async Task BorrowedLease_TheConnectionIsNeverDisposedAndTheReleaseRunsOncePerCall()
    {
        var connection = new SqlConnection(ConnectionString);
        var release = new CountingRelease();
        var provider = new BorrowingProvider(connection, release);
        var executor = new RecordingExecutor();
        var group = NewGroup(provider, executor);

        var transitions = new List<ConnectionState>();
        connection.StateChange += (_, e) => transitions.Add(e.CurrentState);
        var disposals = 0;
        connection.Disposed += (_, _) => disposals++;

        await CallAsync(group, "TouchAsync", 7);
        await CallAsync(group, "TouchAsync", 8);

        // The same emitted statement, the same `await using`, and this time it disposes nothing: the
        // connection keeps its connection string, is never disposed, and never changes state — so it was
        // not opened by us, not closed by us, and would not have been re-opened had it arrived open, which
        // is the state a unit of work hands one over in. Nothing in the body reads ConnectionState, so an
        // open connection travels these same statements.
        Assert.Equal(ConnectionString, connection.ConnectionString);
        Assert.Equal(ConnectionState.Closed, connection.State);
        Assert.Empty(transitions);
        Assert.Equal(0, disposals);

        // What the body does dispose is the lease, once per call, which runs the provider's own undo once
        // per call. On the Entity Framework path that undo is the close matching the open the provider
        // itself performed, so a body that leaked one or ran it twice would either strand a connection or
        // close one out from under a live transaction.
        Assert.Equal(2, provider.Leases);
        Assert.Equal(2, release.Count);

        // And the executor saw a borrowed lease both times, still carrying the connection it was given and
        // still in the state it arrived in — so the body did not open it for the duration of the call and
        // close it again afterwards, which is the one way the assertions above could all have held while
        // the connection was still interfered with.
        Assert.Equal(2, executor.Calls.Count);
        Assert.All(executor.Calls, c =>
        {
            Assert.Same(connection, c.Lease.Connection);
            Assert.False(c.Lease.OwnsConnection);
            Assert.Equal(ConnectionState.Closed, c.StateDuringCall);
        });
    }

    [Fact]
    public async Task TheExecutorsResultTravelsBackOutOfTheBodyUnchanged()
    {
        var connection = new SqlConnection(ConnectionString);
        var executor = new RecordingExecutor();
        var group = NewGroup(new BorrowingProvider(connection, new CountingRelease()), executor);

        var result = await CallAsync(group, "CountAsync", 3);

        // The RETURN-value tail returns what the executor returned, through the lease and out of the
        // `await using`, with no unwrapping and no default value substituted on the way. The value is
        // deliberately not 0, so a body that returned default would fail here rather than pass by luck.
        Assert.Equal(RecordingExecutor.ReturnValue, Assert.IsType<int>(result));
        Assert.Equal("Count", Assert.Single(executor.Calls).Contract.Name);
    }

    /// <summary>Builds the generated group class over a provider and an executor, the way a container does.</summary>
    private static object NewGroup(ISqlConnectionProvider provider, IStoredProcedureExecutor executor) =>
        Activator.CreateInstance(Generated.GetType("Demo.P", throwOnError: true)!, provider, executor)!;

    /// <summary>Awaits one generated method by name and returns its result, or null for a void one.</summary>
    /// <remarks>
    /// The generated implementing part repeats no default values, so both arguments are supplied here -
    /// the token explicitly, exactly as the emitted body forwards it to the provider and the executor.
    /// </remarks>
    private static async Task<object?> CallAsync(object group, string methodName, int id)
    {
        var method = group.GetType().GetMethod(methodName)
            ?? throw new InvalidOperationException($"The generator emitted no {methodName}.");

        var task = (Task)method.Invoke(group, new object?[] { id, CancellationToken.None })!;
        await task.ConfigureAwait(false);

        return method.ReturnType.IsGenericType ? method.ReturnType.GetProperty("Result")!.GetValue(task) : null;
    }

    /// <summary>One executor call, with the state the connection was in while it was being made.</summary>
    private sealed record Call(
        SqlConnectionLease Lease,
        IStoredProcedureContract Contract,
        object?[] Values,
        string ConnectionStringDuringCall,
        ConnectionState StateDuringCall);

    /// <summary>
    /// Records what the emitted body handed it and returns without touching a server. It deliberately
    /// does not dispose the lease: the interface says the body that obtained it owns it, and an executor
    /// that disposed it would close a connection its caller may still be using.
    /// </summary>
    private sealed class RecordingExecutor : IStoredProcedureExecutor
    {
        /// <summary>A RETURN value no default could be mistaken for.</summary>
        public const int ReturnValue = 41;

        public List<Call> Calls { get; } = [];

        public Task<int> ExecuteReturnInt32Async(SqlConnectionLease lease, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken = default)
        {
            Record(lease, contract, parameterValues);
            return Task.FromResult(ReturnValue);
        }

        public Task<short> ExecuteReturnInt16Async(SqlConnectionLease lease, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken = default)
        {
            Record(lease, contract, parameterValues);
            return Task.FromResult((short)ReturnValue);
        }

        public Task ExecuteNonQueryAsync(SqlConnectionLease lease, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken = default)
        {
            Record(lease, contract, parameterValues);
            return Task.CompletedTask;
        }

        public Task<(int ReturnValue, IReadOnlyList<object?> Outputs)> ExecuteWithOutputsAsync(SqlConnectionLease lease, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken = default)
        {
            Record(lease, contract, parameterValues);
            (int ReturnValue, IReadOnlyList<object?> Outputs) result = (ReturnValue, Array.Empty<object?>());
            return Task.FromResult(result);
        }

        public Task<(int ReturnValue, IReadOnlyList<TRow> Rows)> ExecuteReturnResultSetAsync<TRow>(SqlConnectionLease lease, IStoredProcedureContract contract, object?[] parameterValues, CancellationToken cancellationToken = default)
            where TRow : IFromDataRecord<TRow>
        {
            Record(lease, contract, parameterValues);
            (int ReturnValue, IReadOnlyList<TRow> Rows) result = (ReturnValue, Array.Empty<TRow>());
            return Task.FromResult(result);
        }

        // The connection is read here, mid-call, because that is the only moment at which "still alive"
        // and "already disposed" can be told apart from outside the body.
        private void Record(SqlConnectionLease lease, IStoredProcedureContract contract, object?[] parameterValues) =>
            Calls.Add(new Call(lease, contract, parameterValues, lease.Connection.ConnectionString, lease.Connection.State));
    }

    /// <summary>
    /// A provider as they were written before leases existed: <c>Create</c> and nothing else, so every
    /// call reaches <c>LeaseAsync</c> through its default implementation.
    /// </summary>
    /// <remarks>
    /// The twin of the one in the runtime project's <c>SqlConnectionLeaseTests</c>, which cannot be shared
    /// across the two test assemblies. There it proves the default member does what the old prologue did;
    /// here it proves the emitted body is what calls it.
    /// </remarks>
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

    /// <summary>Hands over an already-created connection and gives the body ownership of it.</summary>
    /// <remarks>
    /// This is the default path with the open taken out, which is the most of it that is reachable with no
    /// server: <c>OpenOwnedAsync</c> is <c>Own</c> plus that open, so what the body then owns, and what
    /// disposing the lease does to it, is identical either way. The open itself is covered by the
    /// failed-open case above and by the runtime project.
    /// </remarks>
    private sealed class OwningProvider(SqlConnection connection) : ISqlConnectionProvider
    {
        public int Leases { get; private set; }

        public SqlConnection Create() =>
            throw new NotSupportedException("The test hands over a connection rather than opening one.");

        public ValueTask<SqlConnectionLease> LeaseAsync(CancellationToken cancellationToken = default)
        {
            Leases++;
            return new ValueTask<SqlConnectionLease>(SqlConnectionLease.Own(connection));
        }
    }

    /// <summary>The shape a unit-of-work adapter takes: borrow the caller's connection, undo only your own open.</summary>
    private sealed class BorrowingProvider(SqlConnection connection, IAsyncDisposable release) : ISqlConnectionProvider
    {
        public int Leases { get; private set; }

        public SqlConnection Create() =>
            throw new NotSupportedException("This provider borrows a connection and never creates one.");

        public ValueTask<SqlConnectionLease> LeaseAsync(CancellationToken cancellationToken = default)
        {
            Leases++;
            return new ValueTask<SqlConnectionLease>(SqlConnectionLease.Borrow(connection, null, release));
        }
    }

    /// <summary>Counts how often the body ran the provider's undo.</summary>
    private sealed class CountingRelease : IAsyncDisposable
    {
        public int Count { get; private set; }

        public ValueTask DisposeAsync()
        {
            Count++;
            return default;
        }
    }
}
