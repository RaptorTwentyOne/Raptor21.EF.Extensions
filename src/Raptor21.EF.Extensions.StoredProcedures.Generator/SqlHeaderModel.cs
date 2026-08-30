using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator;

// The value-equatable models the .sql branch of the pipeline carries. Everything here holds strings,
// integers and other value-equatable models - never an AdditionalText, SourceText, Compilation,
// ISymbol, SyntaxNode or Location. Those compare by reference and would defeat the incremental cache,
// which is the whole reason a header-only parse can stop the pipeline dead on a body-only edit.
//
// This file is the home of the pipeline-side models too (SqlFileModel, ProcKey, SqlHeaderIndex), which
// sit at the bottom. Declare anything the .sql branch carries here rather than somewhere else.

/// <summary>A span inside a .sql file, carried as plain integers.</summary>
/// <remarks>
/// The parser cannot build a Roslyn <c>Location</c>: it has no <c>SourceText</c>, and a location is
/// reference-compared anyway. The resolver rebuilds one with
/// <c>Location.Create(path, TextSpan, LinePositionSpan)</c> from these four integers at report time.
/// </remarks>
internal readonly struct SourceSpanInfo : IEquatable<SourceSpanInfo>
{
    /// <summary>Creates a span.</summary>
    /// <param name="start">Absolute character offset of the first character.</param>
    /// <param name="length">Length in characters.</param>
    /// <param name="line">Zero-based line of <paramref name="start"/>.</param>
    /// <param name="column">Zero-based column of <paramref name="start"/>.</param>
    public SourceSpanInfo(int start, int length, int line, int column)
    {
        Start = start;
        Length = length;
        Line = line;
        Column = column;
    }

    /// <summary>Absolute character offset of the first character.</summary>
    public int Start { get; }

    /// <summary>Length in characters.</summary>
    public int Length { get; }

    /// <summary>Zero-based line of <see cref="Start"/> (LinePosition convention).</summary>
    public int Line { get; }

    /// <summary>Zero-based column of <see cref="Start"/> (LinePosition convention).</summary>
    public int Column { get; }

    /// <inheritdoc />
    public bool Equals(SourceSpanInfo other) =>
        Start == other.Start && Length == other.Length && Line == other.Line && Column == other.Column;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SourceSpanInfo other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = 17;
        hash = unchecked(hash * 31 + Start);
        hash = unchecked(hash * 31 + Length);
        hash = unchecked(hash * 31 + Line);
        hash = unchecked(hash * 31 + Column);
        return hash;
    }
}

/// <summary>What kind of parameter a header declared, and therefore whether facts may be taken from it.</summary>
internal enum SqlParamShape
{
    /// <summary>A plain scalar parameter. Only this shape can carry a type name.</summary>
    Ordinary,

    /// <summary>A table-valued parameter (<c>READONLY</c>). <c>ProcParamSpec</c> has no field for its type.</summary>
    TableValued,

    /// <summary>A <c>CURSOR</c> parameter. <c>ApplySqlType</c> has no path for it.</summary>
    Cursor,

    /// <summary>A schema-qualified or otherwise user-defined type. Parsed cleanly, then declined.</summary>
    UserDefined,
}

/// <summary>One parameter as the .sql file declares it. Facts only - the parser decides nothing.</summary>
internal readonly struct SqlHeaderParam : IEquatable<SqlHeaderParam>
{
    private readonly string? _name;
    private readonly string? _typeName;

    /// <summary>Creates a parameter fact set.</summary>
    public SqlHeaderParam(
        string name,
        string typeName,
        int? length,
        byte? precision,
        byte? scale,
        bool isOutput,
        bool hasDefault,
        bool hasNoLengthArgument,
        SqlParamShape shape,
        SourceSpanInfo span)
    {
        _name = name;
        _typeName = typeName;
        Length = length;
        Precision = precision;
        Scale = scale;
        IsOutput = isOutput;
        HasDefault = hasDefault;
        HasNoLengthArgument = hasNoLengthArgument;
        Shape = shape;
        Span = span;
    }

    /// <summary>The file's exact spelling, including the leading at-sign.</summary>
    public string Name => _name ?? "";

    /// <summary>
    /// Normalised, lower-case, unbracketed type name; <c>""</c> whenever the parser declines to supply
    /// a type - a non-<see cref="SqlParamShape.Ordinary"/> shape, or a type it deliberately does not
    /// model (<c>sysname</c>, <c>timestamp</c>, <c>sql_variant</c>, the spatial types, anything
    /// unknown). An empty type name means "the file said nothing", never "the file said this".
    /// </summary>
    public string TypeName => _typeName ?? "";

    /// <summary>Length for the char/binary family only; <c>-1</c> for <c>MAX</c>; null when the file gave none.</summary>
    public int? Length { get; }

    /// <summary>Precision for <c>decimal</c>/<c>numeric</c> only.</summary>
    public byte? Precision { get; }

    /// <summary>Scale for <c>decimal</c>/<c>numeric</c> only.</summary>
    public byte? Scale { get; }

    /// <summary>The file marked the parameter <c>OUT</c> or <c>OUTPUT</c>.</summary>
    public bool IsOutput { get; }

    /// <summary>The file gave the parameter a default. The expression itself is never evaluated.</summary>
    public bool HasDefault { get; }

    /// <summary>
    /// A char/binary-family type written with no length argument. SQL Server reads a bare
    /// <c>varchar</c> as <c>varchar(1)</c>; recording that would truncate every value to one character
    /// at the server and still pass startup validation, so no length is inferred and this flag drives
    /// a warning instead.
    /// </summary>
    public bool HasNoLengthArgument { get; }

    /// <summary>Whether facts may be taken from this parameter at all.</summary>
    public SqlParamShape Shape { get; }

    /// <summary>Where the parameter sits in the .sql file.</summary>
    public SourceSpanInfo Span { get; }

    /// <inheritdoc />
    public bool Equals(SqlHeaderParam other) =>
        string.Equals(Name, other.Name, StringComparison.Ordinal)
        && string.Equals(TypeName, other.TypeName, StringComparison.Ordinal)
        && Length == other.Length
        && Precision == other.Precision
        && Scale == other.Scale
        && IsOutput == other.IsOutput
        && HasDefault == other.HasDefault
        && HasNoLengthArgument == other.HasNoLengthArgument
        && Shape == other.Shape
        && Span.Equals(other.Span);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SqlHeaderParam other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = 17;
        hash = unchecked(hash * 31 + Name.GetHashCode());
        hash = unchecked(hash * 31 + TypeName.GetHashCode());
        hash = unchecked(hash * 31 + Length.GetHashCode());
        hash = unchecked(hash * 31 + Precision.GetHashCode());
        hash = unchecked(hash * 31 + Scale.GetHashCode());
        hash = unchecked(hash * 31 + IsOutput.GetHashCode());
        hash = unchecked(hash * 31 + HasDefault.GetHashCode());
        hash = unchecked(hash * 31 + HasNoLengthArgument.GetHashCode());
        hash = unchecked(hash * 31 + (int)Shape);
        hash = unchecked(hash * 31 + Span.GetHashCode());
        return hash;
    }
}

/// <summary>One <c>CREATE</c>/<c>ALTER PROCEDURE</c> header found in a .sql file.</summary>
internal readonly struct SqlProcHeader : IEquatable<SqlProcHeader>
{
    private readonly string? _filePath;
    private readonly string? _schema;
    private readonly string? _name;

    /// <summary>Creates a header fact set.</summary>
    public SqlProcHeader(
        string filePath,
        string schema,
        string name,
        EquatableArray<SqlHeaderParam> parameters,
        SourceSpanInfo span)
    {
        _filePath = filePath;
        _schema = schema;
        _name = name;
        Parameters = parameters;
        Span = span;
    }

    /// <summary>The <c>AdditionalText.Path</c> the header was read from, verbatim.</summary>
    public string FilePath => _filePath ?? "";

    /// <summary>Unbracketed schema; <c>dbo</c> when the file omitted it.</summary>
    public string Schema => _schema ?? "";

    /// <summary>Unbracketed procedure name.</summary>
    public string Name => _name ?? "";

    /// <summary>The parameters, in the order the file declares them.</summary>
    public EquatableArray<SqlHeaderParam> Parameters { get; }

    /// <summary>The header itself: from <c>CREATE</c>/<c>ALTER</c> through the body's <c>AS</c>.</summary>
    public SourceSpanInfo Span { get; }

    /// <inheritdoc />
    public bool Equals(SqlProcHeader other) =>
        string.Equals(FilePath, other.FilePath, StringComparison.Ordinal)
        && string.Equals(Schema, other.Schema, StringComparison.Ordinal)
        && string.Equals(Name, other.Name, StringComparison.Ordinal)
        && Parameters.Equals(other.Parameters)
        && Span.Equals(other.Span);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is SqlProcHeader other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        var hash = 17;
        hash = unchecked(hash * 31 + FilePath.GetHashCode());
        hash = unchecked(hash * 31 + Schema.GetHashCode());
        hash = unchecked(hash * 31 + Name.GetHashCode());
        hash = unchecked(hash * 31 + Parameters.GetHashCode());
        hash = unchecked(hash * 31 + Span.GetHashCode());
        return hash;
    }
}

/// <summary>Every procedure header one .sql file declares.</summary>
/// <remarks>
/// The <c>AdditionalText</c> and its <c>SourceText</c> are read and discarded inside the pipeline's
/// <c>Select</c>; only this model survives it. That is what makes an edit below a procedure's
/// <c>AS</c> a no-op: the model compares equal, and every step after it is served from the cache.
/// </remarks>
internal sealed record SqlFileModel(string FilePath, EquatableArray<SqlProcHeader> Headers);

/// <summary>The join key between a <c>[StoredProcedure("dbo.X")]</c> declaration and a .sql header.</summary>
/// <remarks>
/// Binding is by procedure identity, never by file name. Both sides build the key through
/// <see cref="From"/> - one function, so there is no comparer for a caller to forget. T-SQL identifiers
/// are case-insensitive under the default collation, so a case-only difference joins and is not a
/// diagnostic; <c>ToUpperInvariant</c> rather than <c>ToLowerInvariant</c> for the Turkish-I hazard
/// <c>ApplySqlType</c> already documents.
/// </remarks>
internal readonly record struct ProcKey(string Value)
{
    /// <summary>Builds the key from an unbracketed schema and name.</summary>
    public static ProcKey From(string schema, string name) =>
        new ProcKey((schema ?? "").ToUpperInvariant() + "." + (name ?? "").ToUpperInvariant());
}

/// <summary>The two files that declare one procedure identity, and where the second declaration sits.</summary>
/// <remarks>Cache payload only - never part of <see cref="SqlHeaderIndex"/>'s equality.</remarks>
internal readonly struct SqlDuplicateProc
{
    /// <summary>Creates a duplicate record.</summary>
    public SqlDuplicateProc(string firstPath, string secondPath, SourceSpanInfo secondSpan)
    {
        FirstPath = firstPath;
        SecondPath = secondPath;
        SecondSpan = secondSpan;
    }

    /// <summary>The file whose header was seen first.</summary>
    public string FirstPath { get; }

    /// <summary>The file whose header collided with it (possibly the same file).</summary>
    public string SecondPath { get; }

    /// <summary>Where the colliding header sits, for the SPG015 location.</summary>
    public SourceSpanInfo SecondSpan { get; }
}

/// <summary>Every parsed header in the compilation, indexed by procedure identity.</summary>
/// <remarks>
/// The lookup dictionaries are built once, in the constructor: a resolver that scanned the header list
/// per method would be O(n*m) on a corpus of several hundred scripts. They are a cache and never the
/// equality basis - <see cref="Equals(SqlHeaderIndex)"/> delegates to the header array built in the same
/// pass, so two indexes over the same headers compare equal however their dictionaries were populated.
/// <para>
/// The index answers questions and reports nothing. Every .sql-derived diagnostic is attached to a
/// method during resolution, which is what keeps a script that parses badly, declares no procedure, or
/// matches no declaration completely silent.
/// </para>
/// </remarks>
internal sealed class SqlHeaderIndex : IEquatable<SqlHeaderIndex>
{
    private readonly Dictionary<ProcKey, SqlProcHeader> _byKey;
    private readonly Dictionary<ProcKey, SqlDuplicateProc> _duplicates;
    private readonly EquatableArray<SqlProcHeader> _headers;

    /// <summary>Builds the index, deduplicating files by path and recording ambiguous identities.</summary>
    /// <param name="files">The parsed files, in the order the compiler supplied them.</param>
    public SqlHeaderIndex(ImmutableArray<SqlFileModel> files)
    {
        _byKey = new Dictionary<ProcKey, SqlProcHeader>();
        _duplicates = new Dictionary<ProcKey, SqlDuplicateProc>();
        var headers = new List<SqlProcHeader>();

        if (!files.IsDefaultOrEmpty)
        {
            // The same path can arrive twice - a consumer who declares their own AdditionalFiles beside
            // the ones the targets file derives, for instance. Reading it twice would make every
            // procedure in it ambiguous with itself.
            var seenPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                if (file is null || !seenPaths.Add(file.FilePath))
                {
                    continue;
                }

                foreach (var header in file.Headers.AsArray())
                {
                    headers.Add(header);

                    var key = ProcKey.From(header.Schema, header.Name);
                    if (_duplicates.ContainsKey(key))
                    {
                        // Already ambiguous. The first two files are the ones worth naming.
                        continue;
                    }

                    if (_byKey.TryGetValue(key, out var first))
                    {
                        _duplicates.Add(key, new SqlDuplicateProc(first.FilePath, header.FilePath, header.Span));
                        continue;
                    }

                    _byKey.Add(key, header);
                }
            }
        }

        _headers = EquatableArray<SqlProcHeader>.From(headers);
    }

    /// <summary>An index over no files at all - the state every consumer who does not promote their .sql is in.</summary>
    public static SqlHeaderIndex Empty { get; } = new SqlHeaderIndex(ImmutableArray<SqlFileModel>.Empty);

    /// <summary>The unambiguous header for an identity, if exactly one file declares it.</summary>
    /// <remarks>
    /// Returns false for an ambiguous identity as well as an unknown one. Refusing to guess between two
    /// files is the honest answer; the consequence is that bare parameters for that procedure become
    /// SPG003, which tells the developer exactly what to fix.
    /// </remarks>
    public bool TryGetHeader(ProcKey key, out SqlProcHeader header)
    {
        if (_duplicates.ContainsKey(key))
        {
            header = default;
            return false;
        }

        return _byKey.TryGetValue(key, out header);
    }

    /// <summary>The two files that declare an identity, when more than one does.</summary>
    public bool TryGetDuplicate(ProcKey key, out SqlDuplicateProc duplicate) =>
        _duplicates.TryGetValue(key, out duplicate);

    /// <inheritdoc />
    public bool Equals(SqlHeaderIndex? other) => other is not null && _headers.Equals(other._headers);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as SqlHeaderIndex);

    /// <inheritdoc />
    public override int GetHashCode() => _headers.GetHashCode();
}
