using System.Data;
using Microsoft.Data.SqlClient;
using Raptor21.EF.Extensions.StoredProcedures.Contracts;
using Raptor21.EF.Extensions.StoredProcedures.Execution;
using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Tests;

// The contract types are allocation-only: no I/O, no branching, nothing that looks worth a test in
// isolation. They earn one anyway, because every other test in this project builds its fixtures out of
// them and the executor and the validator read them without a single guard. What is pinned here is not
// that a property returns what a constructor stored, but the four consequences of that: the constructor
// validates nothing, it aliases the array it is handed, identity is per-instance rather than structural,
// and a default struct carries a null type name into code that dereferences it.
public class ContractTests
{
    [Fact]
    public void GeneratedContract_StoresWhatItIsGivenAndDefaultsResultColumnsToNull()
    {
        var parameters = new[] { new ProcParamSpec("@a", new SqlTypeSpec("int")) };
        var @return = new ProcReturnSpec(ReturnKind.ReturnValue, new SqlTypeSpec("int"));

        var contract = new GeneratedContract("dbo", "P", parameters, @return);

        Assert.Equal("dbo", contract.Schema);
        Assert.Equal("P", contract.Name);
        var parameter = Assert.Single(contract.Parameters);
        Assert.Equal("@a", parameter.Name);
        Assert.Equal("int", parameter.SqlType.SqlTypeName);
        Assert.Equal(@return, contract.Return);

        // The optional argument is what separates a scalar procedure from a result-set one, and its
        // default is null rather than an empty list — a distinction ExecuteReturnResultSetAsync makes by
        // hand, since it treats null and empty alike but only null can be produced by omission.
        Assert.Null(contract.ResultColumns);
    }

    [Fact]
    public void GeneratedContract_ValidatesNothing_AndCanExpressInvalidStates()
    {
        // Every argument here is one the type system would rather not allow: empty identifiers, and a
        // null list behind a non-nullable annotation. The constructor is five assignments and rejects
        // none of it, which is deliberate — it is what lets these tests hand the executor and the
        // validator malformed contracts without a generator in the loop.
        var contract = new GeneratedContract("", "", null!, default);

        Assert.Equal("", contract.Schema);
        Assert.Equal("", contract.Name);
        Assert.Null(contract.Parameters);
        Assert.Equal(default(ProcReturnSpec), contract.Return);
        Assert.Null(contract.ResultColumns);

        // The cost of that permissiveness is paid downstream: a contract with null Parameters reaches
        // BuildCommand's `parameterValues.Length != parameters.Count`, which dereferences it and throws a
        // NullReferenceException naming nothing — not the procedure, not the property.
    }

    [Fact]
    public void GeneratedContract_DoesNotDefensivelyCopyTheParameterArray()
    {
        var array = new[] { new ProcParamSpec("@a", new SqlTypeSpec("int")) };
        var contract = new GeneratedContract("dbo", "P", array, TestContracts.ReturnValueInt32);

        array[0] = new ProcParamSpec("@other", new SqlTypeSpec("bigint"));

        // Not an academic point. The generator holds each contract in a static readonly field and hands
        // that one instance out through GeneratedProcedureRegistry.All, so the IReadOnlyList a consumer
        // receives is the same array object for the life of the process; anyone who casts it back to
        // ProcParamSpec[] can rewrite the binding of a procedure for everyone.
        Assert.Equal("@other", contract.Parameters[0].Name);
        Assert.Equal("bigint", contract.Parameters[0].SqlType.SqlTypeName);
    }

    [Fact]
    public void GeneratedContract_UsesReferenceEqualityNotStructuralEquality()
    {
        var a = TestContracts.Touch;
        var b = TestContracts.Touch;

        // Field for field these are the same contract, but GeneratedContract is a plain sealed class, so
        // Equals is object's and compares references.
        Assert.False(a.Equals(b));

        // Which is what a hashed collection sees too: b is stored beside a rather than deduplicated
        // against it, while a second reference to a collapses. (This holds whatever the hash codes are,
        // so it says nothing about hash collisions, which stay legal.)
        var deduplicated = new HashSet<GeneratedContract> { a, b, a };
        Assert.Equal(2, deduplicated.Count);

        // So no consumer can key on the contract itself, and the de-facto key everyone forms instead is
        // the bracketed name the executor and the validator both interpolate. On that key the two agree.
        Assert.Equal($"[{a.Schema}].[{a.Name}]", $"[{b.Schema}].[{b.Name}]");
    }

    [Fact]
    public void SpecStructs_UseOrdinalCaseSensitiveEquality()
    {
        var upperCase = new SqlTypeSpec("VARCHAR", 21);
        var lowerCase = new SqlTypeSpec("varchar", 21);

        // These two bind identically — ApplySqlType upper-cases the name before it matches — and they
        // validate identically, because CompareSqlType compares with OrdinalIgnoreCase. Record equality
        // is EqualityComparer<string>.Default, which is neither, so they compare unequal. Nothing in the
        // library compares specs today, so this is a trap rather than a live defect: the first drift
        // detector, contract cache or snapshot comparison built on record equality will report a
        // difference between two types that are the same type.
        Assert.False(upperCase.Equals(lowerCase));
        Assert.False(upperCase == lowerCase);

        // The same rule reaches the parameter name, where SQL Server itself is case-insensitive.
        var declaredCasing = new ProcParamSpec("@Sku", lowerCase);
        var otherCasing = new ProcParamSpec("@sku", lowerCase);
        Assert.False(declaredCasing == otherCasing);

        // Equality does work as expected when the strings match exactly, including for ColumnSpec, whose
        // second member is a Type and so compares by reference identity — one Type instance per type.
        Assert.True(new ColumnSpec("Id", typeof(int)) == new ColumnSpec("Id", typeof(int)));
    }

    [Fact]
    public void DefaultStructValues_CarryTheTrapsTheRuntimeTripsOver()
    {
        // A default SqlTypeSpec is reachable from any hand-written contract, and its type name is null.
        // Both consumers dereference it without a guard — ApplySqlType opens with SqlTypeName.Trim() and
        // CompareSqlType does the same — so this null is the source of both NREs.
        Assert.Null(default(SqlTypeSpec).SqlTypeName);
        Assert.Null(default(SqlTypeSpec).MaxLength);
        Assert.Null(default(SqlTypeSpec).Precision);
        Assert.Null(default(SqlTypeSpec).Scale);

        // A default parameter binds as an input, since CreateParameter reads IsOutput as
        // `IsOutput ? Output : Input`; omitting the flag never produces an output parameter by accident.
        Assert.Null(default(ProcParamSpec).Name);
        Assert.False(default(ProcParamSpec).IsOutput);

        // ReturnValue is the first member of the enum and therefore its zero value, so a default
        // ProcReturnSpec does not slip past the validator's return-type rule — it takes the checked
        // branch with a null type name and fails there, reporting an empty expected type.
        Assert.Equal(0, (int)ReturnKind.ReturnValue);
        Assert.Equal(ReturnKind.ReturnValue, default(ProcReturnSpec).Kind);
        Assert.Null(default(ProcReturnSpec).SqlType.SqlTypeName);
    }

    [Fact]
    public async Task ReturnKind_IsNotTheResultSetDiscriminator()
    {
        var executor = new StoredProcedureExecutor();

        // Nothing below opens the connection: the ResultColumns check runs before BuildCommand, and
        // CreateCommand is `new SqlCommand(null, this)`, which needs neither a server nor a string.
        using var connection = new SqlConnection();

        // (a) The contract says ResultSet as loudly as it can, and it still cannot produce rows.
        var sayingResultSet = TestContracts.ContractWithReturn(
            "dbo",
            "Ping",
            [],
            new ProcReturnSpec(ReturnKind.ResultSet, new SqlTypeSpec("int")));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => executor.ExecuteReturnResultSetAsync<ProductRow>(SqlConnectionLease.Own(connection), sayingResultSet, []));
        Assert.Contains("Contract has no ResultColumns", ex.Message);
        Assert.Contains("[dbo].[Ping]", ex.Message);

        // (b) And the contract that says ReturnValue builds its command without complaint, result
        // columns and all.
        var sayingReturnValue = TestContracts.GetBySku;
        Assert.Equal(ReturnKind.ReturnValue, sayingReturnValue.Return.Kind);
        Assert.NotNull(sayingReturnValue.ResultColumns);

        using var cmd = StoredProcedureExecutor.BuildCommand(
            SqlConnectionLease.Own(connection), sayingReturnValue, ["ABC"], "[dbo].[Product_GetBySku]");

        Assert.Equal("[dbo].[Product_GetBySku]", cmd.CommandText);
        Assert.Equal(CommandType.StoredProcedure, cmd.CommandType);
        Assert.Equal(2, cmd.Parameters.Count);

        // So result-set-ness is encoded solely by ResultColumns being non-null, and Kind is never read
        // by the executor at all — the generator emits the literal ReturnKind.ReturnValue for every
        // procedure it writes, and Kind's only reader anywhere is the validator's return-type rule.
        // Anyone hand-writing a contract, or writing a second generator, will otherwise set Kind and
        // expect it to mean something.
    }
}
