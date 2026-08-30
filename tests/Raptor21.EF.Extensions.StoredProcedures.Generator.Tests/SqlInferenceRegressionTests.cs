using Microsoft.CodeAnalysis;
using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator.Tests;

/// <summary>
/// The defects two adversarial reviews found in .sql parameter inference, one test each.
/// </summary>
/// <remarks>
/// <para>
/// Every test here failed against the implementation it was written against, and each pins a rule rather
/// than an implementation: what a <c>[Sql]</c>-stated <c>-1</c> means, what happens when the script's
/// type is one the parser declined, what a silent contract change costs, where an inferred OUTPUT is
/// allowed to go, and which advisories a method that reads nothing from a script is allowed to be
/// interrupted by.
/// </para>
/// <para>
/// The second review's findings run from <c>AttributionStyleIsInvisibleWhenTheContractDoesNotMove</c>
/// down, and they share one theme the first review's do not: a diagnostic is only worth reporting when
/// the developer can see what it is about in their own code. A gate that measured where a fact came from
/// made two spellings of one contract sound different; SPG010's OUTPUT arm reported a word the developer
/// had not written; SPG018 named a remedy that traded a build error for a boot failure; and the one case
/// a fully attributed codebase most needed to hear about was the only one nothing was said for.
/// </para>
/// <para>
/// The through-line is the upgrade constraint. The real consumer has roughly sixty-five legacy scripts and
/// a codebase that compiles today, so promoting those scripts to <c>AdditionalFiles</c> may add
/// diagnostics only where the generator actually changed its mind - and may never add an error to a
/// declaration the old generator accepted. Half the assertions below are that nothing happened.
/// </para>
/// </remarks>
public class SqlInferenceRegressionTests
{
    private const string Usings = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Raptor21.EF.Extensions.StoredProcedures.Generated;
        """;

    private const string Contracts = "global::Raptor21.EF.Extensions.StoredProcedures.Contracts";

    /// <summary>Normalises line endings, which the generator writes as <c>Environment.NewLine</c>.</summary>
    private static string Norm(string text) => text.Replace("\r\n", "\n");

    private static void AssertNoDiagnostics(RunResult result) =>
        Assert.True(result.GeneratorDiagnostics.IsDefaultOrEmpty, result.DiagnosticSummary());

    private static void AssertCompiles(RunResult result) =>
        Assert.True(result.OutputErrors.IsDefaultOrEmpty, result.OutputErrorSummary());

    // ---------------------------------------------------------------- byte identity

    [Fact]
    public void AttributeStatedMinusOneLength_WithNoScript_EmitsTheBareSpecItAlwaysEmitted()
    {
        // The shipped generator defaulted Length to -1 and rendered a length only when it was zero or
        // more, so [Sql("@Blob", "varbinary", -1)] rendered a bare SqlTypeSpec. Modelling Length as int?
        // turned that sentinel into a real value and moved the contract of a consumer who has no .sql in
        // the compilation at all - the one population the whole feature promised not to touch.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@Blob", "varbinary", -1)] byte[] blob, CancellationToken ct = default);
            }
            """);

        AssertCompiles(result);
        AssertNoDiagnostics(result);

        Assert.Contains($"ProcParamSpec(\"@Blob\", new {Contracts}.SqlTypeSpec(\"varbinary\"))", result.GeneratedText);
        Assert.DoesNotContain("SqlTypeSpec(\"varbinary\", -1)", result.GeneratedText);
    }

    [Fact]
    public void ScriptDeclaredMax_SurvivesTheSentinelAndIsReported()
    {
        // The other half of the decision: -1 means MAX, and the only source allowed to say it is the
        // script, where "varbinary(max)" says MAX and can say nothing else. The attribute's -1 stays
        // "unstated" all the way through, so the file is free to fill the facet - and filling it is a
        // change to a contract that compiles today, which is SPG017's job to say out loud.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@Blob", "varbinary", -1)] byte[] blob, CancellationToken ct = default);
            }
            """, ("/x/X.sql", "CREATE PROCEDURE dbo.X @Blob varbinary(max) AS BEGIN SELECT 1 END"));

        AssertCompiles(result);
        Assert.Contains($"ProcParamSpec(\"@Blob\", new {Contracts}.SqlTypeSpec(\"varbinary\", -1))", result.GeneratedText);

        // And not as a contradiction: the attribute stated no length, so there is nothing to contradict.
        Assert.False(result.HasGeneratorDiagnostic("SPG010"), result.DiagnosticSummary());
        Assert.Equal(1, result.CountOf("SPG017"));
        Assert.All(result.WithId("SPG017"), d => Assert.Contains("max", d.GetMessage()));
    }

    // ---------------------------------------------------------------- a type the parser declined

    [Theory]
    [InlineData("CREATE PROCEDURE dbo.X @Name sysname AS BEGIN SELECT 1 END")]
    [InlineData("CREATE PROCEDURE dbo.X @Name sql_variant AS BEGIN SELECT 1 END")]
    [InlineData("CREATE PROCEDURE dbo.X @Name dbo.NameList READONLY AS BEGIN SELECT 1 END")]
    public void ScriptTypeTheParserDeclined_IsAnErrorAndNeverTheClrGuess(string sql)
    {
        // The regression this closes is a build error turning into a boot failure. Before the feature a
        // parameter with no [Sql] was SPG003 and the build stopped; with a script in the compilation the
        // name matched, the declined type fell through the ?? chain, and the .NET guess was emitted -
        // silently, and guaranteed wrong, because the procedure's real type is one this generator cannot
        // bind. The parser refusing to model a type has to mean the resolver refuses too.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync(string name, CancellationToken ct = default);
            }
            """, ("/x/X.sql", sql));

        Assert.True(result.HasGeneratorDiagnostic("SPG016"), result.DiagnosticSummary());
        Assert.All(result.WithId("SPG016"), d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.All(result.WithId("SPG016"), d => Assert.NotEqual(Location.None, d.Location));

        // No guess reached the emitted code, and no contract was emitted for the method at all.
        Assert.DoesNotContain("SqlTypeSpec(\"varchar\")", result.GeneratedText);
        Assert.DoesNotContain("__Contract_XAsync =", result.GeneratedText);
    }

    [Fact]
    public void AttributedParameter_KeepsItsGuessWhenTheScriptTypeIsDeclined()
    {
        // The boundary that keeps SPG016 from being an upgrade wall. This declaration compiles today and
        // emits varchar from the .NET type; the script says sysname, which supplies nothing, so the
        // contract is exactly what it was and an error here would reject code the old generator accepted.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@Name")] string name, CancellationToken ct = default);
            }
            """, ("/x/X.sql", "CREATE PROCEDURE dbo.X @Name sysname AS BEGIN SELECT 1 END"));

        AssertCompiles(result);
        Assert.False(result.HasGeneratorDiagnostic("SPG016"), result.DiagnosticSummary());
        Assert.Contains($"ProcParamSpec(\"@Name\", new {Contracts}.SqlTypeSpec(\"varchar\"))", result.GeneratedText);

        // Keeping the guess is right; keeping it in silence is not. The contract did not move, so this
        // cannot be an error - but 'varchar' against a sysname parameter is a startup failure with a date
        // on it, and SPG019 is the only report that reaches a declaration carrying [Sql].
        Assert.Equal(1, result.CountOf("SPG019"));
        Assert.All(result.WithId("SPG019"), d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
    }

    // ---------------------------------------------------------------- a silent contract change

    [Fact]
    public void ScriptFillingAFacetOnAnAttributedParameter_ReportsSpg017PerFacet()
    {
        // [Sql("@Name")] string name emits SqlTypeSpec("varchar") today. With the script promoted it
        // emits SqlTypeSpec("nvarchar", 100) instead - a better contract, and one nobody asked for. SPG010
        // cannot catch it, because it compares only facets the attribute actually states, so a stale
        // script could rewrite a working contract and newly fail startup validation with nothing said.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@Name")] string name, CancellationToken ct = default);
            }
            """, ("/x/X.sql", "CREATE PROCEDURE dbo.X @Name nvarchar(100) AS BEGIN SELECT 1 END"));

        AssertCompiles(result);
        Assert.Contains($"ProcParamSpec(\"@Name\", new {Contracts}.SqlTypeSpec(\"nvarchar\", 100))", result.GeneratedText);

        // One per facet that moved: the SQL type and the length. A warning rather than an error, because
        // the fill is the feature working as designed - and not informational, because Info is invisible:
        // it was measured at zero occurrences under `dotnet build` and under -v n, and six only at -v d,
        // so the one report that says a contract moved without anyone editing it would reach no CI log
        // any team reads.
        Assert.Equal(2, result.CountOf("SPG017"));
        Assert.All(result.WithId("SPG017"), d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));

        // Each names the facet and both values, which is what makes the report actionable.
        Assert.Contains(result.WithId("SPG017"), d =>
            d.GetMessage().Contains("SQL type") && d.GetMessage().Contains("'varchar'") && d.GetMessage().Contains("'nvarchar'"));
        Assert.Contains(result.WithId("SPG017"), d =>
            d.GetMessage().Contains("length") && d.GetMessage().Contains("none") && d.GetMessage().Contains("'100'"));

        Assert.False(result.HasGeneratorDiagnostic("SPG010"), result.DiagnosticSummary());
    }

    [Fact]
    public void AFillThatLandsOnTheValueTheDeclarationAlreadyHad_SaysNothing()
    {
        // The other half of SPG017, and the reason it compares emitted values rather than stated ones:
        // [Sql("@Sku", 32)] takes its type from the file and lands on the varchar the .NET type already
        // implied. A consumer whose contract did not move has nothing to read about.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@Sku", 32)] string sku, CancellationToken ct = default);
            }
            """, ("/x/X.sql", "CREATE PROCEDURE dbo.X @Sku varchar(32) AS BEGIN SELECT 1 END"));

        AssertCompiles(result);
        AssertNoDiagnostics(result);
        Assert.Contains($"ProcParamSpec(\"@Sku\", new {Contracts}.SqlTypeSpec(\"varchar\", 32))", result.GeneratedText);
    }

    // ---------------------------------------------------------------- an OUTPUT with nowhere to go

    [Fact]
    public void InferredOutput_OnAMethodThatCannotReturnIt_IsAnError()
    {
        // SPG009's mirror, and it was unchecked: SPG009 only runs for a method that returns its OUTPUT
        // parameters. Here the direction comes from the script, the parameter binds as Direction.Output,
        // and the value SQL Server writes back is dropped on the floor - which validates cleanly at
        // startup, because the contract and the catalog agree, and is wrong every time it runs.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync(int a, int b, CancellationToken ct = default);
            }
            """, ("/x/X.sql", "CREATE PROCEDURE dbo.X @a int, @b int OUTPUT AS BEGIN SELECT 1 END"));

        Assert.True(result.HasGeneratorDiagnostic("SPG018"), result.DiagnosticSummary());
        Assert.All(result.WithId("SPG018"), d => Assert.Equal(DiagnosticSeverity.Error, d.Severity));
        Assert.All(result.WithId("SPG018"), d => Assert.Contains("@b", d.GetMessage()));

        Assert.DoesNotContain("__Contract_XAsync =", result.GeneratedText);
    }

    [Fact]
    public void AttributeDeclaredOutput_OnTheSameShape_IsStillAccepted()
    {
        // The boundary that keeps SPG018 from being an upgrade wall. Declaring an OUTPUT parameter on a
        // method that returns only the RETURN value compiles today - a caller who genuinely does not want
        // the value - so the new error is scoped to a direction the script supplied.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@a", "int")] int a, [Sql("@b", "int", Output = true)] int b, CancellationToken ct = default);
            }
            """);

        AssertCompiles(result);
        AssertNoDiagnostics(result);
        Assert.Contains($"ProcParamSpec(\"@b\", new {Contracts}.SqlTypeSpec(\"int\"), true)", result.GeneratedText);
    }

    // ---------------------------------------------------------------- warning volume on upgrade

    [Fact]
    public void Spg011_IsSilentForAFullyAttributedMethodAndLoudForAScriptDrivenOne()
    {
        // Ungated, SPG011 fires once per script parameter the method does not carry, DEFAULTed parameters
        // included - so a legacy corpus produces one warning per optional parameter of every procedure,
        // about contracts that did not move when the files were promoted, and TreatWarningsAsErrors turns
        // every one of them into a build failure on upgrade.
        const string script = "CREATE PROCEDURE dbo.X @A int, @B int = 0 AS BEGIN SELECT 1 END";

        var attributed = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@A", "int")] int a, CancellationToken ct = default);
            }
            """, ("/x/X.sql", script));

        AssertCompiles(attributed);
        AssertNoDiagnostics(attributed);
        Assert.Contains("__Contract_XAsync =", attributed.GeneratedText);

        // And the gate is not a mute button. The moment a parameter takes its facts from the script, the
        // script is what the method is being built from, and a parameter missing from the contract is the
        // next thing that will fail - at startup, on dbParams.Count != contractParams.Count.
        var scriptDriven = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync(int a, CancellationToken ct = default);
            }
            """, ("/x/X.sql", script));

        AssertCompiles(scriptDriven);
        Assert.Equal(1, scriptDriven.CountOf("SPG011"));
        Assert.All(scriptDriven.WithId("SPG011"), d => Assert.Contains("@B", d.GetMessage()));
    }

    [Fact]
    public void Spg012_IsSilentForAParameterTheScriptDoesNotFeed()
    {
        // A bare 'varchar' in a legacy script is common, and for a parameter that states its own facets
        // the emitted contract is what it always was: nothing was inferred and nothing could be.
        const string script = "CREATE PROCEDURE dbo.X @Sku varchar AS BEGIN SELECT 1 END";

        var attributed = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@Sku", "varchar")] string sku, CancellationToken ct = default);
            }
            """, ("/x/X.sql", script));

        AssertCompiles(attributed);
        AssertNoDiagnostics(attributed);

        // For a parameter the script is driving it still fires: there the missing length is why the
        // emitted spec has none, and SQL Server reads that procedure as varchar(1).
        var scriptDriven = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync(string sku, CancellationToken ct = default);
            }
            """, ("/x/X.sql", script));

        AssertCompiles(scriptDriven);
        Assert.Equal(1, scriptDriven.CountOf("SPG012"));
    }

    [Fact]
    public void Spg015_IsSilentForAMethodThatNeededNothingFromEitherScript()
    {
        // Two files declaring one procedure cost a fully stated declaration nothing: no facts were going
        // to be taken from either. SPG015 answers "why did the script not supply this?", so it waits
        // until something actually went unsupplied - exactly as SPG014 does, and never both at once.
        var duplicated = new[]
        {
            ("/x/a.sql", "CREATE PROCEDURE dbo.X @Sku varchar(32) AS BEGIN SELECT 1 END"),
            ("/x/b.sql", "CREATE PROCEDURE dbo.X @Sku varchar(64) AS BEGIN SELECT 1 END"),
        };

        var attributed = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@Sku", "varchar", 32)] string sku, CancellationToken ct = default);
            }
            """, duplicated);

        AssertCompiles(attributed);
        AssertNoDiagnostics(attributed);
        Assert.Contains("__Contract_XAsync =", attributed.GeneratedText);

        // A method that did need the facts is still told why it did not get them.
        var scriptDriven = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync(string sku, CancellationToken ct = default);
            }
            """, duplicated);

        Assert.Equal(1, scriptDriven.CountOf("SPG015"));
        Assert.False(scriptDriven.HasGeneratorDiagnostic("SPG014"), scriptDriven.DiagnosticSummary());
    }

    // ---------------------------------------------------------------- what the gate measures

    [Theory]
    [InlineData("[Sql(\"@Sku\", \"varchar\", 32)]", "varchar(32)")]
    [InlineData("[Sql(\"@Sku\", 32)]", "varchar(32)")]
    [InlineData("[Sql(\"@Sku\", \"varchar\")]", "varchar")]
    [InlineData("[Sql(\"@Sku\")]", "varchar")]
    public void AttributionStyleIsInvisibleWhenTheContractDoesNotMove(string attribute, string sqlType)
    {
        // The second review's defect 5. The gate used to ask where a facet came from, which is a question
        // the consumer cannot see the answer to: all four rows below emit the same contract they emitted
        // before the file was promoted, and the same contract as each other for a given script - yet the
        // rows that leave the type to the file opened the SPG011/SPG012 gate and the rows that spell it
        // out did not. Two spellings of one contract produced different build logs, and the developer had
        // no way to tell from their own code which spelling was the loud one.
        //
        // Rows 1 and 3 are the controls: they were silent before this change and must stay silent. Rows 2
        // and 4 are the defect, and they fail this test against the shipped gate - row 2 with an SPG011
        // for the DEFAULTed @Extra, row 4 with that and an SPG012 for the bare varchar as well.
        var source = Usings + $$"""

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync({{attribute}} string sku, CancellationToken ct = default);
            }
            """;

        var withScript = GeneratorTestHarness.Run(
            source, ("/x/X.sql", $"CREATE PROCEDURE dbo.X @Sku {sqlType}, @Extra int = 0 AS BEGIN SELECT 1 END"));
        var withoutScript = GeneratorTestHarness.Run(source);

        AssertCompiles(withScript);
        AssertNoDiagnostics(withScript);

        // And silent because nothing moved, not because something was muted: the emitted text is the text
        // the same declaration produces with no script in the compilation at all.
        Assert.Equal(Norm(withoutScript.GeneratedText), Norm(withScript.GeneratedText));
    }

    [Fact]
    public void AFillThatDoesMoveTheContract_StillOpensTheGate()
    {
        // The gate is a measurement, not a mute button. The same script and the same @Extra as above, but
        // the attribute states no length, so the contract really does move - and the moment it does, the
        // parameter the method is missing is the next thing that will fail, at startup, on
        // dbParams.Count != contractParams.Count.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@Sku")] string sku, CancellationToken ct = default);
            }
            """, ("/x/X.sql", "CREATE PROCEDURE dbo.X @Sku varchar(32), @Extra int = 0 AS BEGIN SELECT 1 END"));

        AssertCompiles(result);
        Assert.Contains($"ProcParamSpec(\"@Sku\", new {Contracts}.SqlTypeSpec(\"varchar\", 32))", result.GeneratedText);
        Assert.Equal(1, result.CountOf("SPG017"));
        Assert.Equal(1, result.CountOf("SPG011"));
    }

    // ---------------------------------------------------------------- SPG018's remedy

    [Fact]
    public void Spg018_NeverSuggestsStatingTheDirection()
    {
        // The review followed SPG018's own advice and wrote [Sql("@b", Output = false)]. The result is a
        // contract StoredProcedureValidator rejects at startup on the output-flag mismatch, plus an
        // SPG010 that no [Sql] spelling can silence - so the message was steering developers from a build
        // error they could fix into a boot failure they could not.
        const string script = "CREATE PROCEDURE dbo.X @a int, @b int OUTPUT AS BEGIN SELECT 1 END";

        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync(int a, int b, CancellationToken ct = default);
            }
            """, ("/x/X.sql", script));

        Assert.Equal(1, result.CountOf("SPG018"));
        Assert.All(result.WithId("SPG018"), d => Assert.DoesNotContain("Output = false", d.GetMessage()));
        Assert.All(result.WithId("SPG018"), d => Assert.Contains("tuple", d.GetMessage()));
        Assert.All(result.WithId("SPG018"), d => Assert.Contains("change the procedure", d.GetMessage()));

        // The remedy it does name works: give the method somewhere to put the value and the same script
        // and the same bare parameters resolve with nothing left to report.
        var tupled = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<(int ReturnValue, int B)> XAsync(int a, int b, CancellationToken ct = default);
            }
            """, ("/x/X.sql", script));

        AssertCompiles(tupled);
        AssertNoDiagnostics(tupled);
        Assert.Contains($"ProcParamSpec(\"@b\", new {Contracts}.SqlTypeSpec(\"int\"), true)", tupled.GeneratedText);

        // And the remedy it used to name does not. Stating Output = false silences SPG018 by making the
        // parameter attributed, and buys a contract that binds an input against an OUTPUT parameter: the
        // build goes green, the application throws on the first validated startup.
        var stated = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@a", "int")] int a, [Sql("@b", "int", Output = false)] int b, CancellationToken ct = default);
            }
            """, ("/x/X.sql", script));

        Assert.False(stated.HasGeneratorDiagnostic("SPG018"), stated.DiagnosticSummary());
        Assert.Equal(1, stated.CountOf("SPG010"));
        Assert.Contains($"ProcParamSpec(\"@b\", new {Contracts}.SqlTypeSpec(\"int\"))", stated.GeneratedText);
    }

    // ---------------------------------------------------------------- stated versus effective OUTPUT

    [Fact]
    public void Spg010_ReportsAnOutputDirectionTheAttributeStated_AndNotOneItLeftUnsaid()
    {
        // SPG010 compared the attribute's effective direction - Output ?? false - so every [Sql] on a
        // parameter the script declares OUTPUT was reported as declaring 'false', a word the developer had
        // not written, and writing it explicitly changed nothing. A diagnostic whose only two answers are
        // "write what it already claims you wrote" and "change your contract" is not actionable.
        const string script = "CREATE PROCEDURE dbo.X @Id int OUTPUT AS BEGIN SELECT 1 END";

        var unstated = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@Id", "int")] int id, CancellationToken ct = default);
            }
            """, ("/x/X.sql", script));

        AssertCompiles(unstated);
        Assert.False(unstated.HasGeneratorDiagnostic("SPG010"), unstated.DiagnosticSummary());

        // Silent about the contradiction it did not make, and loud about the one thing that is true: the
        // script's OUTPUT was declined, the parameter binds as an input, and that contract fails startup
        // validation on the output-flag mismatch. SPG020 says so, and its remedy is one the developer can
        // actually write.
        Assert.Equal(1, unstated.CountOf("SPG020"));
        Assert.All(unstated.WithId("SPG020"), d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.All(unstated.WithId("SPG020"), d => Assert.Contains("Output = true", d.GetMessage()));

        // Writing the direction out is a different statement and is reported differently: the attribute
        // now says something about OUTPUT, and what it says disagrees with the procedure.
        var stated = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@Id", "int", Output = false)] int id, CancellationToken ct = default);
            }
            """, ("/x/X.sql", script));

        AssertCompiles(stated);
        Assert.Equal(1, stated.CountOf("SPG010"));
        Assert.All(stated.WithId("SPG010"), d => Assert.Contains("OUTPUT", d.GetMessage()));
        Assert.False(stated.HasGeneratorDiagnostic("SPG020"), stated.DiagnosticSummary());

        // Agreeing with the procedure ends both, which is the proof that neither is a warning you are
        // stuck with: the emitted contract is the OUTPUT one and nothing is reported at all.
        var agreeing = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<(int ReturnValue, int Id)> XAsync([Sql("@Id", "int", Output = true)] int id, CancellationToken ct = default);
            }
            """, ("/x/X.sql", script));

        AssertCompiles(agreeing);
        AssertNoDiagnostics(agreeing);
        Assert.Contains($"ProcParamSpec(\"@Id\", new {Contracts}.SqlTypeSpec(\"int\"), true)", agreeing.GeneratedText);
    }

    // ---------------------------------------------------------------- a declined type an attribute hid

    [Fact]
    public void AttributedParameter_IsToldWhenItKeptAGuessTheScriptRefusedToSupply()
    {
        // SPG016 is scoped to a parameter carrying no [Sql] at all, because that scope is the only one in
        // which an error is safe - and it is therefore a scope that reaches none of a codebase with [Sql]
        // on every parameter. For that codebase this was total silence: [Sql("@Rows")] against a
        // table-valued parameter emitted SqlTypeSpec("varchar"), which the runtime cannot bind to a TVP
        // under any circumstances, with nothing said at build time at all.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@Rows")] string rows, CancellationToken ct = default);
            }
            """, ("/x/X.sql", "CREATE PROCEDURE dbo.X @Rows dbo.IdList READONLY AS BEGIN SELECT 1 END"));

        AssertCompiles(result);

        // The contract is unchanged - that is what keeps this a warning rather than SPG016's error - and
        // the report names the construct, the guess it kept, and where each came from.
        Assert.Contains($"ProcParamSpec(\"@Rows\", new {Contracts}.SqlTypeSpec(\"varchar\"))", result.GeneratedText);
        Assert.False(result.HasGeneratorDiagnostic("SPG016"), result.DiagnosticSummary());

        Assert.Equal(1, result.CountOf("SPG019"));
        Assert.All(result.WithId("SPG019"), d => Assert.Equal(DiagnosticSeverity.Warning, d.Severity));
        Assert.All(result.WithId("SPG019"), d => Assert.Contains("table-valued", d.GetMessage()));
        Assert.All(result.WithId("SPG019"), d => Assert.Contains("varchar", d.GetMessage()));
    }

    [Fact]
    public void AttributedParameter_ThatStatesItsOwnTypeIsNeverToldAboutTheDeclinedOne()
    {
        // The boundary. SPG019 reports a guess that was kept, so a developer who wrote the type has
        // nothing to be told: they may know something about the procedure that the file in front of the
        // compiler does not say, which is the same rule SPG013 follows.
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@Name", "nvarchar", 128)] string name, CancellationToken ct = default);
            }
            """, ("/x/X.sql", "CREATE PROCEDURE dbo.X @Name sysname AS BEGIN SELECT 1 END"));

        AssertCompiles(result);
        AssertNoDiagnostics(result);
        Assert.Contains($"ProcParamSpec(\"@Name\", new {Contracts}.SqlTypeSpec(\"nvarchar\", 128))", result.GeneratedText);
    }

    // ---------------------------------------------------------------- the attribute's own documentation

    [Fact]
    public void SqlAttributeLengthDoc_DescribesWhatANegativeLengthActuallyDoes()
    {
        // The doc shipped into every consumer's compilation said "-1 is MAX" while the resolver strips a
        // stated -1 as unstated. The behaviour is the correct one - it is what keeps a declaration that
        // compiles today emitting byte-for-byte what it always emitted - so the doc is what was wrong,
        // and it was wrong in the worst direction: it invited exactly the write that silently does
        // nothing. The two tests at the top of this file pin the behaviour; this pins the sentence.
        Assert.DoesNotContain("-1 is MAX", EmittedAttributes.Source);
        Assert.Contains("A negative value is read as unstated", EmittedAttributes.Source);
        Assert.Contains("varchar(max)", EmittedAttributes.Source);
    }
}
