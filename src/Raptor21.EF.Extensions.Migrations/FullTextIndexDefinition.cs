namespace Raptor21.EF.Extensions.Migrations;

/// <summary>How SQL Server keeps a full-text index current, as <c>WITH CHANGE_TRACKING</c> spells it.</summary>
public enum FullTextChangeTracking
{
    /// <summary>The engine propagates every change as it happens — the default, and the only mode that needs no operator.</summary>
    Auto,

    /// <summary>Changes are tracked and applied when <c>ALTER FULLTEXT INDEX ... START UPDATE POPULATION</c> is run.</summary>
    Manual,

    /// <summary>Changes are not tracked; the index is repopulated only by a full population started by hand.</summary>
    Off,
}

/// <summary>
/// One column of a full-text index. On the model <see cref="Name"/> is the <em>property</em> name, which is
/// what the snapshot can express; once <see cref="CodeFirstTableLayout"/> has resolved it against the table
/// it is the column name. <see cref="Language"/> is the word-breaker language as an LCID (<c>1055</c> for
/// Turkish, <c>1033</c> for English) — <see langword="null"/> leaves the server's default language.
/// </summary>
public sealed record FullTextColumn
{
    private readonly string _name = string.Empty;

    /// <summary>Creates a column with its language, if any.</summary>
    public FullTextColumn(string name, int? language = null)
    {
        Name = name;
        Language = language;
    }

    /// <summary>The property name on the model, the column name once resolved. Trimmed; may not contain <c>|</c>, <c>,</c> or <c>:</c>.</summary>
    public string Name
    {
        get => _name;
        init
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(value, nameof(Name));
            if (value.AsSpan().IndexOfAny("|,:") >= 0)
                throw new ArgumentException($"'{value}' contains one of '|', ',' or ':', which the serialized form uses as separators.", nameof(Name));
            _name = value.Trim();
        }
    }

    /// <summary>The word-breaker language as an LCID, or <see langword="null"/> for the server default.</summary>
    public int? Language { get; init; }

    /// <summary><c>Name</c> or <c>Name:1055</c> — the form the entity annotation carries.</summary>
    public override string ToString() => Language is null ? Name : $"{Name}:{Language.Value}";

    /// <summary>Reads a column back from the form <see cref="ToString"/> produced.</summary>
    /// <exception cref="FormatException">The text is not <c>name</c> or <c>name:lcid</c>.</exception>
    public static FullTextColumn Parse(string text)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);

        var colon = text.IndexOf(':');
        if (colon < 0)
            return new FullTextColumn(text);

        if (!int.TryParse(text.AsSpan(colon + 1), System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var lcid))
            throw new FormatException($"Full-text column '{text}' does not end in an LCID after its ':'.");

        return new FullTextColumn(text[..colon], lcid);
    }
}

/// <summary>
/// A full-text index as an entity declares it: the catalog it lives in, the properties it indexes, the unique
/// index it is keyed on (<see langword="null"/> for the table's primary key) and its change tracking. This is
/// the value stored under <see cref="CodeFirstAnnotations.FullTextIndex"/>; <see cref="CodeFirstTableLayout"/>
/// resolves it into a <see cref="FullTextIndexDefinition"/> once the table is in hand.
/// </summary>
public sealed record FullTextIndexDeclaration
{
    private const string Version = "v1";

    private readonly string _catalog = string.Empty;
    private readonly string? _keyIndex;
    private readonly IReadOnlyList<FullTextColumn> _columns = [];

    /// <summary>Creates a declaration.</summary>
    /// <param name="catalog">The full-text catalog, declared with <see cref="CodeFirstModelBuilderExtensions.HasFullTextCatalog"/>.</param>
    /// <param name="columns">The properties to index, at least one.</param>
    /// <param name="keyIndex">The name of the unique, single-column index the full-text index is keyed on; <see langword="null"/> for the primary key.</param>
    /// <param name="changeTracking">How the index is kept current.</param>
    public FullTextIndexDeclaration(
        string catalog,
        IReadOnlyList<FullTextColumn> columns,
        string? keyIndex = null,
        FullTextChangeTracking changeTracking = FullTextChangeTracking.Auto)
    {
        Catalog = catalog;
        Columns = columns;
        KeyIndex = keyIndex;
        ChangeTracking = changeTracking;
    }

    /// <summary>The catalog name, unbracketed.</summary>
    public string Catalog
    {
        get => _catalog;
        init => _catalog = Require(value, nameof(Catalog));
    }

    /// <summary>The name of the key index, or <see langword="null"/> for the primary key.</summary>
    public string? KeyIndex
    {
        get => _keyIndex;
        init => _keyIndex = value is null ? null : Require(value, nameof(KeyIndex));
    }

    /// <summary>The properties to index, in declaration order.</summary>
    public IReadOnlyList<FullTextColumn> Columns
    {
        get => _columns;
        init
        {
            ArgumentNullException.ThrowIfNull(value, nameof(Columns));
            if (value.Count == 0)
                throw new ArgumentException("A full-text index needs at least one column.", nameof(Columns));
            if (value.Select(c => c.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != value.Count)
                throw new ArgumentException("A full-text index lists each column once.", nameof(Columns));
            _columns = value.ToList();
        }
    }

    /// <summary>How the index is kept current.</summary>
    public FullTextChangeTracking ChangeTracking { get; init; }

    /// <summary>
    /// Renders the declaration as the single string stored under <see cref="CodeFirstAnnotations.FullTextIndex"/>:
    /// <c>v1|ft_Account|PK_Customers|AUTO|Name:1055,Email</c>, with an empty third field for the primary key.
    /// </summary>
    public string Serialize()
        => $"{Version}|{Catalog}|{KeyIndex}|{Spell(ChangeTracking)}|{string.Join(",", Columns)}";

    /// <summary>Reads a declaration back from the form <see cref="Serialize"/> produced.</summary>
    /// <param name="serialized">The annotation value.</param>
    /// <exception cref="FormatException">The value is not a <c>v1</c> full-text index declaration.</exception>
    public static FullTextIndexDeclaration Parse(string serialized)
    {
        ArgumentNullException.ThrowIfNull(serialized);

        var parts = serialized.Split('|');
        if (parts.Length != 5 || parts[0] != Version)
            throw new FormatException(
                $"A full-text index annotation has a value this version cannot read: '{serialized}'. " +
                $"Expected '{Version}|<catalog>|<key index or empty>|<AUTO|MANUAL|OFF>|<column[:lcid],...>'.");

        var tracking = parts[3] switch
        {
            "AUTO" => FullTextChangeTracking.Auto,
            "MANUAL" => FullTextChangeTracking.Manual,
            "OFF" => FullTextChangeTracking.Off,
            _ => throw new FormatException($"Full-text change tracking '{parts[3]}' is not AUTO, MANUAL or OFF."),
        };

        var columns = parts[4].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(FullTextColumn.Parse).ToList();

        return new FullTextIndexDeclaration(parts[1], columns, parts[2].Length == 0 ? null : parts[2], tracking);
    }

    /// <summary>The T-SQL spelling of a tracking mode.</summary>
    public static string Spell(FullTextChangeTracking changeTracking) => changeTracking switch
    {
        FullTextChangeTracking.Auto => "AUTO",
        FullTextChangeTracking.Manual => "MANUAL",
        FullTextChangeTracking.Off => "OFF",
        _ => throw new ArgumentOutOfRangeException(nameof(changeTracking), changeTracking, null),
    };

    private static string Require(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (value.Contains('|'))
            throw new ArgumentException($"'{value}' contains '|', which the serialized form uses as its separator.", parameterName);
        return value.Trim();
    }
}

/// <summary>
/// A full-text index resolved against its table: column names rather than property names, and the key index
/// by name. Two definitions are equal when they would produce the same <c>CREATE FULLTEXT INDEX</c> — column
/// order does not matter to the engine and does not matter here.
/// </summary>
/// <param name="Schema">The table's schema, or <see langword="null"/> when the connection's default applies.</param>
/// <param name="Table">The table name.</param>
/// <param name="Catalog">The full-text catalog the index lives in.</param>
/// <param name="KeyIndex">The unique, single-column index the full-text index is keyed on.</param>
/// <param name="ChangeTracking">How the index is kept current.</param>
/// <param name="Columns">The indexed columns, by column name.</param>
public sealed record FullTextIndexDefinition(
    string? Schema,
    string Table,
    string Catalog,
    string KeyIndex,
    FullTextChangeTracking ChangeTracking,
    IReadOnlyList<FullTextColumn> Columns)
{
    /// <summary>
    /// What the index IS, independent of where it sits: catalog, key index and change tracking, then the
    /// columns sorted by name. Identifiers compare case-insensitively, as SQL Server compares them by default.
    /// </summary>
    public string Signature
        => string.Join("|",
            Catalog.ToUpperInvariant(),
            KeyIndex.ToUpperInvariant(),
            FullTextIndexDeclaration.Spell(ChangeTracking),
            string.Join(",", Columns
                .OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(c => new FullTextColumn(c.Name.ToUpperInvariant(), c.Language).ToString())));

    /// <inheritdoc/>
    public bool Equals(FullTextIndexDefinition? other)
        => other is not null
           && string.Equals(Schema, other.Schema, StringComparison.OrdinalIgnoreCase)
           && string.Equals(Table, other.Table, StringComparison.OrdinalIgnoreCase)
           && string.Equals(Signature, other.Signature, StringComparison.Ordinal);

    /// <inheritdoc/>
    public override int GetHashCode()
        => HashCode.Combine(
            Schema?.ToUpperInvariant(),
            Table.ToUpperInvariant(),
            Signature);
}
