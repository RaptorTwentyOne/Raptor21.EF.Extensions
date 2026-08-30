using Raptor21.EF.Extensions.StoredProcedures.Validation;
using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Tests;

public class StoredProcedureValidatorTypeTests
{
    // Every message ValidateSqlToDotNetType throws interpolates the qualified name and the column name,
    // so holding them as constants lets each test assert on the rendered text without restating literals.
    private const string FullName = "[dbo].[P]";
    private const string ColumnName = "C";

    /// <summary>Runs the rule and hands back what it threw, or null when it accepted the pairing.</summary>
    private static Exception? ValidateColumn(string systemTypeName, bool isNullable, Type dotNetType) =>
        Record.Exception(
            () => StoredProcedureValidator.ValidateSqlToDotNetType(FullName, ColumnName, systemTypeName, isNullable, dotNetType));

    // One positive per arm of the switch. Paired with the negative theory below, this freezes the matrix
    // in both directions: a new arm cannot silently widen an existing one, and an arm cannot be dropped.
    [Theory]
    [InlineData("tinyint", typeof(byte))]
    [InlineData("tinyint", typeof(byte?))]
    [InlineData("smallint", typeof(short))]
    [InlineData("int", typeof(int))]
    [InlineData("int", typeof(int?))]
    [InlineData("bigint", typeof(long))]
    [InlineData("bit", typeof(bool))]
    [InlineData("varchar", typeof(string))]
    [InlineData("nvarchar", typeof(string))]
    [InlineData("char", typeof(string))]
    [InlineData("nchar", typeof(string))]
    [InlineData("text", typeof(string))]
    [InlineData("ntext", typeof(string))]
    [InlineData("binary", typeof(byte[]))]
    [InlineData("varbinary", typeof(byte[]))]
    [InlineData("image", typeof(byte[]))]
    [InlineData("datetime", typeof(DateTime))]
    [InlineData("datetime2", typeof(DateTime?))]
    [InlineData("smalldatetime", typeof(DateTime))]
    [InlineData("uniqueidentifier", typeof(Guid))]
    [InlineData("decimal", typeof(decimal))]
    [InlineData("numeric", typeof(decimal))]
    [InlineData("real", typeof(float))]
    [InlineData("float", typeof(double))]
    [InlineData("money", typeof(decimal))]
    [InlineData("smallmoney", typeof(decimal))]
    public void GetAllowedDotNetTypesForSqlType_MapsEveryDeclaredArm(string sqlType, Type dotNetType)
    {
        var allowed = StoredProcedureValidator.GetAllowedDotNetTypesForSqlType(sqlType);

        Assert.Contains(dotNetType, allowed);
    }

    // The neighbour of each arm is the mistake a developer actually makes: real/float and float/double
    // are the pair most easily transposed, because T-SQL `real` is 4-byte and T-SQL `float` is 8-byte,
    // the opposite way round from how the CLR names read.
    [Theory]
    [InlineData("int", typeof(long))]
    [InlineData("bigint", typeof(int))]
    [InlineData("real", typeof(double))]
    [InlineData("float", typeof(float))]
    [InlineData("decimal", typeof(double))]
    [InlineData("varchar", typeof(char))]
    [InlineData("binary", typeof(string))]
    [InlineData("uniqueidentifier", typeof(string))]
    [InlineData("bit", typeof(int))]
    public void GetAllowedDotNetTypesForSqlType_RejectsTheNeighbouringClrType(string sqlType, Type dotNetType)
    {
        var allowed = StoredProcedureValidator.GetAllowedDotNetTypesForSqlType(sqlType);

        Assert.DoesNotContain(dotNetType, allowed);
    }

    // Every value-type arm lists both the plain and the nullable form; these two list one entry each, and
    // they could not do otherwise — typeof(string?) IS typeof(string) at runtime. This is the reflection
    // limit the validator documents above the nullability block, and the reason that block is entered
    // only for value types: enforcing nullability here would reject every NOT NULL string column.
    [Theory]
    [InlineData("varchar", typeof(string))]
    [InlineData("varbinary", typeof(byte[]))]
    public void GetAllowedDotNetTypesForSqlType_StringAndByteArrayArmsDoNotListNullableForms(string sqlType, Type expected)
    {
        var allowed = StoredProcedureValidator.GetAllowedDotNetTypesForSqlType(sqlType);

        var only = Assert.Single(allowed);
        Assert.Equal(expected, only);
    }

    // The default arm. An empty set is not "no opinion": the caller turns it into "unsupported SQL type",
    // so this list is also the list of SQL types a contract can never name. rowversion and timestamp are
    // the same type under two spellings, and a table-valued parameter arrives fully qualified.
    [Theory]
    [InlineData("xml")]
    [InlineData("sql_variant")]
    [InlineData("timestamp")]
    [InlineData("rowversion")]
    [InlineData("hierarchyid")]
    [InlineData("geography")]
    [InlineData("geometry")]
    [InlineData("dbo.MyTableType")]
    public void GetAllowedDotNetTypesForSqlType_UnknownType_ReturnsEmptySet(string sqlType)
    {
        var allowed = StoredProcedureValidator.GetAllowedDotNetTypesForSqlType(sqlType);

        Assert.Empty(allowed);
    }

    // sys.dm_exec_describe_first_result_set_for_object reports a column type fully spelled out, so the
    // result-set rule normalises with Split('(')[0].Trim().ToLowerInvariant() before looking the arm up.
    // CompareSqlType, on the parameter path, does no such stripping: it compares the contract's type name
    // to sys.parameters.TYPE_NAME() verbatim. So "varchar(32)" is a legal spelling for a result column
    // and an immediate mismatch for a parameter — the same library, two different normalisation rules.
    [Theory]
    [InlineData("decimal(18,2)", false, typeof(decimal))]
    [InlineData("nvarchar(max)", false, typeof(string))]
    [InlineData("varchar(32)", false, typeof(string))]
    [InlineData(" INT ", false, typeof(int))]
    public void ValidateSqlToDotNetType_StripsTheLengthOrPrecisionSuffix(string systemTypeName, bool isNullable, Type dotNetType)
    {
        Assert.Null(ValidateColumn(systemTypeName, isNullable, dotNetType));
    }

    [Fact]
    public void ValidateSqlToDotNetType_SqlNullableAgainstNonNullableValueType_Throws()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => StoredProcedureValidator.ValidateSqlToDotNetType(FullName, ColumnName, "int", true, typeof(int)));

        // This is the direction that matters at runtime: a NULL arriving in an unguarded GetInt32 call
        // would surface as an InvalidCastException from deep inside generated code, so the message has to
        // carry enough to find the procedure and the column without a debugger.
        Assert.Contains("is NULL in SQL", ex.Message);
        Assert.Contains("non-nullable", ex.Message);
        Assert.Contains("[dbo].[P]", ex.Message);
    }

    // EXPECTED TO FAIL — proves the defect: nullable value types are rendered through Type.Name, which is
    // "Nullable`1" for every one of them, so the message never names the underlying type. A developer told
    // that column C "is NOT NULL in SQL but contract type Nullable`1 is nullable" learns nothing they did
    // not already know. The throw itself is defensible mirroring and is not what this test disputes; the
    // assertion is only that the message names Int32, which leaves any fix free to choose its rendering.
    [Fact]
    public void ValidateSqlToDotNetType_SqlNotNullAgainstNullableValueType_ThrowsNamingTheUnderlyingType()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => StoredProcedureValidator.ValidateSqlToDotNetType(FullName, ColumnName, "int", false, typeof(int?)));

        Assert.Contains("Int32", ex.Message);
    }

    // The nullability block is skipped wholesale for reference types, in both directions, and that is a
    // decision rather than an oversight: reflection cannot tell string from string?, so the alternative is
    // to reject every NOT NULL string column outright. All four combinations pass silently.
    [Theory]
    [InlineData("varchar(32)", true, typeof(string))]
    [InlineData("varchar(32)", false, typeof(string))]
    [InlineData("varbinary(50)", false, typeof(byte[]))]
    [InlineData("varbinary(50)", true, typeof(byte[]))]
    public void ValidateSqlToDotNetType_ReferenceTypes_AreExemptInBothDirections(string systemTypeName, bool isNullable, Type dotNetType)
    {
        Assert.Null(ValidateColumn(systemTypeName, isNullable, dotNetType));
    }

    [Fact]
    public void ValidateSqlToDotNetType_NullabilityIsCheckedBeforeCompatibility()
    {
        // A nullable int column declared as a non-nullable long is wrong twice over. Only the first
        // complaint is reported, which is the ordering a caller depends on: one problem per fix cycle,
        // and the nullability one is the cheaper of the two to act on.
        var ex = Assert.Throws<InvalidOperationException>(
            () => StoredProcedureValidator.ValidateSqlToDotNetType(FullName, ColumnName, "int", true, typeof(long)));

        Assert.Contains("is NULL in SQL", ex.Message);
        Assert.DoesNotContain("not compatible", ex.Message);
    }

    [Fact]
    public void ValidateSqlToDotNetType_IncompatibleType_ListsTheAllowedTypesByMembership()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => StoredProcedureValidator.ValidateSqlToDotNetType(FullName, ColumnName, "int", false, typeof(long)));

        // Asserted by membership, never as the literal "Allowed: Int32, Nullable`1." — that ordering falls
        // out of enumerating a freshly built HashSet<Type>, which no documented rule fixes, so pinning the
        // whole string would be correct today and fragile for the life of the file.
        Assert.Contains("not compatible", ex.Message);
        Assert.Contains("Int64", ex.Message);
        Assert.Contains("Int32", ex.Message);
    }

    [Fact]
    public void ValidateSqlToDotNetType_UnsupportedType_MessageContainsTheUnstrippedTypeName()
    {
        // A typed XML column, which the DMV spells out in full. It stands in for datetimeoffset(7), which
        // this case carried until datetimeoffset gained an arm of its own: the two were then the same call
        // with opposite expectations, and only a type with no arm can reach this message at all.
        var ex = Assert.Throws<InvalidOperationException>(
            () => StoredProcedureValidator.ValidateSqlToDotNetType(FullName, ColumnName, "xml(CONTENT dbo.Books)", false, typeof(string)));

        // Worth noticing rather than admiring: the lookup that failed used the stripped name "xml", but
        // the message interpolates the raw systemTypeName, so the two halves of one error report different
        // strings. Anyone grepping the source for the type in the message finds nothing, because no arm is
        // spelled with a suffix.
        Assert.Contains("xml(CONTENT dbo.Books)", ex.Message);
    }

    // datetimeoffset is the one SQL type that genuinely corresponds to DateTimeOffset, and it used to be
    // the one with no arm at all - it fell through to the empty set and was reported as unsupported while
    // date, time, datetime, datetime2 and smalldatetime all accepted DateTimeOffset. The next test pins
    // the other half of that inversion, now also closed.
    [Fact]
    public void ValidateSqlToDotNetType_DateTimeOffsetColumn_AcceptsDateTimeOffset()
    {
        Assert.Null(ValidateColumn("datetimeoffset(7)", false, typeof(DateTimeOffset)));
    }

    [Fact]
    public void ValidateSqlToDotNetType_DateColumn_RejectsDateTimeOffset()
    {
        // The other half of the inversion, now closed. date, datetime, datetime2 and smalldatetime store
        // no offset, so accepting an offset-carrying contract type told a developer their column round-
        // trips a zone it never held. Only a hand-written contract can reach this at all: the generator
        // has no DateTimeOffset reader, so a generated ColumnSpec never carries typeof(DateTimeOffset).
        var ex = ValidateColumn("date", false, typeof(DateTimeOffset));

        Assert.NotNull(ex);
        Assert.Contains("is not compatible", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Allowed: DateTime", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ValidateSqlToDotNetType_TimeColumn_RejectsDateTimeAndAllowsTimeSpan()
    {
        // A time column used to validate against DateTime, which is a green light the runtime cannot
        // honour: SqlDataReader surfaces time as TimeSpan, so the generated record.GetDateTime(...) throws
        // InvalidCastException on the first row. The validator now says so at boot instead, naming the type
        // that would work. TimeOnly is deliberately not allowed - nothing emits a conversion to it, so
        // admitting it would recreate exactly this defect one type over.
        var rejected = ValidateColumn("time(7)", false, typeof(DateTime));

        Assert.NotNull(rejected);
        Assert.Contains("TimeSpan", rejected.Message, StringComparison.Ordinal);

        Assert.Null(ValidateColumn("time(7)", false, typeof(TimeSpan)));
    }

    // The two reference-type rows document a branch that is dead at the only call site: ValidateSqlToDotNetType
    // consults IsNullableType from inside `if (dotNetType.IsValueType)`, so the `!t.IsValueType` early return
    // can never fire in production. It is pinned anyway, because the answer it gives — "yes, nullable" — is
    // the reason the nullability check has to be gated on value types in the first place.
    [Theory]
    [InlineData(typeof(string), true)]
    [InlineData(typeof(byte[]), true)]
    [InlineData(typeof(int), false)]
    [InlineData(typeof(int?), true)]
    [InlineData(typeof(DateTime), false)]
    [InlineData(typeof(DateTime?), true)]
    public void IsNullableType_ReportsEveryReferenceTypeAsNullable(Type type, bool expected)
    {
        Assert.Equal(expected, StoredProcedureValidator.IsNullableType(type));
    }
}
