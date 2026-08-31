using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator.Tests;

/// <summary>
/// The emitted body, pinned statement by statement for every return category.
/// </summary>
/// <remarks>
/// This is the checkable form of the claim the borrowing design rests on: a consumer who never borrows a
/// connection is not on a "default branch" that could drift from a "borrowing branch" — there is one
/// statement, it asks the provider a question, and nothing anywhere in the emitted code tests the answer.
/// If a later change makes the ordinary call site pay for the unit-of-work case, one of the assertions
/// below fires, and it fires as a diff a reviewer reads rather than as behaviour nobody notices.
///
/// <see cref="StoredProcedureGeneratorTests"/> stays as it was: it pins the executor method names and the
/// shape of the returns, and it is deliberately not edited here.
/// </remarks>
public class GeneratedBodyShapeTests
{
    private const string Usings = """
        using System.Collections.Generic;
        using System.Threading;
        using System.Threading.Tasks;
        using Raptor21.EF.Extensions.StoredProcedures.Generated;
        """;

    /// <summary>One method per return category — every shape <c>EmitMethod</c> can produce, in one group.</summary>
    private const string EveryCategory = Usings + """

        namespace Demo;

        [SqlRow]
        public partial record Row(int Id);

        [StoredProcedureGroup]
        public partial class P
        {
            [StoredProcedure("dbo.A")]
            public partial Task<int> AAsync([Sql("@Id", "int")] int id, CancellationToken ct = default);

            [StoredProcedure("dbo.B")]
            public partial Task<short> BAsync([Sql("@Id", "int")] int id, CancellationToken ct = default);

            [StoredProcedure("dbo.C")]
            public partial Task CAsync([Sql("@Id", "int")] int id, CancellationToken ct = default);

            [StoredProcedure("dbo.D")]
            public partial Task<IReadOnlyList<Row>> DAsync([Sql("@Id", "int")] int id, CancellationToken ct = default);

            [StoredProcedure("dbo.E")]
            public partial Task<(int ReturnValue, IReadOnlyList<Row> Rows)> EAsync([Sql("@Id", "int")] int id, CancellationToken ct = default);

            [StoredProcedure("dbo.F")]
            public partial Task<(int ReturnValue, int Out)> FAsync([Sql("@Id", "int")] int id, [Sql("@Out", "int", Output = true)] int outValue, CancellationToken ct = default);
        }
        """;

    /// <summary>The one connection statement, byte for byte, as every method emits it.</summary>
    private const string Prologue = "await using var __lease = await __connectionProvider.LeaseAsync(ct).ConfigureAwait(false);";

    [Fact]
    public void DefaultPath_HasExactlyOneConnectionStatementAndNoBranch()
    {
        var result = GeneratorTestHarness.Run(EveryCategory);

        Assert.True(result.OutputErrors.IsDefaultOrEmpty, result.OutputErrorSummary());

        // Six methods, six identical prologues: the return category decides the tail and nothing else.
        Assert.Equal(6, CountOccurrences(result.GeneratedText, Prologue));

        // Nothing else touches a connection, and nothing decides anything at run time. `__conn =` and
        // `(__conn,` rather than a bare `__conn`, because `__connectionProvider` contains that substring
        // and the field is still there — it is the local and the argument that had to go.
        Assert.DoesNotContain("__connectionProvider.Create()", result.GeneratedText);
        Assert.DoesNotContain("OpenAsync", result.GeneratedText);
        Assert.DoesNotContain("__conn =", result.GeneratedText);
        Assert.DoesNotContain("(__conn,", result.GeneratedText);

        // No type test and no ownership question anywhere in a body: the provider already answered both,
        // once, behind one virtual call.
        Assert.DoesNotContain("OwnsConnection", result.GeneratedText);
        Assert.DoesNotContain(" is global::Raptor21", result.GeneratedText);
    }

    [Theory]
    [InlineData("AAsync", "return await __executor.ExecuteReturnInt32Async(__lease, __Contract_AAsync, new object?[] { id }, ct).ConfigureAwait(false);")]
    [InlineData("BAsync", "return await __executor.ExecuteReturnInt16Async(__lease, __Contract_BAsync, new object?[] { id }, ct).ConfigureAwait(false);")]
    [InlineData("CAsync", "await __executor.ExecuteNonQueryAsync(__lease, __Contract_CAsync, new object?[] { id }, ct).ConfigureAwait(false);")]
    [InlineData("DAsync", "return (await __executor.ExecuteReturnResultSetAsync<global::Demo.Row>(__lease, __Contract_DAsync, new object?[] { id }, ct).ConfigureAwait(false)).Rows;")]
    [InlineData("EAsync", "return await __executor.ExecuteReturnResultSetAsync<global::Demo.Row>(__lease, __Contract_EAsync, new object?[] { id }, ct).ConfigureAwait(false);")]
    public void EachReturnCategory_EmitsItsExactBody(string method, string tail)
    {
        var result = GeneratorTestHarness.Run(EveryCategory);

        Assert.True(result.OutputErrors.IsDefaultOrEmpty, result.OutputErrorSummary());

        // Two statements and no more: the lease, then the executor call. The whole body is compared, so a
        // future statement slipped in beside them is a failure rather than an addition nobody sees.
        Assert.Equal(Prologue + "\n" + tail, BodyOf(result.GeneratedText, method));
    }

    [Fact]
    public void ReturnWithOutputs_EmitsItsExactBody()
    {
        var result = GeneratorTestHarness.Run(EveryCategory);

        Assert.True(result.OutputErrors.IsDefaultOrEmpty, result.OutputErrorSummary());

        // The one category whose tail is three statements rather than two, so it gets its own case: the
        // OUTPUT values are unpacked after the call and converted into the declared tuple.
        Assert.Equal(
            Prologue + "\n"
            + "var (__ret, __outs) = await __executor.ExecuteWithOutputsAsync(__lease, __Contract_FAsync, new object?[] { id, outValue }, ct).ConfigureAwait(false);\n"
            + "return (__ret, (int)__outs[0]!);",
            BodyOf(result.GeneratedText, "FAsync"));
    }

    [Fact]
    public void MethodWithoutACancellationToken_PassesDefaultToLeaseAsync()
    {
        var result = GeneratorTestHarness.Run(Usings + """

            namespace Demo;

            [StoredProcedureGroup]
            public partial class P
            {
                [StoredProcedure("dbo.X")]
                public partial Task<int> XAsync([Sql("@Id", "int")] int id);
            }
            """);

        Assert.True(result.OutputErrors.IsDefaultOrEmpty, result.OutputErrorSummary());

        // `ctArg` is the same substitution the two old statements already made, so the token-less method
        // reads exactly as it always did — with one call instead of two.
        Assert.Equal(
            "await using var __lease = await __connectionProvider.LeaseAsync(default).ConfigureAwait(false);\n"
            + "return await __executor.ExecuteReturnInt32Async(__lease, __Contract_XAsync, new object?[] { id }, default).ConfigureAwait(false);",
            BodyOf(result.GeneratedText, "XAsync"));
    }

    [Fact]
    public void GeneratedConstructor_CarriesADocComment()
    {
        var result = GeneratorTestHarness.Run(EveryCategory);

        Assert.True(result.OutputErrors.IsDefaultOrEmpty, result.OutputErrorSummary());

        // `// <auto-generated/>` does not suppress CS1591, and the constructor is the one generated member
        // that owes a comment — the partial methods' comments live on the developer's own declaring part.
        // Without this line a consumer building with GenerateDocumentationFile and no CS1591 suppression
        // collects one warning per group class, in a file they cannot edit.
        var lines = Lines(result.GeneratedText);
        var ctor = Array.FindIndex(lines, l => l.StartsWith("public P(", StringComparison.Ordinal));
        Assert.True(ctor > 0, "no generated constructor found");
        Assert.StartsWith("/// <summary>", lines[ctor - 1]);
    }

    /// <summary>The statements inside one emitted method, trimmed and newline-joined.</summary>
    /// <remarks>
    /// Indentation is dropped on purpose. The emitter indents by namespace depth, so pinning the leading
    /// whitespace would make an unrelated nesting change look like a body change — while the statements
    /// themselves, which are what this file is about, are compared in full.
    /// </remarks>
    private static string BodyOf(string generated, string methodName)
    {
        var lines = Lines(generated);
        var signature = Array.FindIndex(lines, l => l.Contains(" " + methodName + "(", StringComparison.Ordinal) && l.Contains("partial async", StringComparison.Ordinal));
        Assert.True(signature >= 0, $"no generated body for {methodName}");
        Assert.Equal("{", lines[signature + 1]);

        var body = new List<string>();
        for (var i = signature + 2; i < lines.Length && lines[i] != "}"; i++)
            body.Add(lines[i]);

        return string.Join("\n", body);
    }

    private static string[] Lines(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(l => l.Trim()).ToArray();

    private static int CountOccurrences(string text, string value)
    {
        var count = 0;
        for (var i = text.IndexOf(value, StringComparison.Ordinal); i >= 0; i = text.IndexOf(value, i + value.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}
