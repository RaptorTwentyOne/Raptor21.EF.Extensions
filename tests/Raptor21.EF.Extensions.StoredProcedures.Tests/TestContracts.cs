using Raptor21.EF.Extensions.StoredProcedures.Contracts;

namespace Raptor21.EF.Extensions.StoredProcedures.Tests;

/// <summary>
/// Contracts shaped exactly the way <c>StoredProcedureGenerator</c> emits them, so every executor and
/// validator test starts from something the generator could really have written.
/// </summary>
/// <remarks>
/// Three details of the emitted shape are easy to get wrong by hand and are reproduced here deliberately.
/// <c>RenderSqlType</c> writes <c>SqlTypeSpec(name, length)</c> when precision is 0 and length is
/// non-negative, <c>SqlTypeSpec(name, null, (byte)p, (byte)s)</c> when precision is positive — length is
/// forced to null on that path — and a bare <c>SqlTypeSpec(name)</c> otherwise. The return spec is
/// always the literal <c>ProcReturnSpec(ReturnKind.ReturnValue, new SqlTypeSpec("int"))</c>, even for a
/// procedure with a result set. And a zero-parameter method gets an empty array, never null.
/// <para>
/// The named contracts are properties rather than static fields, so each test gets its own instance:
/// <c>GeneratedContract</c> does not copy the array it is handed, and xunit runs test classes in
/// parallel, so a shared instance would be shared mutable state.
/// </para>
/// </remarks>
internal static class TestContracts
{
    /// <summary>
    /// The return spec the generator writes for every procedure, result-set procedures included — which
    /// is why <c>ReturnKind</c> is not the result-set discriminator; a non-null <c>ResultColumns</c> is.
    /// </summary>
    internal static ProcReturnSpec ReturnValueInt32 => new(ReturnKind.ReturnValue, new SqlTypeSpec("int"));

    /// <summary>An ad-hoc contract carrying the generator's fixed return spec; result columns stay null unless supplied.</summary>
    internal static GeneratedContract Contract(
        string schema,
        string name,
        ProcParamSpec[] parameters,
        ColumnSpec[]? resultColumns = null) =>
        new(schema, name, parameters, ReturnValueInt32, resultColumns);

    /// <summary>The same, for the tests that need a return spec the generator would never emit.</summary>
    internal static GeneratedContract ContractWithReturn(
        string schema,
        string name,
        ProcParamSpec[] parameters,
        ProcReturnSpec @return,
        ColumnSpec[]? resultColumns = null) =>
        new(schema, name, parameters, @return, resultColumns);

    /// <summary>
    /// A one-parameter <c>[dbo].[P]</c>, for the binding tests where only the parameter's SQL type
    /// varies and the procedure's own name never appears in the assertion.
    /// </summary>
    internal static GeneratedContract OneParameter(string parameterName, SqlTypeSpec sqlType, bool isOutput = false) =>
        Contract("dbo", "P", [new ProcParamSpec(parameterName, sqlType, isOutput)]);

    /// <summary>Mirrors the sample's <c>TouchStampAsync</c>: one int parameter, no result set.</summary>
    internal static GeneratedContract Touch => Contract(
        "dbo",
        "Product_TouchStamp",
        [new ProcParamSpec("@Id", new SqlTypeSpec("int"))]);

    /// <summary>
    /// Mirrors the sample's <c>UpsertAsync</c>, including the one shape a hand-written contract usually
    /// gets wrong: <c>[Sql("@Price", Precision = 18, Scale = 2)]</c> renders with MaxLength null.
    /// </summary>
    internal static GeneratedContract Upsert => Contract(
        "dbo",
        "Product_Upsert",
        [
            new ProcParamSpec("@Sku", new SqlTypeSpec("varchar", 32)),
            new ProcParamSpec("@Name", new SqlTypeSpec("varchar", 128)),
            new ProcParamSpec("@Price", new SqlTypeSpec("decimal", null, (byte)18, (byte)2)),
            new ProcParamSpec("@Id", new SqlTypeSpec("int"), true),
        ]);

    /// <summary>
    /// Mirrors the sample's <c>GetBySkuAsync</c>. The columns line up with <see cref="ProductRow"/>, and
    /// the return spec is still ReturnValue/int despite the result set.
    /// </summary>
    internal static GeneratedContract GetBySku => Contract(
        "dbo",
        "Product_GetBySku",
        [new ProcParamSpec("@Sku", new SqlTypeSpec("varchar", 32))],
        [
            new ColumnSpec("Id", typeof(int)),
            new ColumnSpec("Sku", typeof(string)),
            new ColumnSpec("Price", typeof(decimal)),
            new ColumnSpec("UpdatedUtc", typeof(DateTime?)),
        ]);

    /// <summary>A procedure with no parameters at all, which still gets a RETURN slot at index 0.</summary>
    internal static GeneratedContract Ping => Contract("dbo", "Ping", []);
}
