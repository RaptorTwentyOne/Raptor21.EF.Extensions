using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Raptor21.EF.Extensions.Migrations.Tests;

/// <summary>
/// Pins the three <c>HasPartitionFunction</c> overloads at the annotation they write: the
/// <c>IEnumerable&lt;string&gt;</c> and <c>string[]</c> overloads take T-SQL the caller already wrote, the
/// <c>params object[]</c> overload takes values and renders each through
/// <see cref="PartitionBoundaries.Literal"/> — a string is a value there and comes out quoted.
/// </summary>
public class CodeFirstModelBuilderExtensionsTests
{
    private static string? FunctionAnnotation(ModelBuilder modelBuilder, string name)
        => modelBuilder.Model.FindAnnotation(CodeFirstAnnotations.PartitionFunctionPrefix + name)?.Value as string;

    [Fact]
    public void StringArrayOverload_TakesEachLiteralVerbatim()
    {
        // A string[] must bind to the literal overload: through params object[] each string would be quoted
        // into a value, and "1" would land in FOR VALUES as '1' against an int function.
        var modelBuilder = new ModelBuilder();

        modelBuilder.HasPartitionFunction("pf", "int", PartitionRange.Right, new[] { "1", "2" });

        Assert.Equal("v1|int|RIGHT|1,2", FunctionAnnotation(modelBuilder, "pf"));
    }

    [Fact]
    public void ParamsOverload_RendersValuesThroughLiteral()
    {
        var modelBuilder = new ModelBuilder();

        modelBuilder.HasPartitionFunction(
            "pf", "date", PartitionRange.Right, new DateOnly(2026, 8, 1), new DateOnly(2026, 9, 1));

        Assert.Equal("v1|date|RIGHT|'20260801','20260901'", FunctionAnnotation(modelBuilder, "pf"));
    }

    [Fact]
    public void ParamsOverload_QuotesAStringAsAValue()
    {
        var modelBuilder = new ModelBuilder();

        modelBuilder.HasPartitionFunction("pf", "nvarchar(10)", PartitionRange.Left, "a", "b");

        Assert.Equal("v1|nvarchar(10)|LEFT|'a','b'", FunctionAnnotation(modelBuilder, "pf"));
    }

    [Fact]
    public void NoBoundaries_IsALegalOnePartitionFunction()
    {
        // FOR VALUES () is legal T-SQL and yields a single partition.
        var modelBuilder = new ModelBuilder();

        modelBuilder.HasPartitionFunction("pf", "int", PartitionRange.Right);

        Assert.Equal("v1|int|RIGHT|", FunctionAnnotation(modelBuilder, "pf"));
    }

    [Fact]
    public void SameNameDeclaredTwice_LastDeclarationWins()
    {
        // The name is the annotation key, so a second declaration replaces the first — the same last-wins
        // rule every HasAnnotation call follows.
        var modelBuilder = new ModelBuilder();

        modelBuilder.HasPartitionFunction("pf", "int", PartitionRange.Right, new[] { "1" });
        modelBuilder.HasPartitionFunction("pf", "int", PartitionRange.Right, new[] { "2" });

        Assert.Equal("v1|int|RIGHT|2", FunctionAnnotation(modelBuilder, "pf"));
    }
}
