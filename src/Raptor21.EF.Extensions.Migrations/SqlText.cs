namespace Raptor21.EF.Extensions.Migrations;

/// <summary>
/// The three spellings the partition and columnstore SQL needs: a bracketed identifier, a bracketed
/// schema-qualified name, and an <c>N'...'</c> literal for comparing against a catalog view. Kept out of
/// <see cref="StoredProcedureScript.Bracket"/>, which splits a canonical <c>schema.name</c> on its dot and would
/// cut an identifier that itself contains one.
/// </summary>
internal static class SqlText
{
    /// <summary><c>[name]</c>, with a closing bracket inside the name doubled the way T-SQL escapes it.</summary>
    public static string Delimit(string identifier) => $"[{identifier.Replace("]", "]]")}]";

    /// <summary><c>[schema].[name]</c>, or <c>[name]</c> alone when the schema is unspecified and the connection's default applies.</summary>
    public static string Qualified(string? schema, string name)
        => string.IsNullOrEmpty(schema) ? Delimit(name) : $"{Delimit(schema)}.{Delimit(name)}";

    /// <summary><c>N'text'</c> with every embedded quote doubled.</summary>
    public static string NLiteral(string text) => $"N'{text.Replace("'", "''")}'";
}
