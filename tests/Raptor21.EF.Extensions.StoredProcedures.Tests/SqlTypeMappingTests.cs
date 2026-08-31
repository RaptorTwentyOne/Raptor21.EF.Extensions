using System.Data;
using System.Globalization;
using Microsoft.Data.SqlClient;
using Raptor21.EF.Extensions.StoredProcedures.Contracts;
using Raptor21.EF.Extensions.StoredProcedures.Execution;
using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Tests;

/// <summary>
/// Covers <c>StoredProcedureExecutor.ApplySqlType</c>: the complete SQL-type-name to
/// SqlDbType/Size/Precision/Scale decision table that every parameter of every generated call passes
/// through.
/// </summary>
/// <remarks>
/// The subject is pure — a freshly constructed <see cref="SqlParameter"/>, no connection, no I/O — but
/// until the executor's helpers became internal it was reachable only through a live server, so none of
/// these fourteen branches had ever been executed by a test. Several of the tests below assert the
/// CORRECT mapping rather than the current one and therefore fail against today's library; each says so
/// and names the defect. Weakening one of them to green would delete the finding.
/// </remarks>
public class SqlTypeMappingTests
{
    /// <summary>Applies a type spec to a parameter that carries nothing but the mapping under test.</summary>
    private static SqlParameter Map(SqlTypeSpec spec)
    {
        var p = new SqlParameter();
        StoredProcedureExecutor.ApplySqlType(p, spec);
        return p;
    }

    [Theory]
    [InlineData("varchar", 21, SqlDbType.VarChar, 21)]
    [InlineData("varchar", null, SqlDbType.VarChar, 0)]
    [InlineData("nvarchar", 128, SqlDbType.NVarChar, 128)]
    [InlineData("nvarchar", null, SqlDbType.NVarChar, 0)]
    [InlineData("char", 10, SqlDbType.Char, 10)]
    [InlineData("char", null, SqlDbType.Char, 0)]
    [InlineData("nchar", 10, SqlDbType.NChar, 10)]
    [InlineData("nchar", null, SqlDbType.NChar, 0)]
    public void ApplySqlType_StringTypes_SetTypeAndSizeFromMaxLength(
        string typeName,
        int? maxLength,
        SqlDbType expectedDbType,
        int expectedSize)
    {
        var p = Map(new SqlTypeSpec(typeName, maxLength));

        Assert.Equal(expectedDbType, p.SqlDbType);

        // The rows with no MaxLength pin the default every character branch applies: Size 0, which
        // SqlClient reads as "infer from the value". The binary branch defaults to -1 instead, and that
        // asymmetry is what ApplySqlType_BinaryTypes_DefaultSizeToMinusOneNotZero exists to record.
        Assert.Equal(expectedSize, p.Size);
    }

    [Fact]
    public void ApplySqlType_NCharTypeName_ReachesNCharBranchNotCharBranch()
    {
        var p = Map(new SqlTypeSpec("nchar", 10));

        // Correct today, and worth pinning because the guard that appears to produce it does not: the
        // CHAR branch is written `StartsWith("CHAR") && !StartsWith("NCHAR")`, and no string can satisfy
        // both halves of that conjunction, so the negation is dead by construction. "NCHAR" simply never
        // reaches the CHAR branch, and a reader who trusts the guard is trusting the wrong thing.
        Assert.Equal(SqlDbType.NChar, p.SqlDbType);
    }

    [Theory]
    [InlineData("  nVarChar(50) ", SqlDbType.NVarChar)]
    [InlineData("  int  ", SqlDbType.Int)]
    [InlineData("INT", SqlDbType.Int)]
    [InlineData("BiT", SqlDbType.Bit)]
    [InlineData("DeCiMaL", SqlDbType.Decimal)]
    public void ApplySqlType_TypeNameIsTrimmedAndCaseInsensitive(string typeName, SqlDbType expected)
    {
        // The type name reaches ApplySqlType from a hand-typed [Sql(..., "TypeName")] string, so the
        // Trim() and ToUpperInvariant() at the top of the method are load-bearing rather than tidiness.
        Assert.Equal(expected, Map(new SqlTypeSpec(typeName)).SqlDbType);
    }

    [Theory]
    [InlineData("int", SqlDbType.Int)]
    [InlineData("smallint", SqlDbType.SmallInt)]
    [InlineData("bigint", SqlDbType.BigInt)]
    [InlineData("tinyint", SqlDbType.TinyInt)]
    [InlineData("bit", SqlDbType.Bit)]
    public void ApplySqlType_ExactMatchIntegerTypes_MapToTheirSqlDbType(string typeName, SqlDbType expected)
    {
        var p = Map(new SqlTypeSpec(typeName));

        Assert.Equal(expected, p.SqlDbType);

        // These branches assign nothing but the type, so Size stays at the parameter's own default. If a
        // future edit started stamping a Size here it would silently truncate nothing today and
        // something later, so the zero is asserted rather than assumed.
        Assert.Equal(0, p.Size);
    }

    [Theory]
    [InlineData("decimal", null, null, 18, 0)]
    [InlineData("decimal", 18, 2, 18, 2)]
    [InlineData("numeric", 9, 4, 9, 4)]
    [InlineData("numeric", null, null, 18, 0)]
    public void ApplySqlType_Decimal_DefaultsPrecisionTo18AndScaleTo0(
        string typeName,
        int? precision,
        int? scale,
        int expectedPrecision,
        int expectedScale)
    {
        // The generator forces MaxLength to null whenever it renders precision and scale, so a decimal
        // spec really does arrive shaped like this.
        var p = Map(new SqlTypeSpec(typeName, null, (byte?)precision, (byte?)scale));

        Assert.Equal(SqlDbType.Decimal, p.SqlDbType);
        Assert.Equal(expectedPrecision, (int)p.Precision);
        Assert.Equal(expectedScale, (int)p.Scale);

        // Deliberately an assertion about the parameter object only. Whether SqlClient treats a Scale of
        // 0 as authoritative — and therefore truncates 1.5 to 1 for a contract that omitted the scale —
        // is provider behaviour that cannot be established from this repository, so it is not asserted
        // here in either direction.
    }

    [Theory]
    [InlineData("datetime")]
    [InlineData("datetime2")]
    public void ApplySqlType_DateTimeNames_MapToDateTime2(string typeName)
    {
        // Two names, one SqlDbType, and the collapse is harmless: datetime2 is a superset of datetime's
        // range. The names for which the same branch would NOT be harmless get their own tests.
        Assert.Equal(SqlDbType.DateTime2, Map(new SqlTypeSpec(typeName)).SqlDbType);
    }

    [Fact]
    public void ApplySqlType_Date_MapsToSqlDbTypeDate()
    {
        // date used to ride along with datetime2 on the argument that a date is a datetime2 at midnight.
        // True of the value, needless for the binding: date has its own SqlDbType, and sending datetime2
        // instead makes the server convert on every call for nothing.
        Assert.Equal(SqlDbType.Date, Map(new SqlTypeSpec("date")).SqlDbType);
    }

    [Fact]
    public void ApplySqlType_UniqueIdentifier_Maps()
    {
        Assert.Equal(SqlDbType.UniqueIdentifier, Map(new SqlTypeSpec("uniqueidentifier")).SqlDbType);
    }

    [Theory]
    [InlineData("binary", null, -1)]
    [InlineData("varbinary", null, -1)]
    [InlineData("varbinary", 8000, 8000)]
    public void ApplySqlType_BinaryTypes_DefaultSizeToMinusOneNotZero(
        string typeName,
        int? maxLength,
        int expectedSize)
    {
        var p = Map(new SqlTypeSpec(typeName, maxLength));

        // Both names land on VarBinary, so a fixed-length `binary(16)` column is bound as a variable
        // length parameter and the server pads on assignment rather than the client.
        Assert.Equal(SqlDbType.VarBinary, p.SqlDbType);

        // -1 is MAX. This is the only branch that defaults Size to anything but 0, which is the concrete
        // inconsistency behind the character/binary asymmetry: an unsized varchar OUTPUT parameter comes
        // back capped at whatever SqlClient infers, an unsized varbinary one does not.
        Assert.Equal(expectedSize, p.Size);
    }

    [Theory]
    [InlineData("sql_variant")]
    [InlineData("timestamp")]
    [InlineData("geography")]
    [InlineData("dbo.MyTableType")]
    public void ApplySqlType_UnmappedTypeName_ThrowsNamingTheParameterAndTheType(string typeName)
    {
        var p = new SqlParameter { ParameterName = "@Payload" };

        var ex = Assert.Throws<ArgumentException>(
            () => StoredProcedureExecutor.ApplySqlType(p, new SqlTypeSpec(typeName)));

        // Seven names have left this list — money, smallmoney, text, ntext, image, smalldatetime and xml
        // each have an arm of their own now — and what remains is what the library genuinely cannot bind,
        // each name unmapped for its own reason. sql_variant's type is chosen by the value rather than by
        // the contract, which is the one thing a compile-time contract cannot state; a timestamp is a
        // rowversion and can never be assigned, so it is never an input; geography would need a
        // UdtTypeName that SqlTypeSpec has no field to carry; and dbo.MyTableType is a table-valued
        // parameter, which needs SqlDbType.Structured and a row source this library does not model.
        // Throwing is the whole point of the change: binding an unmapped name as a varchar sent the value
        // to the server stringified, and validation cannot catch that, because CompareSqlType checks the
        // contract's type name against the catalog rather than against the executor's table — so a
        // contract naming the parameter's real type passes validation and is then mis-bound.
        Assert.Contains("@Payload", ex.Message);
        Assert.Contains(typeName, ex.Message);
    }

    [Fact]
    public void ApplySqlType_Real_MapsToSqlDbTypeReal()
    {
        // EXPECTED TO FAIL (VarChar today). "real" is not an exotic override: it is exactly what the
        // generator's own InferSqlType returns for System.Single, so this is the default path for a
        // plain `float` parameter with no TypeName. StoredProcedureValidator maps "real" to
        // {float, float?}, which means ValidateAsync green-lights the same contract the executor then
        // mis-binds — the two halves of the library disagree with each other.
        Assert.Equal(SqlDbType.Real, Map(new SqlTypeSpec("real")).SqlDbType);
    }

    [Fact]
    public void ApplySqlType_Float_MapsToSqlDbTypeFloat()
    {
        // EXPECTED TO FAIL (VarChar today), for the same missing branch: InferSqlType returns "float"
        // for System.Double, so every double parameter binds as a string.
        Assert.Equal(SqlDbType.Float, Map(new SqlTypeSpec("float")).SqlDbType);
    }

    [Fact]
    public void ApplySqlType_DateTimeOffset_MapsToSqlDbTypeDateTimeOffset()
    {
        // EXPECTED TO FAIL (DateTime2 today). This is not the varchar fall-through: "DATETIMEOFFSET"
        // starts with "DATETIME", so a wrong branch actively fires and the offset is dropped on the way
        // to the server. Silent data loss rather than a bind error.
        Assert.Equal(SqlDbType.DateTimeOffset, Map(new SqlTypeSpec("datetimeoffset")).SqlDbType);
    }

    [Fact]
    public void ApplySqlType_Time_MapsToSqlDbTypeTime()
    {
        // EXPECTED TO FAIL (DateTime2 today) — and this one is not an accident of StartsWith: the
        // DateTime2 branch explicitly folds in `name == "TIME"`, so a time(7) parameter is bound as a
        // full datetime2 with a date part the procedure never asked for.
        Assert.Equal(SqlDbType.Time, Map(new SqlTypeSpec("time")).SqlDbType);
    }

    [Theory]
    [InlineData("binary(16)", SqlDbType.VarBinary)]
    [InlineData("int(4)", SqlDbType.Int)]
    [InlineData("integer", SqlDbType.Int)]
    public void ApplySqlType_DecoratedExactMatchNames_StillMapToTheirBaseType(string typeName, SqlDbType expected)
    {
        // EXPECTED TO FAIL (VarChar for all three). The table matches some names with StartsWith and
        // others with exact equality, and the choice does not follow the type: bare "binary" and bare
        // "int" pass in the tests above, but the decorated forms of the same names miss their branch and
        // drop into the varchar else with no diagnostic emitted anywhere. [Sql("@Hash", "binary(16)")]
        // is a legal, natural thing to write — the attribute takes the type name as a free-form string.
        Assert.Equal(expected, Map(new SqlTypeSpec(typeName)).SqlDbType);
    }

    [Fact]
    public void ApplySqlType_DefaultSqlTypeSpec_ThrowsNamingTheParameter()
    {
        var p = new SqlParameter { ParameterName = "@Id" };

        // SqlTypeSpec is a readonly record struct, so a default instance has a null SqlTypeName and is
        // reachable without any misuse of the generator: `new ProcParamSpec("@Id", default)` in a
        // hand-written contract, or an array element nobody assigned.
        SqlTypeSpec spec = default;

        var ex = Record.Exception(() => StoredProcedureExecutor.ApplySqlType(p, spec));

        Assert.NotNull(ex);

        // EXPECTED TO FAIL. Today the first line dereferences SqlTypeName with no guard, so this is a
        // NullReferenceException whose message names neither the parameter nor the procedure, leaving a
        // consumer with a stack trace into library internals. The assertion is deliberately only that
        // the parameter is named: which exception type the fix chooses is still open, and pinning one
        // here would make this test an obstacle to the fix rather than a specification of it.
        Assert.Contains("@Id", ex!.Message);
    }

    [Fact]
    public void ApplySqlType_UnderTurkishCulture_StillMatchesInvariantNames()
    {
        // Synchronous on purpose: the culture is restored on the thread it was changed on, with no await
        // in between for the runtime to resume elsewhere.
        var original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");

            Assert.Equal(SqlDbType.Int, Map(new SqlTypeSpec("int")).SqlDbType);
            Assert.Equal(SqlDbType.Bit, Map(new SqlTypeSpec("bit")).SqlDbType);
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }

        // Correct today; the test exists because the failure mode is invisible in review. Turkish
        // uppercases 'i' to 'İ', so a contributor simplifying ToUpperInvariant() to ToUpper() would turn
        // "int" into "İNT", miss every exact-match branch and bind every int parameter as a varchar —
        // on the repository owner's own locale, and nowhere else. The StringComparison.Ordinal on each
        // StartsWith is pinned by the same test.
    }
}
