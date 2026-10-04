namespace Raptor21.EF.Extensions.Migrations;

/// <summary>
/// The T-SQL identifier rules the script headers are read with, so that the name a script declares is the
/// name SQL Server creates — and the name a <c>DROP ... IF EXISTS</c> later removes.
/// </summary>
/// <remarks>
/// A header used to be read with <c>\[?[A-Za-z0-9_]+\]?</c>, which stopped at the first character outside
/// ASCII: a procedure named <c>MOB_NPC_İNSERT</c> (U+0130) was registered as <c>dbo.MOB_NPC_</c>, and its
/// migration's <c>Down</c> dropped an object that does not exist while the real one stayed. SQL Server's own
/// rules are wider, and these follow them.
/// </remarks>
internal static class SqlIdentifier
{
    /// <summary>
    /// One part of a name: a delimited identifier <c>[...]</c> (any character, <c>]]</c> standing for <c>]</c>),
    /// a quoted identifier <c>"..."</c> (<c>""</c> standing for <c>"</c>), or a regular identifier — a Unicode
    /// letter, <c>_</c>, <c>@</c> or <c>#</c>, then Unicode letters, decimal digits, combining marks,
    /// <c>_</c>, <c>@</c>, <c>#</c> and <c>$</c>.
    /// </summary>
    public const string Part =
        @"(?:\[(?:[^\]]|\]\])+\]|""(?:[^""]|"""")+""|[\p{L}_@#][\p{L}\p{Nd}\p{Mn}\p{Mc}_@#$]*)";

    /// <summary>
    /// Turns the one or two parts a header matched into the canonical <c>schema.name</c> — unescaped, with the
    /// schema defaulting to <c>dbo</c>.
    /// </summary>
    /// <exception cref="InvalidOperationException">The schema itself contains a <c>.</c>, which the canonical form cannot carry.</exception>
    public static string Canonical(string first, string? second, string scriptName)
    {
        var schema = second is null ? "dbo" : Unquote(first);
        var name = second is null ? Unquote(first) : Unquote(second);

        // The canonical key is split on its FIRST dot wherever it is turned back into T-SQL, so a dot in the
        // name is harmless ([dbo].[a.b] round-trips) and a dot in the schema is not: [a.b].[c] would come back
        // as [a].[b.c] and drop the wrong object. Refused rather than mis-dropped.
        if (schema.Contains('.'))
            throw new InvalidOperationException(
                $"Script '{scriptName}' declares an object in schema '{schema}', whose name contains a '.'. " +
                "Such a schema cannot be carried; rename the schema.");

        return $"{schema}.{name}";
    }

    /// <summary>The identifier a part spells: brackets or quotes removed and their doubled closer unescaped.</summary>
    public static string Unquote(string part)
    {
        part = part.Trim();
        if (part.Length >= 2 && part[0] == '[' && part[^1] == ']')
            return part[1..^1].Replace("]]", "]");

        if (part.Length >= 2 && part[0] == '"' && part[^1] == '"')
            return part[1..^1].Replace("\"\"", "\"");

        return part;
    }

    /// <summary>Wraps a canonical <c>schema.name</c> as <c>[schema].[name]</c>, splitting on the first dot.</summary>
    public static string Bracket(string qualifiedName)
    {
        var dot = qualifiedName.IndexOf('.');
        return dot < 0
            ? SqlText.Delimit(qualifiedName)
            : $"{SqlText.Delimit(qualifiedName[..dot])}.{SqlText.Delimit(qualifiedName[(dot + 1)..])}";
    }
}
