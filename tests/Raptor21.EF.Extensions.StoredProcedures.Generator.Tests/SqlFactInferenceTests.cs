using Microsoft.CodeAnalysis;
using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator.Tests;

/// <summary>
/// End-to-end tests for parameter facts read from the procedure's .sql at compile time.
/// </summary>
/// <remarks>
/// <para>
/// These run the real generator through the real driver with real <c>AdditionalTexts</c>, because the
/// claim being tested is about emitted text and reported diagnostics, not about any one component. The
/// twelve tests in <c>StoredProcedureGeneratorTests</c> stay exactly as they are and supply no script at
/// all - between them, the two files cover both halves of a consumer's world.
/// </para>
/// <para>
/// Every full-text comparison normalises line endings first. The generator builds its output with
/// <c>StringBuilder.AppendLine</c>, which writes <c>Environment.NewLine</c>: an un-normalised assertion
/// passes on a Windows developer machine and fails on the Linux CI runner.
/// </para>
/// </remarks>
public class SqlFactInferenceTests
{
    private const string Usings = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Raptor21.EF.Extensions.StoredProcedures.Generated;
        """;

    private const string UpsertPath = "/x/DbScripts/Product_Upsert.sql";

    private const string UpsertSql = """
        CREATE OR ALTER PROCEDURE dbo.Product_Upsert
            @Sku   varchar(32),
            @Name  varchar(128),
            @Price decimal(18,2),
            @Id    int OUTPUT
        AS
        BEGIN
            SET NOCOUNT ON;
            RETURN 0;
        END
        """;

    /// <summary>The brief's "after" declaration: no <c>[Sql]</c> anywhere.</summary>
    private const string BareUpsert = """

        namespace Demo;

        [StoredProcedureGroup]
        public partial class CatalogProcedures
        {
            [StoredProcedure("dbo.Product_Upsert")]
            public partial Task<(int ReturnValue, int Id)> UpsertAsync(
                string sku, string name, decimal price, int id,
                CancellationToken ct = default);
        }
        """;

    /// <summary>The brief's "today" declaration: every facet spelled out.</summary>
    private const string AttributedUpsert = """

        namespace Demo;

        [StoredProcedureGroup]
        public partial class CatalogProcedures
        {
            [StoredProcedure("dbo.Product_Upsert")]
            public partial Task<(int ReturnValue, int Id)> UpsertAsync(
                [Sql("@Sku", 32)] string sku,
                [Sql("@Name", 128)] string name,
                [Sql("@Price", Precision = 18, Scale = 2)] decimal price,
                [Sql("@Id", "int", Output = true)] int id,
                CancellationToken ct = default);
        }
        """;

    private static string Norm(string text) => text.Replace("\r\n", "\n");

    private static void AssertNoDiagnostics(RunResult result) =>
        Assert.True(result.GeneratorDiagnostics.IsDefaultOrEmpty, result.DiagnosticSummary());

    private static void AssertCompiles(RunResult result) =>
        Assert.True(result.OutputErrors.IsDefaultOrEmpty, result.OutputErrorSummary());

    // ---------------------------------------------------------------- the headline

    [Fact]
    public void BareDeclaration_WithScript_EmitsExactlyWhatTheAttributedDeclarationEmits()
    {
        // The claim the whole feature is measured by, asserted directly rather than argued: generate both
        // forms and compare the text. The signature half is free - SigParam carries no attribute data -
        // so only the contract's SqlTypeSpec arguments could ever differ, and here they do not.
        var bare = GeneratorTestHarness.Run(Usings + BareUpsert, (UpsertPath, UpsertSql));
        var attributed = GeneratorTestHarness.Run(Usings + AttributedUpsert, (UpsertPath, UpsertSql));

        AssertCompiles(bare);
        AssertCompiles(attributed);
        AssertNoDiagnostics(bare);
        AssertNoDiagnostics(attributed);

        Assert.Equal(Norm(attributed.GeneratedText), Norm(bare.GeneratedText));
    }

    [Fact]
    public void AttributedDeclaration_EmitsIdenticallyWithAndWithoutTheScript()
    {
        // The other half of the byte-identity claim, and the one that carries the upgrade constraint: a
        // declaration that states every facet cannot be moved by anything the file says, so promoting a
        // corpus of .sql files to AdditionalFiles changes nothing for a consumer who spelled it all out.
        var withScript = GeneratorTestHarness.Run(Usings + AttributedUpsert, (UpsertPath, UpsertSql));
        var withoutScript = GeneratorTestHarness.Run(Usings + AttributedUpsert);

        AssertNoDiagnostics(withScript);
        AssertNoDiagnostics(withoutScript);

        Assert.Equal(Norm(withoutScript.GeneratedText), Norm(withScript.GeneratedText));
    }

    [Fact]
    public void BareDeclaration_WithScript_InfersNameTypeLengthPrecisionScaleAndOutput()
    {
        var result = GeneratorTestHarness.Run(Usings + BareUpsert, (UpsertPath, UpsertSql));

        AssertCompiles(result);

        // The emitted name is the file's exact spelling. A bare 'string sku' looks up '@sku', matches
        // '@Sku' case-insensitively and emits '@Sku' - not the derived lookup key.
        Assert.Contains("ProcParamSpec(\"@Sku\", new global::Raptor21.EF.Extensions.StoredProcedures.Contracts.SqlTypeSpec(\"varchar\", 32))", result.GeneratedText);
        Assert.Contains("ProcParamSpec(\"@Name\", new global::Raptor21.EF.Extensions.StoredProcedures.Contracts.SqlTypeSpec(\"varchar\", 128))", result.GeneratedText);
        Assert.Contains("ProcParamSpec(\"@Price\", new global::Raptor21.EF.Extensions.StoredProcedures.Contracts.SqlTypeSpec(\"decimal\", null, (byte)18, (byte)2))", result.GeneratedText);
        Assert.Contains("ProcParamSpec(\"@Id\", new global::Raptor21.EF.Extensions.StoredProcedures.Contracts.SqlTypeSpec(\"int\"), true)", result.GeneratedText);

        // And the arguments still go out in C# declaration order, which is the order the validator
        // compares the contract against sys.parameters in.
        Assert.Contains("new object?[] { sku, name, price, id }", result.GeneratedText);
    }

    // ---------------------------------------------------------------- no script, or the wrong one

    [Fact]
    public void BareParameter_WithNoScriptAtAll_StillReportsSpg003()
    {
        // Written with an explicit empty array to say out loud what is being tested: a consumer who never
        // promotes their .sql sees exactly today's behaviour. Existing test 9 makes the same guarantee
        // implicitly, by predating the parameter.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync(string accountId, CancellationToken ct = default);
            }
            """, Array.Empty<(string Path, string Text)>());

        Assert.True(result.HasGeneratorDiagnostic("SPG003"), result.DiagnosticSummary());
        Assert.DoesNotContain("__Contract_XAsync =", result.GeneratedText);

        // SPG003 keeps the parameter's own location rather than regressing to a file-less diagnostic.
        Assert.All(result.WithId("SPG003"), d => Assert.NotEqual(Location.None, d.Location));
    }

    [Fact]
    public void BareParameter_WithScriptForAnotherProcedure_ReportsSpg003AndSpg014()
    {
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync(string a, string b, string c, CancellationToken ct = default);
            }
            """, ("/x/Other.sql", "CREATE PROCEDURE dbo.Other @Z int AS BEGIN SELECT 1 END"));

        Assert.Equal(3, result.CountOf("SPG003"));

        // SPG014 separates "you forgot [Sql]" from "your .sql never reached the compiler" - the
        // Directory.Build.targets ordering trap and the ProjectReference-without-the-Import trap. Once per
        // method, however many parameters went unresolved, or a 400-procedure project would drown in it.
        Assert.Equal(1, result.CountOf("SPG014"));
        Assert.All(result.WithId("SPG014"), d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
    }

    // ---------------------------------------------------------------- precedence, per facet

    [Fact]
    public void AttributeWinsOverScript_ForEveryFacet()
    {
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.Product_Upsert")]
                public partial Task<int> UpsertAsync(
                    [Sql("@Sku", "nchar", 64)] string sku,
                    [Sql("@Name", 128)] string name,
                    [Sql("@Price", Precision = 9, Scale = 4)] decimal price,
                    [Sql("@Id", "int")] int id,
                    CancellationToken ct = default);
            }
            """, (UpsertPath, UpsertSql));

        AssertCompiles(result);
        Assert.Contains("ProcParamSpec(\"@Sku\", new global::Raptor21.EF.Extensions.StoredProcedures.Contracts.SqlTypeSpec(\"nchar\", 64))", result.GeneratedText);
        Assert.Contains("ProcParamSpec(\"@Price\", new global::Raptor21.EF.Extensions.StoredProcedures.Contracts.SqlTypeSpec(\"decimal\", null, (byte)9, (byte)4))", result.GeneratedText);

        // OUTPUT is the one asymmetric facet: any [Sql] at all makes the attribute authoritative, so the
        // file's OUTPUT on @Id is declined rather than taken. Declining it is still reported - but as
        // SPG020, not SPG010: this attribute states no direction at all, so it has contradicted nothing,
        // and SPG010's OUTPUT arm fires only on a direction the developer actually wrote. Either way the
        // direction of the emitted call never changes, which is what makes "SPG009 cannot newly fire for
        // a consumer who compiles today" provable.
        Assert.Contains("ProcParamSpec(\"@Id\", new global::Raptor21.EF.Extensions.StoredProcedures.Contracts.SqlTypeSpec(\"int\"))", result.GeneratedText);
        Assert.DoesNotContain("SqlTypeSpec(\"int\"), true)", result.GeneratedText);
    }

    [Fact]
    public void AttributeContradictingScript_ReportsSpg010PerFacet()
    {
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.Product_Upsert")]
                public partial Task<int> UpsertAsync(
                    [Sql("@Sku", "nchar", 64)] string sku,
                    [Sql("@Name", 128)] string name,
                    [Sql("@Price", Precision = 9, Scale = 4)] decimal price,
                    [Sql("@Id", "int")] int id,
                    CancellationToken ct = default);
            }
            """, (UpsertPath, UpsertSql));

        // One SPG010 per contradicting facet: @Sku's type and its length, @Price's precision and its
        // scale. @Name agrees with the file and is silent, which is the assertion that keeps this honest.
        // @Id is deliberately not a fifth. [Sql("@Id", "int")] leaves Output unstated, so the script's
        // OUTPUT is a fact the attribute declined rather than one it contradicted, and SPG020 is the
        // diagnostic that says so - which is why the OUTPUT facet is asserted here by its absence from
        // every SPG010 message as well as by the SPG020 count.
        Assert.Equal(4, result.CountOf("SPG010"));
        Assert.All(result.WithId("SPG010"), d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.All(result.WithId("SPG010"), d => Assert.DoesNotContain("OUTPUT", d.GetMessage()));

        Assert.Equal(1, result.CountOf("SPG020"));
        Assert.All(result.WithId("SPG020"), d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.All(result.WithId("SPG020"), d => Assert.Contains("'@Id'", d.GetMessage()));

        // Warnings, never errors: the code still compiles, and TreatWarningsAsErrors is off repo-wide.
        AssertCompiles(result);
    }

    [Fact]
    public void AttributeSilentOnTypeName_TakesNvarcharFromTheScript()
    {
        // A documented behaviour change, and the reason it needs a test rather than a changelog line:
        // InferSqlType maps every string to varchar and has no nvarchar path at all, so this consumer has
        // been binding SqlDbType.VarChar against a Unicode parameter and paying for the implicit
        // conversion - or failing CompareSqlType at startup, if they run the validator.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@Name", 128)] string name, CancellationToken ct = default);
            }
            """, ("/x/X.sql", "CREATE PROCEDURE dbo.X @Name nvarchar(128) AS BEGIN SELECT 1 END"));

        AssertCompiles(result);
        Assert.Contains("SqlTypeSpec(\"nvarchar\", 128)", result.GeneratedText);

        // No SPG010: the attribute said nothing about the type, so there is nothing to contradict.
        Assert.False(result.HasGeneratorDiagnostic("SPG010"), result.DiagnosticSummary());
    }

    [Fact]
    public void AttributeSilentOnScale_TakesScaleFromTheScript()
    {
        // Emits (byte)18, (byte)0 today. This closes the known decimal-scale gap for procedures whose
        // .sql spells the scale out - not in general: a procedure declaring a bare decimal still
        // truncates, exactly as SQL Server does. The library now agrees with the procedure, rather than
        // guessing.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@Price", Precision = 18)] decimal price, CancellationToken ct = default);
            }
            """, ("/x/X.sql", "CREATE PROCEDURE dbo.X @Price decimal(18,2) AS BEGIN SELECT 1 END"));

        AssertCompiles(result);
        Assert.Contains("SqlTypeSpec(\"decimal\", null, (byte)18, (byte)2)", result.GeneratedText);
    }

    [Fact]
    public void VarcharMax_EmitsMinusOneLength()
    {
        // Today -1 is the "unspecified" sentinel, so MAX is silently lost. With Length modelled as int?,
        // null is unspecified and -1 is genuinely MAX; ApplySqlType already sets Size = -1 and
        // CompareSqlType skips its char conversion for a non-positive max_length, so it validates.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync(string blob, CancellationToken ct = default);
            }
            """, ("/x/X.sql", "CREATE PROCEDURE dbo.X @Blob varchar(max) AS BEGIN SELECT 1 END"));

        AssertCompiles(result);
        Assert.Contains("SqlTypeSpec(\"varchar\", -1)", result.GeneratedText);
    }

    // ---------------------------------------------------------------- the new warnings

    [Fact]
    public void BareCharTypeInScript_ReportsSpg012AndInfersNoLength()
    {
        // The sharpest silent-regression risk in the whole feature, and it is counter-intuitive: the
        // FAITHFUL reading (a bare varchar is varchar(1)) is the dangerous one. Recording 1 would set
        // SqlParameter.Size = 1, truncate every value to one character at the server, and pass startup
        // validation, because sys.parameters.max_length really is 1. Nothing is inferred; the emitted
        // code is exactly today's, and the developer is told the procedure is almost certainly wrong.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync(string sku, CancellationToken ct = default);
            }
            """, ("/x/X.sql", "CREATE PROCEDURE dbo.X @Sku varchar AS BEGIN SELECT 1 END"));

        AssertCompiles(result);
        Assert.Contains("ProcParamSpec(\"@Sku\", new global::Raptor21.EF.Extensions.StoredProcedures.Contracts.SqlTypeSpec(\"varchar\"))", result.GeneratedText);
        Assert.DoesNotContain("SqlTypeSpec(\"varchar\", 1)", result.GeneratedText);

        Assert.True(result.HasGeneratorDiagnostic("SPG012"), result.DiagnosticSummary());

        // Located in the .sql, on the parameter, so the squiggle lands where the defect is.
        Assert.All(result.WithId("SPG012"), d => Assert.EndsWith("X.sql", d.Location.GetLineSpan().Path));
    }

    [Fact]
    public void ScriptDeclaresAParameterTheContractOmits_ReportsSpg011()
    {
        // StoredProcedureValidator throws on dbParams.Count != contractParams.Count even when the SQL
        // parameter has a DEFAULT, so this contract fails at startup. Moving that from boot to build is
        // strictly better; a warning rather than an error because a stale .sql must not break a build.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync(string sku, CancellationToken ct = default);
            }
            """, ("/x/X.sql", "CREATE PROCEDURE dbo.X @Sku varchar(32), @Extra int = 5 AS BEGIN SELECT 1 END"));

        AssertCompiles(result);
        Assert.Equal(1, result.CountOf("SPG011"));
        Assert.All(result.WithId("SPG011"), d => Assert.Contains("@Extra", d.GetMessage()));

        // Still a working contract: the warning names a risk, it does not withhold the code.
        Assert.Contains("__Contract_XAsync =", result.GeneratedText);
    }

    [Fact]
    public void ScriptTypeIncompatibleWithClrType_ReportsSpg013()
    {
        // Deliberately narrow: only pairs where a silent bind is data corruption or an outright failure.
        // @Id int against short and @Flag bit against int are conversions SqlClient makes freely, and
        // rejecting them would block a working pattern - so the table must stay quiet about both.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync(string count, int sku, short id, int flag, CancellationToken ct = default);
            }
            """, ("/x/X.sql", "CREATE PROCEDURE dbo.X @Count int, @Sku varchar(32), @Id int, @Flag bit AS BEGIN SELECT 1 END"));

        AssertCompiles(result);
        Assert.Equal(2, result.CountOf("SPG013"));
        Assert.Contains(result.WithId("SPG013"), d => d.GetMessage().Contains("@Count"));
        Assert.Contains(result.WithId("SPG013"), d => d.GetMessage().Contains("@Sku"));
        Assert.All(result.WithId("SPG013"), d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
    }

    [Fact]
    public void ScriptTypeIsNeverSecondGuessedWhenTheDeveloperWroteIt()
    {
        // SPG013 applies only to a type the file supplied. A hand-written [Sql(TypeName)] is the
        // developer's call - they may know something about the procedure that the file in front of the
        // compiler does not say.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@Count", "varchar", 8)] string count, CancellationToken ct = default);
            }
            """, ("/x/X.sql", "CREATE PROCEDURE dbo.X @Count varchar(8) AS BEGIN SELECT 1 END"));

        AssertCompiles(result);
        Assert.False(result.HasGeneratorDiagnostic("SPG013"), result.DiagnosticSummary());
    }

    [Fact]
    public void TwoScriptsForOneProcedure_ReportSpg015AndInferNoFacts()
    {
        // Refusing to guess is the honest answer, and it matches the precedent the runtime already sets,
        // where two embedded resources yielding one script name throw naming both. The consequence is
        // loud but correct: the bare parameter falls through to SPG003, which says exactly what to fix.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync(string sku, CancellationToken ct = default);
            }
            """,
            ("/x/a.sql", "CREATE PROCEDURE dbo.X @Sku varchar(32) AS BEGIN SELECT 1 END"),
            ("/x/b.sql", "CREATE PROCEDURE dbo.X @Sku varchar(64) AS BEGIN SELECT 1 END"));

        Assert.Equal(1, result.CountOf("SPG015"));
        Assert.All(result.WithId("SPG015"), d =>
        {
            Assert.Contains("a.sql", d.GetMessage());
            Assert.Contains("b.sql", d.GetMessage());
        });

        Assert.True(result.HasGeneratorDiagnostic("SPG003"), result.DiagnosticSummary());
        Assert.DoesNotContain("SqlTypeSpec(\"varchar\", 32)", result.GeneratedText);
        Assert.DoesNotContain("SqlTypeSpec(\"varchar\", 64)", result.GeneratedText);

        // Not SPG014: a script was found, and refused. Saying both at once would be two answers to one
        // question.
        Assert.False(result.HasGeneratorDiagnostic("SPG014"), result.DiagnosticSummary());
    }

    // ---------------------------------------------------------------- matching rules

    [Theory]
    [InlineData("dbo.Product_Upsert", "CREATE PROCEDURE [DBO].[PRODUCT_UPSERT] @Sku varchar(32) AS BEGIN SELECT 1 END")]
    [InlineData("[dbo].[Product_Upsert]", "CREATE PROCEDURE dbo.Product_Upsert @Sku varchar(32) AS BEGIN SELECT 1 END")]
    [InlineData("Product_Upsert", "CREATE PROCEDURE dbo.Product_Upsert @Sku varchar(32) AS BEGIN SELECT 1 END")]
    public void ProcedureIdentityMatchesCaseInsensitivelyAndThroughBrackets(string declared, string sql)
    {
        // T-SQL identifiers are case-insensitive under the default collation, so a case-only difference
        // joins and is not a diagnostic. Both sides canonicalise through one function - ProcKey.From -
        // so there is no comparer for a caller to forget to pass.
        var result = GeneratorTestHarness.Run(Usings + $$"""

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("{{declared}}")]
                public partial Task<int> XAsync(string sku, CancellationToken ct = default);
            }
            """, ("/x/a.sql", sql));

        AssertCompiles(result);
        AssertNoDiagnostics(result);
        Assert.Contains("SqlTypeSpec(\"varchar\", 32)", result.GeneratedText);
    }

    [Fact]
    public void ParametersMatchByNameNotByPosition()
    {
        // The regression this guards cannot be caught at run time: the contract's order is the positional
        // binding order of the object?[] handed to the executor, and StoredProcedureValidator compares
        // the contract to sys.parameters by position too. A positional guess that landed wrong would send
        // the price to @Sku and validate cleanly.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.T")]
                public partial Task<int> TAsync(decimal price, string sku, CancellationToken ct = default);
            }
            """, ("/x/T.sql", "CREATE PROCEDURE dbo.T @Sku varchar(32), @Price decimal(18,2) AS BEGIN SELECT 1 END"));

        AssertCompiles(result);
        AssertNoDiagnostics(result);

        Assert.Contains("ProcParamSpec(\"@Price\", new global::Raptor21.EF.Extensions.StoredProcedures.Contracts.SqlTypeSpec(\"decimal\", null, (byte)18, (byte)2))", result.GeneratedText);
        Assert.Contains("ProcParamSpec(\"@Sku\", new global::Raptor21.EF.Extensions.StoredProcedures.Contracts.SqlTypeSpec(\"varchar\", 32))", result.GeneratedText);

        // The list is filled in by name, never rebuilt in the file's order.
        Assert.Contains("new object?[] { price, sku }", result.GeneratedText);
    }

    [Fact]
    public void CancellationTokenIsNeverMatchedToASqlParameter()
    {
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync(string sku, CancellationToken ct = default);
            }
            """, ("/x/X.sql", "CREATE PROCEDURE dbo.X @Sku varchar(32), @ct int AS BEGIN SELECT 1 END"));

        AssertCompiles(result);

        // The token is skipped before matching, so it never gets a lookup key - and @ct is therefore an
        // unbound SQL parameter like any other, which is exactly what SPG011 is for.
        Assert.Contains(result.WithId("SPG011"), d => d.GetMessage().Contains("@ct"));
        Assert.DoesNotContain("ProcParamSpec(\"@ct\"", result.GeneratedText);
        Assert.Contains("new object?[] { sku }", result.GeneratedText);
    }

    // ---------------------------------------------------------------- structural guarantees

    [Fact]
    public void AnUnresolvableParameterIsNeverDroppedFromAContract()
    {
        // The structural trap this design exists to close. Dropping the unresolvable parameter and
        // emitting the rest would produce a contract with fewer parameters than the procedure: clean at
        // build, fatal at boot on dbParams.Count != contractParams.Count. SpParams is populated only when
        // the method resolves, so a short contract cannot be represented at all.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@Sku", 32)] string sku, string mystery, CancellationToken ct = default);
            }
            """);

        Assert.True(result.HasGeneratorDiagnostic("SPG003"), result.DiagnosticSummary());

        Assert.DoesNotContain("__Contract_XAsync =", result.GeneratedText);
        Assert.DoesNotContain("ProcParamSpec(\"@Sku\"", result.GeneratedText);
        Assert.DoesNotContain("GeneratedProcedureRegistry", result.GeneratedText);
        Assert.Contains("throw new global::System.NotSupportedException", result.GeneratedText);
    }

    [Fact]
    public void UnsupportedParameterType_ReportsSpg005()
    {
        // SPG005 has no coverage at all today and can regress silently. DateTimeOffset is deliberate:
        // it is a type ClassifyClr does not classify, so nothing is inferred and nothing is rejected.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@When")] System.DateTimeOffset when, CancellationToken ct = default);
            }
            """);

        Assert.True(result.HasGeneratorDiagnostic("SPG005"), result.DiagnosticSummary());
    }

    [Fact]
    public void Spg009_DoesNotFireWhenInferredOutputArityMatches()
    {
        // The mirror of existing test 8, which supplies no script and stays untouched. There the OUTPUT
        // count comes from attributes and SPG009 fires; here it is inferred from the file and matches.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<(int ReturnValue, int NewId)> XAsync(string prefix, int newId, CancellationToken ct = default);
            }
            """, ("/x/X.sql", "CREATE PROCEDURE dbo.X @Prefix varchar(10), @NewId int OUTPUT AS BEGIN SELECT 1 END"));

        AssertCompiles(result);
        Assert.False(result.HasGeneratorDiagnostic("SPG009"), result.DiagnosticSummary());
        Assert.Contains("ProcParamSpec(\"@NewId\", new global::Raptor21.EF.Extensions.StoredProcedures.Contracts.SqlTypeSpec(\"int\"), true)", result.GeneratedText);
    }

    // ---------------------------------------------------------------- the upgrade constraint

    [Fact]
    public void NonProcedureSqlFileProducesNoDiagnosticOfAnySeverity()
    {
        // The single most important test for the upgrade constraint. The real consumer has ~65 legacy
        // scripts that do not pass even today's much simpler parser; promoting all of them to
        // AdditionalFiles must cost nothing. The .sql branch reports nothing of its own, ever - every
        // .sql-derived diagnostic is attached to a method during resolution - so a file that fails to
        // parse, declares no procedure, or matches no declaration is silent at every severity.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@Id", "int")] int id, CancellationToken ct = default);
            }
            """,
            ("/x/legacy.sql", "-- CREATE OR ALTER PROCEDURE dbo.Commented\nINSERT INTO dbo.T (A) VALUES (1)\nGO\nUPDATE dbo.T SET A = 2\nGO\n"),
            ("/x/malformed.sql", "CREATE PROCEDURE dbo."),
            ("/x/truncated.sql", "/* unterminated"));

        AssertCompiles(result);
        AssertNoDiagnostics(result);
        Assert.Contains("ProcParamSpec(\"@Id\", new global::Raptor21.EF.Extensions.StoredProcedures.Contracts.SqlTypeSpec(\"int\"))", result.GeneratedText);
    }

    [Fact]
    public void ScriptsWithoutDeclarations_GenerateNothingAndSayNothing()
    {
        // The correct behaviour for "I promoted the files, I have not written the declarations yet".
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            public class NotAGroup { }
            """, (UpsertPath, UpsertSql));

        AssertNoDiagnostics(result);
        Assert.DoesNotContain("GeneratedProcedureRegistry", result.GeneratedText);
    }
}
