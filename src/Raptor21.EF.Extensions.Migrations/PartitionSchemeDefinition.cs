namespace Raptor21.EF.Extensions.Migrations;

/// <summary>
/// A partition scheme as declared on the model: its name, the partition function it maps, and the one
/// filegroup every partition lands on (<c>ALL TO ([filegroup])</c>).
/// </summary>
/// <param name="Name">The scheme name, unbracketed.</param>
/// <param name="FunctionName">The name of the partition function the scheme maps.</param>
/// <param name="Filegroup">The filegroup every partition is placed on; <c>PRIMARY</c> unless said otherwise.</param>
public sealed record PartitionSchemeDefinition(string Name, string FunctionName, string Filegroup = "PRIMARY")
{
    private const string Version = "v1";

    // Validated on construction and again on `with`, so no route produces a definition the serializer
    // cannot carry.
    private readonly string _name = Require(Name, nameof(Name));
    private readonly string _functionName = Require(FunctionName, nameof(FunctionName));
    private readonly string _filegroup = Require(Filegroup, nameof(Filegroup));

    /// <summary>The scheme name, unbracketed.</summary>
    public string Name
    {
        get => _name;
        init => _name = Require(value, nameof(Name));
    }

    /// <summary>The name of the partition function the scheme maps.</summary>
    public string FunctionName
    {
        get => _functionName;
        init => _functionName = Require(value, nameof(FunctionName));
    }

    /// <summary>The filegroup every partition is placed on.</summary>
    public string Filegroup
    {
        get => _filegroup;
        init => _filegroup = Require(value, nameof(Filegroup));
    }

    /// <summary>
    /// Renders the definition as the single string stored under
    /// <see cref="CodeFirstAnnotations.PartitionSchemePrefix"/>: <c>v1|pf_EventsMonth|PRIMARY</c>.
    /// </summary>
    public string Serialize() => $"{Version}|{FunctionName}|{Filegroup}";

    /// <summary>Reads a definition back from the form <see cref="Serialize"/> produced.</summary>
    /// <param name="name">The scheme name, taken from the annotation key.</param>
    /// <param name="serialized">The annotation value.</param>
    /// <exception cref="FormatException">The value is not a <c>v1</c> partition scheme definition.</exception>
    public static PartitionSchemeDefinition Parse(string name, string serialized)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(serialized);

        var parts = serialized.Split('|');
        if (parts.Length != 3 || parts[0] != Version)
            throw new FormatException(
                $"Partition scheme '{name}' has an annotation value this version cannot read: '{serialized}'. " +
                $"Expected '{Version}|<function name>|<filegroup>'.");

        return new PartitionSchemeDefinition(name, parts[1], parts[2]);
    }

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('|'))
            throw new ArgumentException($"'{value}' contains '|', which the serialized form uses as its separator.", parameterName);
        return value.Trim();
    }
}
