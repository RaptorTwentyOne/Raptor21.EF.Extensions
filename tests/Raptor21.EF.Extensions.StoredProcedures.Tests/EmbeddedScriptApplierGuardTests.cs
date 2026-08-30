using Raptor21.EF.Extensions.StoredProcedures.Scripts;
using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Tests;

/// <summary>
/// The whole of <c>EmbeddedScriptApplier.ApplyEmbeddedScriptsAsync</c> that is reachable with no SQL
/// Server behind it: the three argument guards, and the zero-script early return that sits above the
/// first <c>new SqlConnection</c>.
/// </summary>
/// <remarks>
/// The method is <c>async</c>, so a guard never throws at the call site — it faults the returned task.
/// That is why every case here awaits the call rather than wrapping a synchronous invocation, and why a
/// hypothetical <c>Assert.Throws</c> would pass vacuously by never observing the task at all. Cases that
/// could conceivably get past the guards are handed a connection string that parses but can never
/// resolve, so a regression that reaches the connection fails in about a second rather than stalling the
/// suite behind the default connect timeout.
/// </remarks>
public class EmbeddedScriptApplierGuardTests
{
    // .invalid is reserved by RFC 2606 and is guaranteed never to resolve, and Connect Timeout=1 trims
    // the 15-second default to something CI can absorb. Only the parse has to succeed: SqlConnection's
    // constructor contacts nothing, so reaching it is silent and only OpenAsync would surface a failure.
    private const string UnreachableConnectionString = "Server=nosuchhost.invalid;Database=x;Connect Timeout=1;";

    private const string RealPrefix = "Fx.Db.";

    /// <summary>An assembly carrying one genuine .sql resource, so no case passes for want of scripts.</summary>
    private static FakeResourceAssembly OneScript() =>
        new FakeResourceAssembly().With("Fx.Db.001_Create.sql", "CREATE OR ALTER PROCEDURE dbo.X AS SELECT 1;");

    [Fact]
    public async Task ApplyEmbeddedScriptsAsync_NullConnectionString_ThrowsArgumentNullException()
    {
        // ArgumentException.ThrowIfNullOrWhiteSpace splits its two failures across two exception types:
        // null gets the derived ArgumentNullException, blank gets the base ArgumentException. ThrowsAsync
        // matches the type exactly rather than by assignability, so asking for the derived type here is
        // what pins which of the two a caller passing null actually receives.
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(
            () => EmbeddedScriptApplier.ApplyEmbeddedScriptsAsync(null!, OneScript(), RealPrefix));

        Assert.Equal("connectionString", ex.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task ApplyEmbeddedScriptsAsync_BlankConnectionString_ThrowsArgumentException(string connectionString)
    {
        // ThrowsAnyAsync deliberately, so that the type assertion below is the test's own work rather
        // than something the catch clause quietly performed: an empty or all-whitespace string must not
        // be reported as a null one, because the two say different things to whoever has to fix the call.
        var ex = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => EmbeddedScriptApplier.ApplyEmbeddedScriptsAsync(connectionString, OneScript(), RealPrefix));

        Assert.Equal("connectionString", ex.ParamName);
        Assert.IsNotType<ArgumentNullException>(ex);
    }

    [Fact]
    public async Task ApplyEmbeddedScriptsAsync_NullAssembly_ThrowsArgumentNullException()
    {
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(
            () => EmbeddedScriptApplier.ApplyEmbeddedScriptsAsync(UnreachableConnectionString, null!, RealPrefix));

        Assert.Equal("assembly", ex.ParamName);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task ApplyEmbeddedScriptsAsync_NullOrBlankResourcePrefix_Throws(string? resourcePrefix)
    {
        // The three inputs deliberately straddle both arms of ThrowIfNullOrWhiteSpace — null raises
        // ArgumentNullException and the two blanks raise ArgumentException — so the catch has to accept
        // the whole family. Which arm fires for which input is already pinned by the two connectionString
        // cases above; what matters here is that all three name the argument the caller got wrong.
        var ex = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => EmbeddedScriptApplier.ApplyEmbeddedScriptsAsync(UnreachableConnectionString, OneScript(), resourcePrefix!));

        Assert.Equal("resourcePrefix", ex.ParamName);
    }

    [Fact]
    public async Task ApplyEmbeddedScriptsAsync_GuardOrderIsConnectionStringThenAssemblyThenPrefix()
    {
        // Guard order is observable behaviour, not an implementation detail, because ParamName is the
        // only thing that tells a caller which argument to fix. It also matters one level up:
        // StoredProcedureSchemaManager.ApplyAndValidateAsync forwards its own assemblyWithScripts
        // parameter into this method's assembly parameter, so a null there is reported against a name
        // that does not appear on the method the caller actually invoked. Reproducing that finding
        // depends on knowing exactly which guard speaks first.
        var allThreeWrong = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => EmbeddedScriptApplier.ApplyEmbeddedScriptsAsync(null!, null!, null!));
        Assert.Equal("connectionString", allThreeWrong.ParamName);

        var lastTwoWrong = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => EmbeddedScriptApplier.ApplyEmbeddedScriptsAsync(UnreachableConnectionString, null!, null!));
        Assert.Equal("assembly", lastTwoWrong.ParamName);

        var prefixWrong = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => EmbeddedScriptApplier.ApplyEmbeddedScriptsAsync(UnreachableConnectionString, OneScript(), null!));
        Assert.Equal("resourcePrefix", prefixWrong.ParamName);
    }

    [Fact]
    public async Task ApplyEmbeddedScriptsAsync_BadArgument_FailsBeforeAnyConnectionAttempt()
    {
        // Half of this test is what does not happen. The assembly holds a real script and the connection
        // string points at a host that cannot exist, so if the prefix guard were ever reordered below
        // LoadScripts and new SqlConnection, the awaited task would fault with a SqlException instead —
        // and ThrowsAnyAsync<ArgumentException> reports that as a failure rather than swallowing it.
        var ex = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => EmbeddedScriptApplier.ApplyEmbeddedScriptsAsync(UnreachableConnectionString, OneScript(), ""));

        Assert.Equal("resourcePrefix", ex.ParamName);
    }

    [Fact]
    public async Task ApplyEmbeddedScriptsAsync_NoMatchingResources_CompletesWithoutConnecting()
    {
        // Only .txt resources, and a prefix none of them carries, so the filter inside LoadScripts rejects
        // every candidate twice over.
        var assembly = new FakeResourceAssembly()
            .With("Fx.Db.readme.txt", "notes, not a script")
            .With("Fx.Db.changelog.txt", "also not a script");

        var ex = await Record.ExceptionAsync(
            () => EmbeddedScriptApplier.ApplyEmbeddedScriptsAsync(UnreachableConnectionString, assembly, "No.Such.Prefix."));

        // Completing at all against an unresolvable host is the whole assertion: the only path that
        // returns without touching the network is the zero-script early return, so reaching this line
        // proves the short-circuit runs before the connection is constructed. That is also the only
        // non-guard behaviour of this public API observable without a seam change, and it comes at a
        // price worth naming — the method returns a bare Task, so "applied four scripts", "skipped four
        // already-applied scripts" and "found nothing and did nothing" are all the same to the caller.
        Assert.Null(ex);
    }

    [Fact]
    public async Task ApplyEmbeddedScriptsAsync_DotOnlyPrefix_SurvivesTheGuardAndMatchesNothing()
    {
        var assembly = new FakeResourceAssembly()
            .With("Fx.Db.001_Create.sql", "CREATE OR ALTER PROCEDURE dbo.X AS SELECT 1;")
            .With("Fx.Db.002_Seed.sql", "INSERT INTO dbo.X (Id) VALUES (1);");

        // The assembly really is carrying scripts — under their own prefix both are found — so nothing
        // below can be explained away as an empty fixture.
        Assert.Equal(2, EmbeddedScriptApplier.LoadScripts(assembly, RealPrefix).Count);

        // "." is non-empty and non-whitespace, so ThrowIfNullOrWhiteSpace waves it through, and the
        // normalisation that follows (TrimEnd('.') then + ".") turns it back into "." rather than into
        // anything a resource name could start with. The prefix guard therefore accepts a value that is
        // structurally incapable of matching.
        Assert.Empty(EmbeddedScriptApplier.LoadScripts(assembly, "."));

        var ex = await Record.ExceptionAsync(
            () => EmbeddedScriptApplier.ApplyEmbeddedScriptsAsync(UnreachableConnectionString, assembly, "."));

        // DEFECT, pinned rather than asserted away: a prefix that matches no resources returns success
        // having applied nothing, and the caller cannot tell that apart from a real application. Here the
        // two scripts above are silently skipped and the call still reports success. Through
        // StoredProcedureSchemaManager.ApplyAndValidateAsync the mistake then surfaces one step later as
        // the validator's "Stored procedure [dbo].[X] does not exist.", which points at the procedure
        // instead of at the resource prefix that was actually wrong. There is no stronger assertion to
        // write today: the method returns a bare Task, so the correct behaviour this case argues for —
        // reporting that nothing matched — has no place in the signature to be observed from.
        Assert.Null(ex);
    }
}
