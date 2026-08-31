using Microsoft.EntityFrameworkCore.Metadata;

namespace Raptor21.EF.Extensions.Migrations;

/// <summary>
/// Every partition function and partition scheme a model declares, keyed by name. This is the input
/// <see cref="PartitionDiff.Compute"/> compares; <see cref="FromModel"/> reads it out of the model
/// annotations <see cref="CodeFirstModelBuilderExtensions.HasPartitionFunction(Microsoft.EntityFrameworkCore.ModelBuilder, string, string, PartitionRange, IEnumerable{string})"/>
/// and <see cref="CodeFirstModelBuilderExtensions.HasPartitionScheme"/> write.
/// </summary>
public sealed class PartitionLayout
{
    /// <summary>A layout with no functions and no schemes — what a model without declarations, or no model at all, reads as.</summary>
    public static PartitionLayout Empty { get; } = new([], []);

    /// <summary>Creates a layout from explicit definitions. Names are compared case-insensitively, as SQL Server compares them by default.</summary>
    public PartitionLayout(
        IEnumerable<PartitionFunctionDefinition> functions,
        IEnumerable<PartitionSchemeDefinition> schemes)
    {
        ArgumentNullException.ThrowIfNull(functions);
        ArgumentNullException.ThrowIfNull(schemes);

        var f = new Dictionary<string, PartitionFunctionDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var function in functions)
        {
            if (!f.TryAdd(function.Name, function))
                throw new ArgumentException($"Partition function '{function.Name}' is declared twice.", nameof(functions));
        }

        var s = new Dictionary<string, PartitionSchemeDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var scheme in schemes)
        {
            if (!s.TryAdd(scheme.Name, scheme))
                throw new ArgumentException($"Partition scheme '{scheme.Name}' is declared twice.", nameof(schemes));
        }

        Functions = f;
        Schemes = s;
    }

    /// <summary>Partition functions by name.</summary>
    public IReadOnlyDictionary<string, PartitionFunctionDefinition> Functions { get; }

    /// <summary>Partition schemes by name.</summary>
    public IReadOnlyDictionary<string, PartitionSchemeDefinition> Schemes { get; }

    /// <summary>
    /// Reads the declarations off a model's annotations. A <see langword="null"/> model — the snapshot of a
    /// project before its first migration — reads as <see cref="Empty"/>.
    /// </summary>
    public static PartitionLayout FromModel(IModel? model)
    {
        if (model is null)
            return Empty;

        var functions = new List<PartitionFunctionDefinition>();
        var schemes = new List<PartitionSchemeDefinition>();

        // Annotation keys compare ordinally, so one name in one casing is one annotation and the last
        // declaration wins; two CASINGS of one name are two annotations, which SQL Server would read as one
        // object. The layout constructor refuses that too, but with an ArgumentException aimed at a caller
        // building lists by hand — coming from a model it is a model error, and it is named as one here,
        // next to every other model validation the differ makes.
        foreach (var annotation in model.GetAnnotations())
        {
            if (annotation.Value is not string serialized)
                continue;

            if (annotation.Name.StartsWith(CodeFirstAnnotations.PartitionFunctionPrefix, StringComparison.Ordinal))
            {
                var function = PartitionFunctionDefinition.Parse(
                    annotation.Name[CodeFirstAnnotations.PartitionFunctionPrefix.Length..], serialized);
                if (functions.Any(f => string.Equals(f.Name, function.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(
                        $"Partition function '{function.Name}' is declared more than once, in different casings. " +
                        "SQL Server compares object names case-insensitively; keep one spelling of the name in " +
                        "HasPartitionFunction.");
                functions.Add(function);
            }
            else if (annotation.Name.StartsWith(CodeFirstAnnotations.PartitionSchemePrefix, StringComparison.Ordinal))
            {
                var scheme = PartitionSchemeDefinition.Parse(
                    annotation.Name[CodeFirstAnnotations.PartitionSchemePrefix.Length..], serialized);
                if (schemes.Any(s => string.Equals(s.Name, scheme.Name, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException(
                        $"Partition scheme '{scheme.Name}' is declared more than once, in different casings. " +
                        "SQL Server compares object names case-insensitively; keep one spelling of the name in " +
                        "HasPartitionScheme.");
                schemes.Add(scheme);
            }
        }

        return functions.Count == 0 && schemes.Count == 0 ? Empty : new PartitionLayout(functions, schemes);
    }
}
