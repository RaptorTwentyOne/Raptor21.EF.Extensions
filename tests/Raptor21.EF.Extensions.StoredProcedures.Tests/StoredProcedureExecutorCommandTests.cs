using System.Data;
using Microsoft.Data.SqlClient;
using Raptor21.EF.Extensions.StoredProcedures.Contracts;
using Raptor21.EF.Extensions.StoredProcedures.Execution;
using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Tests;

/// <summary>
/// Command shape and parameter binding: <c>BuildCommand</c>, <c>CreateReturnParameter</c> and the
/// <c>CreateParameter</c> rules that are visible on the finished command.
/// </summary>
/// <remarks>
/// Every case runs against a bare <c>new SqlConnection()</c> — no connection string, never opened —
/// because <c>SqlConnection.CreateCommand()</c> is nothing more than <c>new SqlCommand(null, this)</c>
/// and performs no I/O. Nothing here needs a server, and nothing here would behave differently if one
/// were present, because <c>BuildCommand</c> returns before anything is sent.
///
/// The connection arrives wrapped in <c>SqlConnectionLease.Own</c>, which is the same lease the default
/// <c>ISqlConnectionProvider.LeaseAsync</c> produces: an owned connection, inside no transaction. The
/// lease is deliberately never disposed here — that would dispose the connection, and every case reads
/// the finished command afterwards.
/// </remarks>
public class StoredProcedureExecutorCommandTests
{
    [Fact]
    public void BuildCommand_SetsBracketQuotedTwoPartName()
    {
        using var cmd = StoredProcedureExecutor.BuildCommand(
            SqlConnectionLease.Own(new SqlConnection()),
            TestContracts.GetBySku,
            new object?[] { "SKU-1" },
            "[dbo].[Product_GetBySku]");

        // The name arrives already quoted: BuildCommand takes it as an argument rather than rebuilding it
        // from the contract, so all four public entry points have to agree on the same interpolation.
        Assert.Equal("[dbo].[Product_GetBySku]", cmd.CommandText);
    }

    [Fact]
    public void BuildCommand_SetsCommandTypeStoredProcedure()
    {
        using var cmd = StoredProcedureExecutor.BuildCommand(
            SqlConnectionLease.Own(new SqlConnection()),
            TestContracts.Touch,
            new object?[] { 1 },
            "[dbo].[Product_TouchStamp]");

        // This is why the bracket quoting above is decorative rather than a defence against injection:
        // the text is sent as an RPC name and never parsed as a batch, so there is no statement for a
        // hostile schema or procedure name to escape into.
        Assert.Equal(CommandType.StoredProcedure, cmd.CommandType);
    }

    [Fact]
    public void BuildCommand_LeavesTheTransactionNullWhenTheLeaseHasNone()
    {
        using var cmd = StoredProcedureExecutor.BuildCommand(
            SqlConnectionLease.Own(new SqlConnection()),
            TestContracts.Touch,
            new object?[] { 1 },
            "[dbo].[Product_TouchStamp]");

        // `cmd.Transaction = lease.Transaction` is unconditional, and this is the case where the value is
        // null: an owned lease is inside no transaction, because the library opened the connection itself
        // a statement earlier. Assigning null is a no-op, so the command is exactly what it always was —
        // which is the whole of what a consumer without a unit of work sees of this change.
        //
        // The non-null half cannot be asserted here: SqlTransaction has no public constructor and cannot
        // be obtained without a server, so it is proved by the sample's live borrowing step instead.
        Assert.Null(cmd.Transaction);
    }

    [Fact]
    public void BuildCommand_BuildsOnTheLeasesConnection()
    {
        var connection = new SqlConnection();

        using var cmd = StoredProcedureExecutor.BuildCommand(
            SqlConnectionLease.Own(connection),
            TestContracts.Touch,
            new object?[] { 1 },
            "[dbo].[Product_TouchStamp]");

        // The command comes from the lease's connection and from nowhere else, so a borrowing provider's
        // connection is the one every parameter is bound against.
        Assert.Same(connection, cmd.Connection);
    }

    [Fact]
    public void BuildCommand_DefaultLease_ThrowsBeforeTheArityCheck()
    {
        // A struct is default-constructible whether the library likes it or not, so `default` is a value a
        // caller can reach: it is what a hand-written provider returns from an unimplemented LeaseAsync.
        // Reading lease.Connection first is what the old ArgumentNullException.ThrowIfNull(connection)
        // became, and it keeps this file's ordering rule — everything decidable from the arguments alone
        // is decided before a SqlCommand exists — with the arity mismatch below never reached.
        var ex = Assert.Throws<InvalidOperationException>(() => StoredProcedureExecutor.BuildCommand(
            default,
            TestContracts.Touch,
            Array.Empty<object?>(),
            "[dbo].[Product_TouchStamp]"));

        Assert.Contains("LeaseAsync", ex.Message);
    }

    [Fact]
    public void BuildCommand_PrependsReturnParameterAtIndexZero()
    {
        using var cmd = StoredProcedureExecutor.BuildCommand(
            SqlConnectionLease.Own(new SqlConnection()),
            TestContracts.Upsert,
            new object?[] { "s", "n", 1.5m, 0 },
            "[dbo].[Product_Upsert]");

        // Index 0 is a convention the rest of the executor leans on without ever re-checking it: every
        // read of a RETURN value goes straight to cmd.Parameters[0].
        Assert.Equal(5, cmd.Parameters.Count);
        Assert.Equal("@return", cmd.Parameters[0].ParameterName);
        Assert.Equal(ParameterDirection.ReturnValue, cmd.Parameters[0].Direction);
        Assert.Equal(SqlDbType.Int, cmd.Parameters[0].SqlDbType);
    }

    [Fact]
    public void BuildCommand_ZeroParameterContract_StillGetsTheReturnParameter()
    {
        using var cmd = StoredProcedureExecutor.BuildCommand(
            SqlConnectionLease.Own(new SqlConnection()),
            TestContracts.Ping,
            Array.Empty<object?>(),
            "[dbo].[Ping]");

        // The slot is unconditional, so even ExecuteNonQueryAsync — which never reads Parameters[0] —
        // pays for a RETURN parameter on every call to every procedure.
        var p = Assert.Single(cmd.Parameters.Cast<SqlParameter>());
        Assert.Equal("@return", p.ParameterName);
    }

    [Fact]
    public void BuildCommand_BindsContractParametersPositionallyFromIndexOne()
    {
        var contract = TestContracts.Contract(
            "dbo",
            "P",
            [
                new ProcParamSpec("@a", new SqlTypeSpec("int")),
                new ProcParamSpec("@b", new SqlTypeSpec("int")),
                new ProcParamSpec("@c", new SqlTypeSpec("int")),
            ]);

        using var cmd = StoredProcedureExecutor.BuildCommand(
            SqlConnectionLease.Own(new SqlConnection()),
            contract,
            new object?[] { 1, 2, 3 },
            "[dbo].[P]");

        // Names come only from the contract and values only from the caller's array, paired by position
        // and shifted one place by the RETURN slot. A caller therefore cannot name a parameter, and a
        // reordered argument list binds silently to the wrong names.
        Assert.Equal(new[] { "@return", "@a", "@b", "@c" }, cmd.Parameters.Cast<SqlParameter>().Select(p => p.ParameterName));
        Assert.Equal(1, cmd.Parameters[1].Value);
        Assert.Equal(2, cmd.Parameters[2].Value);
        Assert.Equal(3, cmd.Parameters[3].Value);
    }

    [Fact]
    public void BuildCommand_NullValue_BecomesDbNull()
    {
        var contract = TestContracts.OneParameter("@Name", new SqlTypeSpec("varchar"));

        using var cmd = StoredProcedureExecutor.BuildCommand(
            SqlConnectionLease.Own(new SqlConnection()),
            contract,
            new object?[] { null },
            "[dbo].[P]");

        // SqlClient reads a CLR null as "parameter not supplied", which for a procedure with a defaulted
        // parameter is a different call from passing NULL. CreateParameter's `value ?? DBNull.Value` is
        // the single line that keeps the two apart, so a null argument always means SQL NULL here.
        Assert.Equal(DBNull.Value, cmd.Parameters[1].Value);
    }

    [Fact]
    public void BuildCommand_NonNullValue_IsAssignedVerbatimWithNoCoercion()
    {
        var contract = TestContracts.OneParameter("@Id", new SqlTypeSpec("int"));

        using var cmd = StoredProcedureExecutor.BuildCommand(
            SqlConnectionLease.Own(new SqlConnection()),
            contract,
            new object?[] { "7" },
            "[dbo].[P]");

        // A string bound to an int parameter is accepted without a word: CreateParameter assigns the
        // value and ApplySqlType then stamps the declared type over it, converting nothing in either
        // direction. The mismatch stays invisible until SqlClient tries to put it on the wire.
        Assert.Equal("7", cmd.Parameters[1].Value);
        Assert.Equal(SqlDbType.Int, cmd.Parameters[1].SqlDbType);
    }

    [Fact]
    public void BuildCommand_OutputSpec_BindsDirectionOutputNeverInputOutput()
    {
        // Upsert's @Id is the contract's fourth parameter and IsOutput, so the RETURN slot pushes it to
        // command index 4.
        using var cmd = StoredProcedureExecutor.BuildCommand(
            SqlConnectionLease.Own(new SqlConnection()),
            TestContracts.Upsert,
            new object?[] { "SKU-9", "Widget", 9.99m, 42 },
            "[dbo].[Product_Upsert]");

        Assert.Equal(ParameterDirection.Output, cmd.Parameters[4].Direction);

        // ProcParamSpec carries a single bool, so there is no shape of contract the generator or a hand
        // author could write that reaches ParameterDirection.InputOutput: a parameter is either pure IN
        // or pure OUT, never both.
        Assert.DoesNotContain(cmd.Parameters.Cast<SqlParameter>(), p => p.Direction == ParameterDirection.InputOutput);

        // The seed is still written to .Value even though a pure OUTPUT parameter discards it — README
        // already documents the discarding, and this pins the assignment that survives it as dead work.
        Assert.Equal(42, cmd.Parameters[4].Value);
    }

    [Fact]
    public void BuildCommand_OutputCharacterParameterWithNoLength_GetsSizeZeroWhileBinaryGetsMinusOne()
    {
        // The exact specs RenderSqlType emits for [Sql("@Code", "varchar", Output = true)] and its
        // varbinary twin when no length is given: MaxLength null on both.
        var character = TestContracts.OneParameter("@Code", new SqlTypeSpec("varchar"), isOutput: true);
        var binary = TestContracts.OneParameter("@Blob", new SqlTypeSpec("varbinary"), isOutput: true);

        // Both values stay null so that Size reports the spec rather than the argument: SqlParameter.Size
        // falls back to the length of its own value whenever no size was set, which a seeded string would
        // quietly mask.
        using var characterCommand = StoredProcedureExecutor.BuildCommand(
            SqlConnectionLease.Own(new SqlConnection()), character, new object?[] { null }, "[dbo].[P]");
        using var binaryCommand = StoredProcedureExecutor.BuildCommand(
            SqlConnectionLease.Own(new SqlConnection()), binary, new object?[] { null }, "[dbo].[P]");

        Assert.Equal(ParameterDirection.Output, characterCommand.Parameters[1].Direction);
        Assert.Equal(ParameterDirection.Output, binaryCommand.Parameters[1].Direction);

        // ApplySqlType's two defaults disagree: the four character branches fall back to 0, the binary
        // branch to -1. Whether SqlClient can return a value through a zero-sized output buffer is
        // SqlClient's business and is not provable from this repository, so the asymmetry is pinned as a
        // plain fact about what the library sets.
        Assert.Equal(0, characterCommand.Parameters[1].Size);
        Assert.Equal(-1, binaryCommand.Parameters[1].Size);
    }

    [Fact]
    public void BuildCommand_ValueLongerThanMaxLength_IsNotCheckedOrRejected()
    {
        // The shape README.md documents, down to the length.
        var contract = TestContracts.OneParameter("@AccountID", new SqlTypeSpec("varchar", 21));

        using var cmd = StoredProcedureExecutor.BuildCommand(
            SqlConnectionLease.Own(new SqlConnection()),
            contract,
            new object?[] { new string('x', 25) },
            "[dbo].[P]");

        // CreateParameter performs no comparison whatsoever between the value and spec.MaxLength, which
        // is the whole of what is provable offline: the oversized value is accepted, and the declared
        // size travels beside it untouched. What SqlClient does with a 25-character value in a 21-wide
        // slot is its own documented behaviour, not this library's.
        Assert.Equal(21, cmd.Parameters[1].Size);
        Assert.Equal(25, ((string)cmd.Parameters[1].Value!).Length);
    }

    [Fact]
    public void BuildCommand_ArityMismatch_ThrowsArgumentExceptionNamingBothCounts()
    {
        var contract = TestContracts.Contract(
            "dbo",
            "X",
            [
                new ProcParamSpec("@a", new SqlTypeSpec("int")),
                new ProcParamSpec("@b", new SqlTypeSpec("int")),
            ]);

        var ex = Assert.Throws<ArgumentException>(() => StoredProcedureExecutor.BuildCommand(
            SqlConnectionLease.Own(new SqlConnection()),
            contract,
            new object?[] { 1, 2, 3 },
            "[dbo].[X]"));

        // A generated call site can never produce this, so the audience is whoever hand-wrote a contract
        // or called the executor directly — someone holding neither count. The message has to carry the
        // procedure and both numbers to be actionable at all, which is why they are asserted rather than
        // just the exception type.
        Assert.Equal("parameterValues", ex.ParamName);
        Assert.Contains("[dbo].[X]", ex.Message);
        Assert.Contains("contract has 2", ex.Message);
        Assert.Contains("got 3", ex.Message);
    }

    [Fact]
    public void BuildCommand_NullContract_ThrowsArgumentNullException()
    {
        // EXPECTED TO FAIL. Defect: StoredProcedureExecutor has no argument guards at all — not one
        // ArgumentNullException.ThrowIfNull anywhere in the file — so a null contract surfaces as a
        // NullReferenceException at `contract.Parameters` naming neither the argument nor the procedure.
        // StoredProcedureValidator and StoredProcedureSchemaManager both guard their arguments, so the
        // library is inconsistent with itself rather than deliberately unguarded.
        var ex = Assert.Throws<ArgumentNullException>(() => StoredProcedureExecutor.BuildCommand(
            SqlConnectionLease.Own(new SqlConnection()),
            null!,
            Array.Empty<object?>(),
            "[dbo].[X]"));

        Assert.Equal("contract", ex.ParamName);
    }

    [Fact]
    public void BuildCommand_NullParameterValues_ThrowsArgumentNullException()
    {
        // EXPECTED TO FAIL, same missing-guard defect. Here the NullReferenceException lands on
        // `parameterValues.Length` — after the SqlCommand has already been created and the RETURN
        // parameter added, so the half-built command is abandoned undisposed on the way out.
        var ex = Assert.Throws<ArgumentNullException>(() => StoredProcedureExecutor.BuildCommand(
            SqlConnectionLease.Own(new SqlConnection()),
            TestContracts.Ping,
            null!,
            "[dbo].[Ping]"));

        Assert.Equal("parameterValues", ex.ParamName);
    }

    [Fact]
    public void CreateReturnParameter_HasNoSizeOrValue()
    {
        var p = StoredProcedureExecutor.CreateReturnParameter();

        Assert.Equal("@return", p.ParameterName);
        Assert.Equal(ParameterDirection.ReturnValue, p.Direction);
        Assert.Equal(SqlDbType.Int, p.SqlDbType);
        Assert.Equal(0, p.Size);

        // The null Value is load-bearing rather than incidental. It is what makes an unpopulated RETURN
        // slot read back as null instead of DBNull, and the executor's two coercion paths only agree on
        // null: ExecuteWithOutputsAsync's `is int i ? i : 0` yields 0 for anything non-int, while
        // ExecuteReturnValueAsync's `?? 0` would hand a DBNull straight to Convert.ToInt32 and throw.
        Assert.Null(p.Value);
    }
}
