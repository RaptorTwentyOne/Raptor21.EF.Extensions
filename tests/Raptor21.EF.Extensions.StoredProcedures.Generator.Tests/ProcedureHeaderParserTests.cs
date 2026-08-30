using Raptor21.EF.Extensions.StoredProcedures.Generator;
using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator.Tests;

/// <summary>
/// Unit tests for the header-only T-SQL parser behind the inferred parameter facts.
/// </summary>
/// <remarks>
/// <para>
/// No Roslyn, no driver, no compilation: <c>Parse</c> is a pure function from a string to a list of
/// facts, and testing it as one is the reason it was written that way. Every assertion here is about
/// what the file says, never about what the generator decides to do with it - that half lives in
/// <c>SqlFactInferenceTests</c>.
/// </para>
/// <para>
/// The contract these tests defend is narrower than "parses T-SQL": <c>Parse</c> never throws for
/// anything in the file, and an unreadable construct abandons only the header it is inside. That is
/// what lets a consumer promote a corpus of legacy scripts to <c>AdditionalFiles</c> without a wall of
/// warnings, so the negative cases below matter at least as much as the positive ones.
/// </para>
/// </remarks>
public class ProcedureHeaderParserTests
{
    private const string Path = "/x/a.sql";

    private static List<SqlProcHeader> Parse(string sql) =>
        ProcedureHeaderParser.Parse(Path, sql, CancellationToken.None);

    private static SqlProcHeader Single(string sql)
    {
        var headers = Parse(sql);
        Assert.Single(headers);
        return headers[0];
    }

    private static SqlHeaderParam[] Params(string sql) => Single(sql).Parameters.AsArray();

    [Fact]
    public void ParsesTheSampleUpsertHeader()
    {
        // The fact set the whole feature rests on: this is the procedure the brief's "after" example
        // declares with no [Sql] at all, and these six facts are what replace the attributes.
        var header = Single("""
            CREATE OR ALTER PROCEDURE dbo.Product_Upsert
                @Sku   varchar(32),
                @Name  varchar(128),
                @Price decimal(18,2),
                @Id    int OUTPUT
            AS
            BEGIN
                SET NOCOUNT ON;
            END
            """);

        Assert.Equal("dbo", header.Schema);
        Assert.Equal("Product_Upsert", header.Name);
        Assert.Equal(Path, header.FilePath);

        var p = header.Parameters.AsArray();
        Assert.Equal(4, p.Length);

        Assert.Equal("@Sku", p[0].Name);
        Assert.Equal("varchar", p[0].TypeName);
        Assert.Equal<int?>(32, p[0].Length);
        Assert.Null(p[0].Precision);
        Assert.Null(p[0].Scale);
        Assert.False(p[0].IsOutput);
        Assert.False(p[0].HasNoLengthArgument);
        Assert.Equal(SqlParamShape.Ordinary, p[0].Shape);

        Assert.Equal("@Name", p[1].Name);
        Assert.Equal("varchar", p[1].TypeName);
        Assert.Equal<int?>(128, p[1].Length);

        // decimal(18,2) is precision and scale, never length. Routing it to Length would send it down
        // RenderSqlType's other branch and change the emitted text.
        Assert.Equal("@Price", p[2].Name);
        Assert.Equal("decimal", p[2].TypeName);
        Assert.Null(p[2].Length);
        Assert.Equal<byte?>(18, p[2].Precision);
        Assert.Equal<byte?>(2, p[2].Scale);

        Assert.Equal("@Id", p[3].Name);
        Assert.Equal("int", p[3].TypeName);
        Assert.Null(p[3].Length);
        Assert.Null(p[3].Precision);
        Assert.Null(p[3].Scale);
        Assert.True(p[3].IsOutput);
    }

    [Fact]
    public void DefaultsSchemaToDbo()
    {
        var header = Single("CREATE PROCEDURE X @A int AS SELECT 1");

        Assert.Equal("dbo", header.Schema);
        Assert.Equal("X", header.Name);
    }

    [Theory]
    [InlineData("CREATE PROCEDURE dbo.A @x int AS SELECT 1")]
    [InlineData("ALTER PROCEDURE dbo.A @x int AS SELECT 1")]
    [InlineData("CREATE PROC dbo.A @x int AS SELECT 1")]
    [InlineData("ALTER PROC dbo.A @x int AS SELECT 1")]
    [InlineData("CREATE OR ALTER PROCEDURE dbo.A @x int AS SELECT 1")]
    [InlineData("CREATE OR ALTER PROC dbo.A @x int AS SELECT 1")]
    [InlineData("create or alter procedure dbo.A @x int as select 1")]
    public void AcceptsPlainCreatePlainAlterAndProcAbbreviation(string sql)
    {
        // "CREATE OR ALTER only" is a Migrations *deployment* policy and stays in
        // StoredProcedureScript.ParseQualifiedName. Enforcing it here would make the ~65-script legacy
        // corpus - which is full of plain CREATE and plain ALTER - contribute nothing at all, which is
        // the opposite of what this feature is for.
        Assert.Single(Parse(sql));
    }

    [Theory]
    [InlineData("CREATE TABLE dbo.A (x int)")]
    [InlineData("ALTER TABLE dbo.A ADD B int")]
    [InlineData("CREATE FUNCTION dbo.f (@x int) RETURNS int AS BEGIN RETURN 1 END")]
    [InlineData("CREATE VIEW dbo.v AS SELECT 1 AS x")]
    public void IgnoresCreateAndAlterOfAnythingButAProcedure(string sql)
    {
        Assert.Empty(Parse(sql));
    }

    [Fact]
    public void IgnoresProcedureNameInsideLineComment()
    {
        // The Migrations regex returns dbo.Commented here, which is how a model annotation can be
        // written under a procedure name that does not exist. This parser is comment-aware.
        var header = Single(
            "-- CREATE OR ALTER PROCEDURE dbo.Commented\n"
            + "CREATE OR ALTER PROCEDURE dbo.Real @x int AS SELECT 1");

        Assert.Equal("Real", header.Name);
    }

    [Fact]
    public void IgnoresProcedureNameInsideNestedBlockComment()
    {
        // Depth counting, not first-*/ matching: nested block comments are legal T-SQL, and Microsoft's
        // own documentation example nests them.
        var header = Single(
            "/* outer /* CREATE PROC dbo.InBlock */ still comment */\n"
            + "CREATE PROC dbo.Real @x int AS SELECT 1");

        Assert.Equal("Real", header.Name);

        // An unterminated nested comment swallows the rest of the file rather than being "closed" early.
        Assert.Empty(Parse("/* /* CREATE PROC dbo.A @x int AS SELECT 1"));
    }

    [Fact]
    public void IgnoresProcedureNameInsideStringLiteral()
    {
        Assert.Empty(Parse("EXEC('CREATE PROCEDURE dbo.InLiteral @x int AS SELECT 1')"));
        Assert.Empty(Parse("EXEC(N'CREATE PROC dbo.A @x int AS SELECT ''a'' ')"));

        var header = Single(
            "EXEC('CREATE PROCEDURE dbo.InLiteral');\n"
            + "CREATE PROC dbo.Real @x int AS SELECT 1");

        Assert.Equal("Real", header.Name);
    }

    [Fact]
    public void UnbracketsDelimitedNamesAndHonoursEscapes()
    {
        // The Migrations regex truncates these to "My" and "Odd".
        var spaced = Single("CREATE PROC [dbo].[My Proc] @x int AS SELECT 1");
        Assert.Equal("dbo", spaced.Schema);
        Assert.Equal("My Proc", spaced.Name);

        var escaped = Single("CREATE PROC [dbo].[Odd]]Name] @x int AS SELECT 1");
        Assert.Equal("dbo", escaped.Schema);
        Assert.Equal("Odd]Name", escaped.Name);
    }

    [Fact]
    public void AcceptsDoubleQuotedNamesWithEscapes()
    {
        // The Migrations regex rejects this shape outright, and SqlBatch's scanner does not track double
        // quotes at all - a documented gap, and a concrete reason this parser is not grafted onto it.
        var quoted = Single("CREATE PROC \"dbo\".\"Quoted\" @x int AS SELECT 1");
        Assert.Equal("dbo", quoted.Schema);
        Assert.Equal("Quoted", quoted.Name);

        Assert.Equal("Odd\"Name", Single("CREATE PROC \"dbo\".\"Odd\"\"Name\" @x int AS SELECT 1").Name);
    }

    [Fact]
    public void ParsesParenthesisedParameterList()
    {
        var p = Params("CREATE PROCEDURE dbo.X (@A int, @B varchar(10)) AS SELECT 1");

        Assert.Equal(2, p.Length);
        Assert.Equal("@A", p[0].Name);
        Assert.Equal("int", p[0].TypeName);
        Assert.Equal("@B", p[1].Name);
        Assert.Equal<int?>(10, p[1].Length);

        Assert.Empty(Params("CREATE PROCEDURE dbo.X () AS SELECT 1"));
    }

    [Fact]
    public void RecordsDefaultsWithoutEvaluatingThem()
    {
        // A default is skipped by balancing parentheses while still honouring literals and comments, so
        // the ',' inside dbo.f() and the ''' inside 'x''y' cannot swallow the next parameter.
        var p = Params("CREATE PROC dbo.X @A int = 5, @B varchar(10) = 'x''y', @C int = (1+2) AS SELECT 1");

        Assert.Equal(3, p.Length);
        Assert.All(p, x => Assert.True(x.HasDefault));
        Assert.Equal("int", p[0].TypeName);
        Assert.Equal("varchar", p[1].TypeName);
        Assert.Equal<int?>(10, p[1].Length);
        Assert.Equal("int", p[2].TypeName);

        var withOutput = Params("CREATE PROC dbo.X @A int = NULL OUTPUT, @B int = -1 AS SELECT 1");
        Assert.Equal(2, withOutput.Length);
        Assert.True(withOutput[0].HasDefault);
        Assert.True(withOutput[0].IsOutput);
        Assert.True(withOutput[1].HasDefault);

        Assert.Equal(2, Params("CREATE PROC dbo.X (@A int = 5, @B int = 6) AS SELECT 1").Length);
        Assert.Equal(2, Params("CREATE PROC dbo.X @A datetime = dbo.f(), @B int AS SELECT 1").Length);
    }

    [Fact]
    public void TreatsOutOutputAndLowercaseAlike()
    {
        var p = Params("CREATE PROC dbo.X @A int OUT, @B int OUTPUT, @C int out, @D int AS SELECT 1");

        Assert.Equal(4, p.Length);
        Assert.True(p[0].IsOutput);
        Assert.True(p[1].IsOutput);
        Assert.True(p[2].IsOutput);
        Assert.False(p[3].IsOutput);
    }

    [Fact]
    public void StopsAtWithOptionsAndForReplication()
    {
        Assert.Single(Params("CREATE PROC dbo.X @A int WITH RECOMPILE AS SELECT 1"));

        // The whole WITH clause is consumed, so the AS in EXECUTE AS OWNER is never mistaken for the
        // body's AS - which would end the header a keyword too early and lose the parameter list.
        Assert.Single(Params("CREATE PROC dbo.X @A int WITH ENCRYPTION, EXECUTE AS OWNER AS BEGIN SELECT 1 END"));
        Assert.Single(Params("CREATE PROC dbo.X @A int WITH EXECUTE AS 'someuser' AS SELECT 1"));
        Assert.Single(Params("CREATE PROC dbo.X @A int FOR REPLICATION AS SELECT 1"));
        Assert.Single(Params("CREATE PROC dbo.X (@A int) WITH RECOMPILE FOR REPLICATION AS SELECT 1"));

        Assert.Single(Params(
            "CREATE PROC dbo.X @A int WITH NATIVE_COMPILATION, SCHEMABINDING, EXECUTE AS OWNER "
            + "AS BEGIN ATOMIC WITH (TRANSACTION ISOLATION LEVEL = SNAPSHOT, LANGUAGE = N'us_english') SELECT 1 END"));
    }

    [Fact]
    public void ParsesMaxAsMinusOne()
    {
        var p = Params("CREATE PROC dbo.X @A nvarchar(max), @B varbinary(MAX), @C varchar(Max) AS SELECT 1");

        Assert.Equal(3, p.Length);
        Assert.Equal("nvarchar", p[0].TypeName);
        Assert.Equal<int?>(-1, p[0].Length);
        Assert.Equal<int?>(-1, p[1].Length);
        Assert.Equal<int?>(-1, p[2].Length);
    }

    [Fact]
    public void InfersNoLengthForBareCharAndBinaryTypes()
    {
        // SQL Server really does read a bare varchar as varchar(1), and that is exactly why the faithful
        // reading must not be taken: recording 1 would set SqlParameter.Size = 1, truncate every value to
        // one character at the server, and still pass startup validation, because sys.parameters
        // .max_length really is 1. Nothing is recorded; HasNoLengthArgument drives the warning instead.
        var p = Params("CREATE PROC dbo.X @A varchar, @B nchar, @C varbinary, @D int AS SELECT 1");

        Assert.Equal(4, p.Length);
        for (var i = 0; i < 3; i++)
        {
            Assert.Null(p[i].Length);
            Assert.True(p[i].HasNoLengthArgument, $"p[{i}] should report a missing length argument");
        }

        Assert.False(p[3].HasNoLengthArgument);
    }

    [Fact]
    public void RoutesTypeArgumentsByFamily()
    {
        // Load-bearing for byte-identity. RenderSqlType's precedence is hard - precision beats length,
        // and the two are never emitted together - so the parser has to put each argument in the right
        // slot; the emitter cannot fix it up afterwards.
        var p = Params(
            "CREATE PROC dbo.X @A decimal(18,2), @B numeric(9), @C varchar(32), @D float(24), "
            + "@E datetime2(7), @F time(3), @G datetimeoffset(7) AS SELECT 1");

        Assert.Equal(7, p.Length);

        Assert.Equal<byte?>(18, p[0].Precision);
        Assert.Equal<byte?>(2, p[0].Scale);
        Assert.Null(p[0].Length);

        Assert.Equal<byte?>(9, p[1].Precision);
        Assert.Null(p[1].Scale);
        Assert.Null(p[1].Length);

        Assert.Equal<int?>(32, p[2].Length);
        Assert.Null(p[2].Precision);

        // Fractional-seconds and approximate types parse their argument and record nothing: RenderSqlType
        // has no scale-without-precision branch, and ApplySqlType ignores it for these types anyway.
        for (var i = 3; i < 7; i++)
        {
            Assert.Null(p[i].Length);
            Assert.Null(p[i].Precision);
            Assert.Null(p[i].Scale);
        }

        Assert.Equal("float", p[3].TypeName);
        Assert.Equal("datetime2", p[4].TypeName);
        Assert.Equal("time", p[5].TypeName);
        Assert.Equal("datetimeoffset", p[6].TypeName);
    }

    [Fact]
    public void NormalisesIntegerToIntAndDecToDecimal()
    {
        // Only these two synonyms normalise, and "integer" is the one that matters: ApplySqlType accepts
        // it, but CompareSqlType string-compares the contract's type name against TYPE_NAME(), which
        // returns "int" - so emitting "integer" would fail startup validation against a correct database.
        var p = Params("CREATE PROC dbo.X @A integer, @B dec(9,2), @C VARCHAR(8), @D [int] AS SELECT 1");

        Assert.Equal(4, p.Length);
        Assert.Equal("int", p[0].TypeName);
        Assert.Equal("decimal", p[1].TypeName);
        Assert.Equal<byte?>(9, p[1].Precision);
        Assert.Equal<byte?>(2, p[1].Scale);
        Assert.Equal("varchar", p[2].TypeName);
        Assert.Equal("int", p[3].TypeName);
    }

    [Fact]
    public void RefusesTableValuedCursorUserDefinedAndSysname()
    {
        // Refusing a parameter is not refusing the header: the others keep every fact. A refused
        // parameter carries an empty TypeName, which the resolver reads as "the file said nothing"
        // rather than "the file said this" - so nothing unbindable can reach the emitted contract.
        var p = Params(
            "CREATE PROC dbo.X @t dbo.MyType READONLY, @c CURSOR VARYING OUTPUT, @u [dbo].[MyType], "
            + "@s sysname, @ok int AS SELECT 1");

        Assert.Equal(5, p.Length);

        Assert.Equal(SqlParamShape.TableValued, p[0].Shape);
        Assert.Equal("", p[0].TypeName);

        Assert.Equal(SqlParamShape.Cursor, p[1].Shape);
        Assert.Equal("", p[1].TypeName);
        Assert.True(p[1].IsOutput);

        Assert.Equal(SqlParamShape.UserDefined, p[2].Shape);
        Assert.Equal("", p[2].TypeName);

        // sysname is nvarchar(128) under another name, and normalising it here would need matching
        // additions to ApplySqlType and GetAllowedDotNetTypesForSqlType in the same change - otherwise it
        // becomes a third table that disagrees with the other two. Refusing costs nothing today, because
        // sysname already throws at bind time.
        Assert.Equal(SqlParamShape.Ordinary, p[3].Shape);
        Assert.Equal("", p[3].TypeName);
        Assert.Null(p[3].Length);
        Assert.False(p[3].HasNoLengthArgument);

        Assert.Equal("int", p[4].TypeName);
    }

    [Fact]
    public void RefusesTypesWithNoPlaceInTheContract()
    {
        var p = Params(
            "CREATE PROC dbo.X @a timestamp, @b rowversion, @c sql_variant, @d geography, "
            + "@e geometry, @f hierarchyid AS SELECT 1");

        Assert.Equal(6, p.Length);
        Assert.All(p, x => Assert.Equal("", x.TypeName));
    }

    [Theory]
    [InlineData("CREATE PROC dbo.X;2 @a int AS SELECT 1")]
    [InlineData("CREATE PROC #tmp @a int AS SELECT 1")]
    [InlineData("CREATE PROC ##tmp @a int AS SELECT 1")]
    [InlineData("CREATE PROC [#tmp] @a int AS SELECT 1")]
    public void RefusesGroupedAndTemporaryProcedures(string sql)
    {
        // The executor's CommandText is [schema].[name] and can address neither a grouped nor a temporary
        // procedure, so supplying facts for one would only produce a contract that cannot be called.
        Assert.Empty(Parse(sql));
    }

    [Fact]
    public void ReturnsEveryProcedureInAMultiProcedureFile()
    {
        // No batch splitting is involved, which is the concrete answer to "does this need SqlBatch?".
        // CREATE is only recognised at statement level, so a GO inside a literal or a comment cannot
        // mislead the scan, and a GO between two procedures simply produces two headers.
        var headers = Parse("""
            CREATE OR ALTER PROC dbo.A @x int AS
            BEGIN SELECT 1 END
            GO
            CREATE OR ALTER PROC dbo.B @y varchar(8) AS
            BEGIN SELECT 2 END
            GO
            """);

        Assert.Equal(2, headers.Count);
        Assert.Equal("A", headers[0].Name);
        Assert.Equal("B", headers[1].Name);
        Assert.Equal<int?>(8, headers[1].Parameters.AsArray()[0].Length);
    }

    [Theory]
    [InlineData("INSERT INTO dbo.T (A) VALUES (1)\nGO\nINSERT INTO dbo.T (A) VALUES (2)\nGO\n", "a legacy data script")]
    [InlineData("/* unterminated\nCREATE PROC dbo.A @x int AS SELECT 1", "an unterminated block comment")]
    [InlineData("CREATE PROCEDURE dbo.Truncated @a int", "a truncated header")]
    [InlineData("", "an empty file")]
    [InlineData("   \n\t ", "a whitespace-only file")]
    [InlineData("CREATE", "a bare CREATE")]
    [InlineData("CREATE PROCEDURE", "a header with no name")]
    [InlineData("CREATE PROC dbo.X @a varchar(zz) AS SELECT 1", "an unreadable type argument")]
    [InlineData("CREATE PROC dbo.X @a varchar(1234567890123) AS SELECT 1", "an absurd length")]
    public void ReturnsNothingForNonProcedureAndMalformedScripts(string sql, string what)
    {
        // The contract that keeps the legacy corpus silent: no throw, no diagnostic, no facts. Everything
        // here is something a real .sql in a real repository contains.
        Assert.True(Parse(sql).Count == 0, $"{what} must contribute no headers");
    }

    [Fact]
    public void AbandonsAMalformedHeaderAndResumesAtTheNextCreateOrAlter()
    {
        // One bad header never costs the others.
        var recovered = Single("CREATE PROCEDURE dbo.Truncated @a int\nCREATE PROC dbo.Good @b int AS SELECT 1");
        Assert.Equal("Good", recovered.Name);

        // The same recovery explains these two, which look surprising written down: "ALTER OR" is not a
        // legal opening, so the header is abandoned at "OR" - and scanning then resumes and finds the
        // perfectly valid "CREATE PROC dbo.A @x int AS" (respectively "ALTER PROC ...") that follows it.
        // One header each, and it is the real one.
        Assert.Single(Parse("ALTER OR CREATE PROC dbo.A @x int AS SELECT 1"));
        Assert.Single(Parse("ALTER OR ALTER PROC dbo.A @x int AS SELECT 1"));

        // With nothing valid left after the bad keyword there is nothing to recover to.
        Assert.Empty(Parse("ALTER OR PROC dbo.A @x int AS SELECT 1"));
    }

    [Fact]
    public void ParsesTheHeaderEvenWhenTheBodyIsUnparseable()
    {
        // This is the property that rules out a statement-level parser such as ScriptDom: it returns zero
        // statements for each of these and destroys a perfectly good header - on exactly the corpus that
        // most needs the facts. Stopping at the body's AS means the body's age is irrelevant.
        var header = Single("""
            CREATE PROCEDURE dbo.Legacy
                @Id int,
                @Name varchar(21)
            AS
            SELECT * FORM dbo.A a, dbo.B b WHERE a.Id *= b.Id
            RAISERROR 50001 'boom'
            """);

        Assert.Equal("Legacy", header.Name);

        var p = header.Parameters.AsArray();
        Assert.Equal(2, p.Length);
        Assert.Equal<int?>(21, p[1].Length);
    }

    [Fact]
    public void ParsesAProcedureWithNoParametersAtAll()
    {
        var header = Single("CREATE PROCEDURE dbo.NoArgs\nAS\nBEGIN SELECT 1 END");

        Assert.Equal("NoArgs", header.Name);
        Assert.Empty(header.Parameters.AsArray());
    }

    [Fact]
    public void RecordsSpansForTheHeaderAndEachParameter()
    {
        // SPG012 is reported inside the .sql, which the resolver can only do because these four integers
        // survive the pipeline: a Roslyn Location compares by reference and would defeat the cache.
        const string sql = "-- a comment\nCREATE PROC dbo.X\n    @Sku varchar(32),\n    @Id int OUTPUT\nAS\nSELECT 1";
        var header = Single(sql);

        Assert.Equal(1, header.Span.Line);
        Assert.Equal(0, header.Span.Column);
        Assert.Equal(sql.IndexOf("CREATE", StringComparison.Ordinal), header.Span.Start);
        Assert.EndsWith("AS", sql.Substring(header.Span.Start, header.Span.Length), StringComparison.Ordinal);

        var p = header.Parameters.AsArray();
        Assert.Equal(2, p.Length);

        Assert.Equal(2, p[0].Span.Line);
        Assert.Equal(4, p[0].Span.Column);
        Assert.Equal("@Sku varchar(32)", sql.Substring(p[0].Span.Start, p[0].Span.Length));

        Assert.Equal(3, p[1].Span.Line);
        Assert.Equal(4, p[1].Span.Column);
        Assert.Equal("@Id int OUTPUT", sql.Substring(p[1].Span.Start, p[1].Span.Length));

        // CRLF must not shift the column, or every location in a Windows-authored script is wrong by one.
        var crlf = Single("CREATE PROC dbo.X\r\n    @A int\r\nAS\r\nSELECT 1").Parameters.AsArray()[0];
        Assert.Equal(1, crlf.Span.Line);
        Assert.Equal(4, crlf.Span.Column);
    }

    [Fact]
    public void UnwrapsTheSysSchemaFromABuiltInTypeName()
    {
        var p = Params("CREATE PROC dbo.X @a sys.varchar(32), @b [sys].[int], @c sys.sysname AS SELECT 1");

        Assert.Equal(3, p.Length);
        Assert.Equal("varchar", p[0].TypeName);
        Assert.Equal<int?>(32, p[0].Length);
        Assert.Equal("int", p[1].TypeName);

        // Unwrapping sys. does not exempt the name from the family table.
        Assert.Equal("", p[2].TypeName);
    }

    [Fact]
    public void ReadsTheHeaderThroughCommentsPlacedAnywhereInIt()
    {
        var header = Single(
            "CREATE /* c */ OR -- line\n ALTER /* /* nested */ */ PROC /*x*/ dbo /*y*/ . /*z*/ X\n"
            + " @A /*t*/ varchar /*l*/ ( /*n*/ 32 /*m*/ ) /*o*/ OUTPUT\n AS SELECT 1");

        Assert.Equal("X", header.Name);

        var p = header.Parameters.AsArray();
        Assert.Single(p);
        Assert.Equal("varchar", p[0].TypeName);
        Assert.Equal<int?>(32, p[0].Length);
        Assert.True(p[0].IsOutput);
    }

    [Theory]
    [InlineData("SELECT 2CREATE PROC dbo.X @a int AS SELECT 1")]
    [InlineData("SELECT @CREATE PROC dbo.X @a int AS SELECT 1")]
    [InlineData("SELECT RECREATE PROC dbo.X @a int AS SELECT 1")]
    public void RecognisesTheKeywordOnlyAsAWholeToken(string sql)
    {
        Assert.Empty(Parse(sql));
    }

    [Fact]
    public void TreatsBracketedTextInABodyAsOpaque()
    {
        // A bracketed identifier can neither hide the next procedure nor fake one.
        var headers = Parse(
            "CREATE PROC dbo.A @x int AS SELECT [CREATE PROC dbo.Fake @y int AS] FROM t\n"
            + "CREATE PROC dbo.B @z int AS SELECT 1");

        Assert.Equal(2, headers.Count);
        Assert.Equal("A", headers[0].Name);
        Assert.Equal("B", headers[1].Name);
    }

    [Fact]
    public void ReadsNullNotNullAndVaryingModifiers()
    {
        var p = Params("CREATE PROC dbo.X @a int NOT NULL, @b varchar(8) NULL OUTPUT, @c int VARYING AS SELECT 1");

        Assert.Equal(3, p.Length);
        Assert.Equal("int", p[0].TypeName);
        Assert.True(p[1].IsOutput);
        Assert.Equal("int", p[2].TypeName);
    }

    [Fact]
    public void DoesNotMistakeAParameterNamedAsForTheBodysAs()
    {
        Assert.Equal(2, Params("CREATE PROC dbo.X @As int, @Astronomy varchar(4) AS SELECT 1").Length);
    }

    [Fact]
    public void LowerCasesTypeNamesWithTheInvariantCulture()
    {
        // The Turkish-I hazard ApplySqlType already documents: a culture-sensitive lower-casing turns
        // "INT" into "ınt" on a tr-TR machine, and the emitted contract then fails against TYPE_NAME().
        var p = Params("CREATE PROC dbo.X @a INT, @b INTEGER AS SELECT 1");

        Assert.Equal("int", p[0].TypeName);
        Assert.Equal("int", p[1].TypeName);
    }

    [Fact]
    public void ReadsUnicodeIdentifiers()
    {
        Assert.Equal("Ürün_Ekle", Single("CREATE PROC dbo.Ürün_Ekle @Ad varchar(32) AS SELECT 1").Name);
    }

    [Fact]
    public void PropagatesCancellationRatherThanReturningAPartialResult()
    {
        // Returning what had been parsed so far would let Roslyn store a truncated list as the step's
        // output for an input that never changed; the next uncancelled run would compare equal and keep
        // it. Cancellation is not a property of the file, which is what "never throws" is about.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => ProcedureHeaderParser.Parse(Path, "CREATE PROC dbo.X @a int AS SELECT 1", cts.Token));
    }

    [Fact]
    public void HandlesAFileWithVeryManyProcedures()
    {
        // A command-line build gets no incrementality at all - Roslyn persists nothing between runs - so
        // every .sql in the project is parsed from scratch on every build. This is a scale and
        // stack-safety check, not a timing assertion: a recursive parser would die here.
        var sql = string.Concat(Enumerable.Repeat(
            "CREATE OR ALTER PROCEDURE dbo.P @a varchar(32), @b decimal(18,2), @c int OUTPUT\n"
            + "AS\nBEGIN\n SELECT 'x' /* c */ -- c\n END\nGO\n",
            2000));

        Assert.Equal(2000, Parse(sql).Count);
    }
}
