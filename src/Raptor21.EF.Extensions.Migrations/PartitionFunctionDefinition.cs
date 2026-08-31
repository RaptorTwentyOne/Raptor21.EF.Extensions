using System.Text;

namespace Raptor21.EF.Extensions.Migrations;

/// <summary>
/// A partition function as declared on the model: its name, the SQL type of its input parameter, the range
/// direction and the boundary values, each written as the T-SQL literal that will appear in
/// <c>FOR VALUES (...)</c>.
/// </summary>
/// <param name="Name">The function name, unbracketed.</param>
/// <param name="SqlType">The input parameter type exactly as T-SQL spells it, e.g. <c>datetime2(3)</c> or <c>int</c>.</param>
/// <param name="Range">Whether boundaries belong to the partition on their left or on their right.</param>
/// <param name="Boundaries">
/// Boundary values as T-SQL literals, in ascending order — <c>'2026-08-01'</c>, <c>100</c>. Compared as text
/// by the differ, so a literal that is rewritten without changing its value reads as a removed boundary and an
/// added one. <see cref="PartitionBoundaries"/> produces a stable spelling.
/// </param>
public sealed record PartitionFunctionDefinition(
    string Name,
    string SqlType,
    PartitionRange Range,
    IReadOnlyList<string> Boundaries)
{
    // The serialized form is what the model snapshot stores, so it is versioned: a later format can be
    // read next to this one instead of invalidating every snapshot already checked in.
    private const string Version = "v1";

    // Validated on construction and again on `with`, so no route produces a definition the serializer
    // cannot carry.
    private readonly string _name = Require(Name, nameof(Name));
    private readonly string _sqlType = Require(SqlType, nameof(SqlType));
    private readonly IReadOnlyList<string> _boundaries = RequireBoundaries(Boundaries);

    /// <summary>The function name, unbracketed.</summary>
    public string Name
    {
        get => _name;
        init => _name = Require(value, nameof(Name));
    }

    /// <summary>The input parameter type exactly as T-SQL spells it.</summary>
    public string SqlType
    {
        get => _sqlType;
        init => _sqlType = Require(value, nameof(SqlType));
    }

    /// <summary>Boundary values as T-SQL literals, in ascending order.</summary>
    public IReadOnlyList<string> Boundaries
    {
        get => _boundaries;
        init => _boundaries = RequireBoundaries(value);
    }

    /// <summary>
    /// Renders the definition as the single string stored under
    /// <see cref="CodeFirstAnnotations.PartitionFunctionPrefix"/>: <c>v1|datetime2(3)|RIGHT|'2026-08-01','2026-09-01'</c>.
    /// The name is the annotation key and is not repeated in the value.
    /// </summary>
    public string Serialize()
        => $"{Version}|{SqlType}|{(Range == PartitionRange.Right ? "RIGHT" : "LEFT")}|{string.Join(",", Boundaries)}";

    /// <summary>Reads a definition back from the form <see cref="Serialize"/> produced.</summary>
    /// <param name="name">The function name, taken from the annotation key.</param>
    /// <param name="serialized">The annotation value.</param>
    /// <exception cref="FormatException">The value is not a <c>v1</c> partition function definition.</exception>
    public static PartitionFunctionDefinition Parse(string name, string serialized)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(serialized);

        // At most four parts: the boundary list is last and may itself contain '|' inside a string literal.
        var parts = serialized.Split('|', 4);
        if (parts.Length != 4 || parts[0] != Version)
            throw new FormatException(
                $"Partition function '{name}' has an annotation value this version cannot read: '{serialized}'. " +
                $"Expected '{Version}|<sql type>|LEFT or RIGHT|<boundary literals>'.");

        var range = parts[2] switch
        {
            "RIGHT" => PartitionRange.Right,
            "LEFT" => PartitionRange.Left,
            _ => throw new FormatException(
                $"Partition function '{name}' declares range '{parts[2]}'; only LEFT and RIGHT exist."),
        };

        return new PartitionFunctionDefinition(name, parts[1], range, SplitBoundaries(parts[3]));
    }

    /// <summary>
    /// Splits a comma-separated boundary list on the commas that are outside string literals, so a
    /// <c>varchar</c> boundary written <c>'a,b'</c> survives the round trip. A doubled quote inside a literal
    /// is an escaped quote and does not close it.
    /// </summary>
    internal static IReadOnlyList<string> SplitBoundaries(string list)
    {
        if (list.Length == 0)
            return [];

        var result = new List<string>();
        var current = new StringBuilder();
        var inLiteral = false;

        for (var i = 0; i < list.Length; i++)
        {
            var c = list[i];
            if (c == '\'')
            {
                // Inside a literal, '' is one escaped quote: consume both and stay inside.
                if (inLiteral && i + 1 < list.Length && list[i + 1] == '\'')
                {
                    current.Append("''");
                    i++;
                    continue;
                }

                inLiteral = !inLiteral;
            }
            else if (c == ',' && !inLiteral)
            {
                result.Add(current.ToString().Trim());
                current.Clear();
                continue;
            }

            current.Append(c);
        }

        result.Add(current.ToString().Trim());
        return result;
    }

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        return value.Trim();
    }

    private static IReadOnlyList<string> RequireBoundaries(IReadOnlyList<string> boundaries)
    {
        ArgumentNullException.ThrowIfNull(boundaries);

        var copy = new string[boundaries.Count];
        for (var i = 0; i < copy.Length; i++)
        {
            var literal = boundaries[i];
            if (string.IsNullOrWhiteSpace(literal))
                throw new ArgumentException($"Boundary {i} is empty. Every boundary must be a T-SQL literal.", nameof(Boundaries));

            // An odd number of quotes is a literal that never closes. Alone it would round-trip, because the
            // splitter has nothing after it to swallow; next to another boundary it swallows the comma and
            // the two become one. It is refused in both cases, since the count of its neighbours should not
            // decide whether a boundary is valid.
            if (literal.Count(c => c == '\'') % 2 != 0)
                throw new ArgumentException($"Boundary '{literal}' has an unbalanced quote.", nameof(Boundaries));

            copy[i] = literal.Trim();
        }

        // The serialized form joins boundaries with commas and splits them on the commas outside string
        // literals. A literal the splitter cannot give back — a bare `1,5`, an unbalanced quote — would
        // round-trip through the snapshot as a different boundary list and surface as a SPLIT nobody asked
        // for, so it is refused here, where the caller can still see what they wrote.
        var roundTrip = SplitBoundaries(string.Join(",", copy));
        if (roundTrip.Count != copy.Length || !roundTrip.SequenceEqual(copy, StringComparer.Ordinal))
            throw new ArgumentException(
                $"Boundaries [{string.Join(", ", copy)}] do not survive serialization: a comma outside a string " +
                "literal or an unbalanced quote splits the list differently when it is read back from the snapshot.",
                nameof(Boundaries));

        return copy;
    }
}
