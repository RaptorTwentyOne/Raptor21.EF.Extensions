using Xunit;

namespace Raptor21.EF.Extensions.Migrations.Tests;

/// <summary>
/// Pins the serialized form of a full-text declaration — it is what the model snapshot carries, so a change to
/// it is a change every consumer's snapshot has to survive — and the equality rule of a resolved definition.
/// </summary>
public class FullTextDefinitionTests
{
    [Fact]
    public void Serialize_WritesTheVersionCatalogKeyTrackingAndColumns()
    {
        var declaration = new FullTextIndexDeclaration(
            "ft_Account", [new FullTextColumn("Name", 1055), new FullTextColumn("Email")], "UX_Customers_Code", FullTextChangeTracking.Manual);

        Assert.Equal("v1|ft_Account|UX_Customers_Code|MANUAL|Name:1055,Email", declaration.Serialize());
    }

    [Fact]
    public void Serialize_LeavesTheKeyIndexEmptyForThePrimaryKey()
        => Assert.Equal("v1|ft||AUTO|Name", new FullTextIndexDeclaration("ft", [new FullTextColumn("Name")]).Serialize());

    [Fact]
    public void Parse_ReadsBackWhatSerializeWrote()
    {
        var original = new FullTextIndexDeclaration(
            "ft_Account", [new FullTextColumn("Name", 1055), new FullTextColumn("Email")], null, FullTextChangeTracking.Off);

        var parsed = FullTextIndexDeclaration.Parse(original.Serialize());

        Assert.Equal("ft_Account", parsed.Catalog);
        Assert.Null(parsed.KeyIndex);
        Assert.Equal(FullTextChangeTracking.Off, parsed.ChangeTracking);
        Assert.Equal([new FullTextColumn("Name", 1055), new FullTextColumn("Email")], parsed.Columns);
    }

    [Theory]
    [InlineData("v2|ft||AUTO|Name")]
    [InlineData("v1|ft|AUTO|Name")]
    [InlineData("v1|ft||SOMETIMES|Name")]
    [InlineData("v1|ft||AUTO|Name:tr")]
    public void Parse_RefusesWhatItCannotRead(string serialized)
        => Assert.Throws<FormatException>(() => FullTextIndexDeclaration.Parse(serialized));

    [Fact]
    public void Parse_RefusesAnEmptyColumnList()
        => Assert.Throws<ArgumentException>(() => FullTextIndexDeclaration.Parse("v1|ft||AUTO|"));

    [Theory]
    [InlineData("Na|me")]
    [InlineData("Na,me")]
    [InlineData("Na:me")]
    public void Column_RefusesANameThatContainsASeparator(string name)
        => Assert.Throws<ArgumentException>(() => new FullTextColumn(name));

    [Fact]
    public void Declaration_RefusesAColumnListedTwice()
        => Assert.Throws<ArgumentException>(
            () => new FullTextIndexDeclaration("ft", [new FullTextColumn("Name"), new FullTextColumn("name")]));

    [Fact]
    public void Declaration_RefusesACatalogWithTheSeparator()
        => Assert.Throws<ArgumentException>(() => new FullTextIndexDeclaration("f|t", [new FullTextColumn("Name")]));

    [Fact]
    public void Definition_ComparesWhatTheEngineWouldBuild_NotTheSpelling()
    {
        // Column order and identifier casing do not change the index SQL Server builds, so they must not
        // scaffold a drop and a create.
        var a = new FullTextIndexDefinition("dbo", "Customers", "ft_Account", "PK_Customers", FullTextChangeTracking.Auto,
            [new FullTextColumn("Name"), new FullTextColumn("Email")]);
        var b = new FullTextIndexDefinition("DBO", "customers", "FT_ACCOUNT", "pk_customers", FullTextChangeTracking.Auto,
            [new FullTextColumn("EMAIL"), new FullTextColumn("name")]);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.Equal(a.Signature, b.Signature);
    }

    [Fact]
    public void Definition_ALanguageIsADifference()
    {
        var plain = new FullTextIndexDefinition("dbo", "Customers", "ft", "PK", FullTextChangeTracking.Auto, [new FullTextColumn("Name")]);
        var turkish = plain with { Columns = [new FullTextColumn("Name", 1055)] };

        Assert.NotEqual(plain, turkish);
        Assert.NotEqual(plain.Signature, turkish.Signature);
    }
}
