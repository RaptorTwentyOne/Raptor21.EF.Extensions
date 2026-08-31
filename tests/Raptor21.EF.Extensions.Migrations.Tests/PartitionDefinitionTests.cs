using System.Globalization;
using Xunit;

namespace Raptor21.EF.Extensions.Migrations.Tests;

/// <summary>
/// Covers the serialized forms that travel through the model snapshot and the literal spellings the
/// differ compares as text. A definition that does not survive its own round trip would surface as a
/// SPLIT or MERGE nobody asked for, in the next migration, against a partition full of data.
/// </summary>
public class PartitionDefinitionTests
{
    [Fact]
    public void FunctionSerialize_IsTheDocumentedForm()
    {
        var function = new PartitionFunctionDefinition(
            "pf_EventsMonth", "datetime2(3)", PartitionRange.Right, ["'2026-08-01'", "'2026-09-01'"]);

        // Pinned as a string rather than round-tripped only, because this is what every checked-in snapshot
        // stores: a change to the spelling is a change to the format every consumer already has on disk.
        Assert.Equal("v1|datetime2(3)|RIGHT|'2026-08-01','2026-09-01'", function.Serialize());
    }

    [Fact]
    public void FunctionParse_RoundTrips_IncludingCommasAndQuotesInsideLiterals()
    {
        var original = new PartitionFunctionDefinition(
            "pf_Regions", "nvarchar(20)", PartitionRange.Left, ["'a,b'", "'it''s'", "'z'"]);

        var parsed = PartitionFunctionDefinition.Parse(original.Name, original.Serialize());

        Assert.Equal(original.Name, parsed.Name);
        Assert.Equal(original.SqlType, parsed.SqlType);
        Assert.Equal(original.Range, parsed.Range);
        Assert.Equal(original.Boundaries, parsed.Boundaries);
    }

    [Fact]
    public void FunctionParse_ReadsAnEmptyBoundaryList()
    {
        // FOR VALUES () is legal and yields one partition. The serialized form is "v1|int|LEFT|" with nothing
        // after the last separator, which must read back as zero boundaries rather than one empty one.
        var parsed = PartitionFunctionDefinition.Parse("pf", "v1|int|LEFT|");

        Assert.Empty(parsed.Boundaries);
        Assert.Equal(PartitionRange.Left, parsed.Range);
    }

    [Theory]
    [InlineData("v2|int|RIGHT|1")]
    [InlineData("int|RIGHT|1")]
    [InlineData("v1|int|UP|1")]
    [InlineData("")]
    public void FunctionParse_RefusesWhatItCannotRead(string serialized)
        => Assert.Throws<FormatException>(() => PartitionFunctionDefinition.Parse("pf", serialized));

    [Fact]
    public void FunctionParse_ErrorSpellsTheExpectedShape()
    {
        // "LEFT or RIGHT" rather than "LEFT|RIGHT": the serialized form has four fields, and an error whose
        // example reads as five sends the reader hunting for a field that does not exist.
        var ex = Assert.Throws<FormatException>(() => PartitionFunctionDefinition.Parse("pf", "int|RIGHT|1"));

        Assert.Contains("|LEFT or RIGHT|", ex.Message);
    }

    [Theory]
    [InlineData("1,5")]
    [InlineData("'unbalanced")]
    [InlineData(" ")]
    public void Function_RefusesABoundaryThatWouldNotSurviveTheSnapshot(string boundary)
        => Assert.Throws<ArgumentException>(
            () => new PartitionFunctionDefinition("pf", "int", PartitionRange.Right, [boundary]));

    [Fact]
    public void SchemeSerialize_IsTheDocumentedForm()
        => Assert.Equal("v1|pf_EventsMonth|PRIMARY", new PartitionSchemeDefinition("ps_Events", "pf_EventsMonth").Serialize());

    [Fact]
    public void SchemeParse_RoundTrips()
    {
        var original = new PartitionSchemeDefinition("ps_Events", "pf_EventsMonth", "FG_Events");

        var parsed = PartitionSchemeDefinition.Parse(original.Name, original.Serialize());

        Assert.Equal(original, parsed);
    }

    [Theory]
    [InlineData("v2|pf|PRIMARY")]
    [InlineData("v1|pf")]
    [InlineData("v1|pf|PRIMARY|extra")]
    public void SchemeParse_RefusesWhatItCannotRead(string serialized)
        => Assert.Throws<FormatException>(() => PartitionSchemeDefinition.Parse("ps", serialized));

    [Fact]
    public void Scheme_RefusesTheSeparatorInsideAName()
        => Assert.Throws<ArgumentException>(() => new PartitionSchemeDefinition("ps", "pf|x"));

    [Fact]
    public void Monthly_StartsOnTheFirstOfTheGivenMonth()
    {
        // The day is ignored on purpose: "Monthly" means month boundaries, and a caller passing today's date
        // should get the first of the month rather than a boundary on the 15th.
        var boundaries = PartitionBoundaries.Monthly(new DateOnly(2026, 11, 15), 3);

        Assert.Equal(["'20261101'", "'20261201'", "'20270101'"], boundaries);
    }

    [Fact]
    public void Monthly_RefusesZeroMonths()
        => Assert.Throws<ArgumentOutOfRangeException>(() => PartitionBoundaries.Monthly(new DateOnly(2026, 1, 1), 0));

    public static TheoryData<object, string> Literals => new()
    {
        { "plain", "'plain'" },
        { "it's", "'it''s'" },
        { new DateOnly(2026, 8, 1), "'20260801'" },
        { new DateTime(2026, 8, 1, 13, 5, 7, 123), "'2026-08-01T13:05:07.123'" },
        { 10, "10" },
        { 10L, "10" },
        { (short)10, "10" },
        { (byte)10, "10" },
        { 1.5m, "1.5" },
    };

    [Theory]
    [MemberData(nameof(Literals))]
    public void Literal_SpellsEachSupportedType(object value, string expected)
        => Assert.Equal(expected, PartitionBoundaries.Literal(value));

    [Fact]
    public void Literal_IsCultureInvariant()
    {
        // A decimal boundary rendered under a comma-decimal culture would read as two boundaries, and a date
        // rendered under a d/M culture would be read back by SQL Server under whatever DATEFORMAT the
        // deploying session has. The literal must not depend on the machine that scaffolded the migration.
        var culture = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("tr-TR");
        try
        {
            Assert.Equal("1.5", PartitionBoundaries.Literal(1.5m));
            Assert.Equal("'20260801'", PartitionBoundaries.Literal(new DateOnly(2026, 8, 1)));
        }
        finally
        {
            CultureInfo.CurrentCulture = culture;
        }
    }

    [Fact]
    public void Literal_RefusesATypeItCannotSpell()
        => Assert.Throws<ArgumentException>(() => PartitionBoundaries.Literal(Guid.NewGuid()));
}
