using Raptor21.EF.Extensions.StoredProcedures.Contracts;
using Raptor21.EF.Extensions.StoredProcedures.Validation;
using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Tests;

/// <summary>
/// Covers <c>StoredProcedureValidator.CompareSqlType</c>: the parameter type-name, MaxLength, Precision
/// and Scale rules and the order they run in, driven with catalog values as plain arguments. The method
/// is pure, so these are the only tests in the validator that need no server; everything around them in
/// <c>ValidateAsync</c> opens a connection before the first rule is reached.
/// </summary>
public class StoredProcedureValidatorCompareTests
{
    /// <summary>The procedure name every case shares; it is the prefix of every message the method throws.</summary>
    private const string FullName = "[dbo].[P]";

    /// <summary>
    /// Calls the method under test with the procedure name filled in, leaving the argument order exactly
    /// as the production signature declares it so a reader can line the two up.
    /// </summary>
    private static void Compare(string paramName, SqlTypeSpec contract, string dbTypeName, int dbMaxLen, byte dbPrec, byte dbScale) =>
        StoredProcedureValidator.CompareSqlType(paramName, contract, dbTypeName, dbMaxLen, dbPrec, dbScale, FullName);

    [Fact]
    public void CompareSqlType_TypeNamesCompareTrimmedAndCaseInsensitively()
    {
        // Both operands are trimmed and compared with OrdinalIgnoreCase, so neither the casing a contract
        // was written in nor stray padding around it can fail a correct procedure. Worth pinning because
        // rule 2 of ValidateOneAsync — the "RETURN must be int" check — compares SqlTypeName without any
        // Trim at all, so the two rules in the same file do not agree about whitespace.
        Assert.Null(Record.Exception(
            () => Compare("@Sku", new SqlTypeSpec("  VARCHAR "), "varchar", 32, 0, 0)));
        Assert.Null(Record.Exception(
            () => Compare("@Sku", new SqlTypeSpec("varchar"), "  VarChar ", 32, 0, 0)));
    }

    [Fact]
    public void CompareSqlType_TypeNameMismatch_ThrowsWithExpectedAndFound()
    {
        // This is where the generator's string -> varchar inference surfaces: a procedure that really
        // declares nvarchar is rejected against a contract nobody wrote by hand, so the message has to
        // carry enough context to point at the parameter rather than just at the procedure.
        var ex = Assert.Throws<InvalidOperationException>(
            () => Compare("@Sku", new SqlTypeSpec("varchar"), "nvarchar", 64, 0, 0));

        Assert.Contains("[dbo].[P]", ex.Message);
        Assert.Contains("@Sku", ex.Message);
        Assert.Contains("Expected varchar, found nvarchar", ex.Message);
    }

    [Fact]
    public void CompareSqlType_InlineLengthSpelling_IsRejected()
    {
        // Pins current behaviour and records a disagreement rather than blessing it: CompareSqlType treats
        // the whole type name as one opaque string, while ApplySqlType matches "varchar(50)" by StartsWith
        // and binds it correctly. Nothing in the library states which spelling is canonical, and this is
        // reachable from an ordinary [Sql("@Sku", "varchar(32)")] attribute — RenderSqlType passes the
        // type name through verbatim — so it is not confined to hand-written contracts.
        var ex = Assert.Throws<InvalidOperationException>(
            () => Compare("@Sku", new SqlTypeSpec("varchar(50)"), "varchar", 50, 0, 0));

        Assert.Contains("Expected varchar(50), found varchar", ex.Message);
    }

    [Fact]
    public void CompareSqlType_MaxLengthIsOptIn()
    {
        // MaxLength is only compared when the contract carries one, so a [Sql("@p")] with no length
        // argument is never length-checked against the catalog at all — which covers most string
        // parameters, since the generator only emits a length when the attribute supplies one.
        Assert.Null(Record.Exception(
            () => Compare("@Sku", new SqlTypeSpec("varchar"), "varchar", 8000, 0, 0)));
    }

    [Fact]
    public void CompareSqlType_MaxLengthMismatch_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => Compare("@Sku", new SqlTypeSpec("varchar", 32), "varchar", 64, 0, 0));

        Assert.Contains("max_length mismatch. Expected 32, found 64", ex.Message);
    }

    [Fact]
    public void CompareSqlType_MaxTypesReportMinusOne()
    {
        // sys.parameters.max_length is -1 for varchar(max), nvarchar(max) and varbinary(max), so a contract
        // that spells a finite length out does not describe a MAX parameter and must be rejected.
        var ex = Assert.Throws<InvalidOperationException>(
            () => Compare("@Blob", new SqlTypeSpec("varchar", 8000), "varchar", -1, 0, 0));
        Assert.Contains("Expected 8000, found -1", ex.Message);

        // -1 is therefore the only way a contract can say MAX, and it is the value that round-trips.
        Assert.Null(Record.Exception(
            () => Compare("@Blob", new SqlTypeSpec("varchar", -1), "varchar", -1, 0, 0)));
    }

    [Fact]
    public void CompareSqlType_NvarcharLengthInCharacters_MatchesTheCatalogByteCount()
    {
        // EXPECTED TO FAIL — confirmed defect: CompareSqlType compares the contract's MaxLength, which is
        // a CHARACTER count everywhere else in the library, against sys.parameters.max_length, which is a
        // BYTE count. 64 is exactly what SQL Server reports for a correctly declared @Sku nvarchar(32),
        // so this correct procedure fails validation at boot with
        // "Parameter @Sku max_length mismatch. Expected 32, found 64."
        // The unit is unambiguous at execution: ApplySqlType assigns the same field to SqlParameter.Size
        // for SqlDbType.NVarChar, where it is characters (pinned by SqlTypeMappingTests), so the two
        // consumers of SqlTypeSpec.MaxLength disagree. The sample escapes it only because every string
        // parameter in its DbScripts/*.sql is varchar, where bytes and characters happen to coincide.
        Assert.Null(Record.Exception(
            () => Compare("@Sku", new SqlTypeSpec("nvarchar", 32), "nvarchar", 64, 0, 0)));
    }

    [Fact]
    public void CompareSqlType_PrecisionAndScaleAreOptIn()
    {
        // This is the validator's half of the recorded "decimal loses scale" gap. RenderSqlType gates the
        // precision/scale arm on `precision > 0`, so [Sql("@Price", "decimal", Scale = 2)] emits a bare
        // SqlTypeSpec("decimal") with both fields null. The contract then carries nothing to compare, the
        // catalog's decimal(18,2) is accepted without inspection, and ApplySqlType goes on to bind the
        // parameter as Precision 18 / Scale 0 — losing the scale the author actually asked for.
        Assert.Null(Record.Exception(
            () => Compare("@Price", new SqlTypeSpec("decimal"), "decimal", 0, 18, 2)));
    }

    [Theory]
    [InlineData(10, 2, "precision mismatch. Expected 18, found 10")]
    [InlineData(18, 0, "scale mismatch. Expected 2, found 0")]
    public void CompareSqlType_PrecisionAndScaleMismatch_Throw(int dbPrecision, int dbScale, string expectedFragment)
    {
        // The shape RenderSqlType emits for [Sql("@Price", Precision = 18, Scale = 2)]: MaxLength forced
        // to null, precision and scale both present, so both dimensions are genuinely compared.
        var contract = new SqlTypeSpec("decimal", null, (byte)18, (byte)2);

        var ex = Assert.Throws<InvalidOperationException>(
            () => Compare("@Price", contract, "decimal", 0, (byte)dbPrecision, (byte)dbScale));

        Assert.Contains(expectedFragment, ex.Message);
    }

    [Fact]
    public void CompareSqlType_CheckOrderIsNameThenLengthThenPrecisionThenScale()
    {
        // Wrong in all four dimensions at once. The type-name check throws first and masks the other
        // three, so a developer fixing a badly declared parameter is told about one problem per boot
        // rather than all of them — the checks are sequential throws, not a collected report.
        var contract = new SqlTypeSpec("nvarchar", 10, (byte)5, (byte)1);

        var ex = Assert.Throws<InvalidOperationException>(
            () => Compare("@Sku", contract, "varchar", 32, 0, 0));

        Assert.Contains("type mismatch", ex.Message);
        Assert.DoesNotContain("max_length", ex.Message);
    }

    [Fact]
    public void CompareSqlType_DefaultSqlTypeSpec_ThrowsNamingTheParameter()
    {
        // EXPECTED TO FAIL — confirmed defect: contract.SqlTypeName is dereferenced with no null guard, so
        // a default SqlTypeSpec produces a bare NullReferenceException carrying neither the procedure nor
        // the parameter name. It is the same unguarded-dereference class as ApplySqlType's ToUpperInvariant
        // call, so validation and execution fail identically rather than disagreeing; the point of the
        // assertion is that whatever is thrown must at least say which parameter caused it.
        var ex = Record.Exception(() => Compare("@Id", default, "int", 4, 10, 0));

        Assert.NotNull(ex);
        Assert.Contains("@Id", ex!.Message);
    }
}
