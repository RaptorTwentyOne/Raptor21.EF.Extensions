using Raptor21.EF.Extensions.StoredProcedures.Contracts;
using Raptor21.EF.Extensions.StoredProcedures.Validation;
using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Tests;

/// <summary>
/// Covers the argument guards on the two entry points a host calls at startup. Everything past the
/// guards opens a <c>SqlConnection</c>, so the guards are the entire boot path reachable without a
/// server — and they are worth pinning exactly, because a misconfigured host meets them first and the
/// parameter name in the exception is the only thing telling it which argument to fix.
/// </summary>
public class StoredProcedureSchemaManagerTests
{
    // Parseable and credential-free. Every test in this class throws before anything tries to connect,
    // so this string only ever has to survive SqlConnection's offline key=value syntax check.
    private const string ValidConnectionString = "Server=(local);Database=Fx;Integrated Security=True";

    private static IEnumerable<IStoredProcedureContract> NoContracts => Array.Empty<IStoredProcedureContract>();

    [Fact]
    public async Task ApplyAndValidateAsync_NullContracts_ThrowsBeforeAnythingElse()
    {
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(
            () => StoredProcedureSchemaManager.ApplyAndValidateAsync(
                ValidConnectionString,
                new FakeResourceAssembly(),
                "Fx.Db.",
                null!));

        // ThrowIfNull(contracts) is the manager's only logic of its own; everything after it is two
        // delegating awaits, which is precisely why this guard has to be right.
        Assert.Equal("contracts", ex.ParamName);
    }

    [Fact]
    public async Task ApplyAndValidateAsync_ContractsGuardOutranksTheConnectionStringGuard()
    {
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(
            () => StoredProcedureSchemaManager.ApplyAndValidateAsync(
                "",
                new FakeResourceAssembly(),
                "Fx.Db.",
                null!));

        // Two arguments are wrong at once and the caller is told about contracts, because the manager
        // runs its own guard before it delegates to the applier that would have rejected the empty
        // connection string. Pinning the winner keeps a later reordering from silently changing which
        // mistake a caller is told about first.
        Assert.Equal("contracts", ex.ParamName);
    }

    [Fact]
    public async Task ApplyAndValidateAsync_BlankConnectionString_ReportsTheAppliersGuard()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => StoredProcedureSchemaManager.ApplyAndValidateAsync(
                "   ",
                new FakeResourceAssembly(),
                "Fx.Db.",
                NoContracts));

        // Whitespace, not null, so ThrowIfNullOrWhiteSpace takes the ArgumentException arm rather than
        // the ArgumentNullException one. The name still arrives intact because the manager and the
        // applier happen to spell this parameter the same way — which is exactly what the assembly
        // case below shows is not guaranteed.
        Assert.Equal("connectionString", ex.ParamName);
    }

    [Fact]
    public async Task ApplyAndValidateAsync_NullAssembly_ReportsTheDeclaredParameterName()
    {
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(
            () => StoredProcedureSchemaManager.ApplyAndValidateAsync(
                ValidConnectionString,
                null!,
                "Fx.Db.",
                new[] { TestContracts.Ping }));

        // EXPECTED TO FAIL against today's library: the reported name is "assembly".
        // The manager declares the parameter as assemblyWithScripts but guards only contracts itself,
        // so the null reaches EmbeddedScriptApplier.ApplyEmbeddedScriptsAsync, whose own
        // ThrowIfNull(assembly) uses the internal name. The caller is then handed a parameter name that
        // appears nowhere in the signature it wrote against, and connectionString and resourcePrefix
        // agree by coincidence, so this is the only argument affected.
        Assert.Equal("assemblyWithScripts", ex.ParamName);
    }

    [Theory]
    [InlineData(null, typeof(ArgumentNullException))]
    [InlineData("", typeof(ArgumentException))]
    [InlineData("\t ", typeof(ArgumentException))]
    public async Task ValidateAsync_NullOrBlankConnectionString_Throws(string? connectionString, Type expectedExceptionType)
    {
        // ArgumentNullException derives from ArgumentException, so one ThrowsAnyAsync catches both arms
        // of ThrowIfNullOrWhiteSpace and the exact type is then asserted per case: null must not be
        // flattened into a plain ArgumentException, because a null connection string and a blank one
        // are different configuration mistakes.
        var ex = await Assert.ThrowsAnyAsync<ArgumentException>(
            () => StoredProcedureValidator.ValidateAsync(connectionString!, NoContracts));

        Assert.IsType(expectedExceptionType, ex);
        Assert.Equal("connectionString", ex.ParamName);
    }

    [Fact]
    public async Task ValidateAsync_NullContracts_Throws()
    {
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(
            () => StoredProcedureValidator.ValidateAsync(ValidConnectionString, null!));

        Assert.Equal("contracts", ex.ParamName);
    }

    [Fact]
    public async Task ValidateAsync_GuardOrderIsConnectionStringThenContracts()
    {
        var ex = await Assert.ThrowsAsync<ArgumentNullException>(
            () => StoredProcedureValidator.ValidateAsync(null!, null!));

        // Both guards would fire; the connection string is checked first, so that is the one name the
        // caller sees. Asserting the order rather than "some ArgumentNullException" is what makes this
        // test able to notice a reordering.
        Assert.Equal("connectionString", ex.ParamName);
    }

    [Fact]
    public async Task ValidateAsync_MalformedConnectionString_FailsAtConstructionNotAtConnect()
    {
        var ex = await Assert.ThrowsAsync<ArgumentException>(
            () => StoredProcedureValidator.ValidateAsync("this is not a connection string", NoContracts));

        // SqlConnection parses the key=value form in its constructor, so a syntactically broken string
        // is rejected offline and this test reaches no network even though ValidateAsync's next
        // statement is OpenAsync. The message belongs to SqlClient and is deliberately not asserted;
        // the null ParamName is what distinguishes this from the guard above, which always names
        // connectionString.
        Assert.Null(ex.ParamName);
    }
}
