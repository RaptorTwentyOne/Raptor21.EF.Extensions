using Raptor21.EF.Extensions.StoredProcedures.Contracts;
using Raptor21.EF.Extensions.StoredProcedures.Validation;
using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Tests;

/// <summary>
/// Covers the argument guards on <see cref="StoredProcedureValidator.ValidateAsync"/>, the one entry
/// point a host calls at startup. Everything past the guards opens a <c>SqlConnection</c>, so the guards
/// are the entire boot path reachable without a server - and they are worth pinning exactly, because a
/// misconfigured host meets them first and the parameter name in the exception is the only thing telling
/// it which argument to fix.
/// </summary>
public class StoredProcedureValidatorGuardTests
{
    // Parseable and credential-free. Every test in this class throws before anything tries to connect,
    // so this string only ever has to survive SqlConnection's offline key=value syntax check.
    private const string ValidConnectionString = "Server=(local);Database=Fx;Integrated Security=True";

    private static IEnumerable<IStoredProcedureContract> NoContracts => Array.Empty<IStoredProcedureContract>();

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
