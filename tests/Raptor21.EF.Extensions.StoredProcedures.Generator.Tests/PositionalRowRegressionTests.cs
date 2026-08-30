using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator.Tests;

/// <summary>
/// The pre-existing defect the entity binding had to fix before it could emit anything realistic.
/// </summary>
/// <remarks>
/// <para>
/// <c>BuildReadExpression</c> built the <c>ColumnSpec</c>'s <c>typeof()</c> text from the display format
/// that carries the nullable-reference modifier, so <c>[SqlRow] record R(string? Name)</c> emitted
/// <c>typeof(string?)</c> into a <c>#nullable enable</c> file: CS8639, "the typeof operator cannot be
/// used on a nullable reference type". The fix takes that text from the non-nullable format, which
/// already existed for exactly this class of illegality.
/// </para>
/// <para>
/// It is the one place in this change where emitted text moves for a declaration nobody had to opt into,
/// and it moves only for declarations that did not compile: the two formats are identical for every
/// value type and for an unannotated reference type, so the only text that can move is <c>string?</c> to
/// <c>string</c>. Every EF reference-type property lacking <c>IsRequired()</c> lands on that path, which
/// is why the first realistic entity-bound row was otherwise uncompilable and why this had to land
/// first.
/// </para>
/// <para>
/// It lives in its own file because <c>StoredProcedureGeneratorTests</c> - the original twelve - is left
/// byte for byte as it is.
/// </para>
/// </remarks>
public class PositionalRowRegressionTests
{
    [Fact]
    public void NullableReferenceRowMemberEmitsTypeofWithoutTheAnnotation()
    {
        var result = GeneratorTestHarness.Run("""
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Raptor21.EF.Extensions.StoredProcedures.Generated;

            namespace Demo;

            [SqlRow]
            public partial record ProductRow(int Id, string? Name, decimal? Price);

            [StoredProcedureGroup]
            public partial class Procs
            {
                [StoredProcedure("dbo.Product_List")]
                public partial Task<IReadOnlyList<ProductRow>> ListAsync(CancellationToken ct = default);
            }
            """);

        // The output compilation is what proves it: CS8639 is an error in the generated class file, so a
        // regression here fails as a compile error rather than as a text mismatch.
        Assert.True(result.OutputErrors.IsDefaultOrEmpty, result.OutputErrorSummary());
        Assert.True(result.GeneratorDiagnostics.IsDefaultOrEmpty, result.DiagnosticSummary());

        Assert.Contains("ColumnSpec(\"Name\", typeof(string))", result.GeneratedText);
        Assert.DoesNotContain("typeof(string?)", result.GeneratedText);

        // A nullable VALUE type keeps its '?' in both places - typeof(decimal?) is legal and the cast in
        // the read expression requires it - so the fix is not a blanket strip.
        Assert.Contains("ColumnSpec(\"Price\", typeof(decimal?))", result.GeneratedText);
        Assert.Contains("(decimal?)null", result.GeneratedText);
    }
}
