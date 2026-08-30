using System.Text.RegularExpressions;
using Raptor21.EF.Extensions.StoredProcedures.Scripts;

namespace Raptor21.EF.Extensions.Migrations;

/// <summary>Parses and validates a single stored-procedure script (one <c>CREATE OR ALTER PROCEDURE</c> batch).</summary>
public static partial class StoredProcedureScript
{
    /// <summary>Model-annotation key prefix under which procedure scripts are stored (e.g. "Sp:dbo.ACCOUNT_LOGIN").</summary>
    public const string AnnotationPrefix = "Sp:";

    [GeneratedRegex(@"\bCREATE\s+OR\s+ALTER\s+PROC(?:EDURE)?\s+(\[?[A-Za-z0-9_]+\]?)(?:\s*\.\s*(\[?[A-Za-z0-9_]+\]?))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateOrAlterRegex();

    /// <summary>
    /// Validates the script (must be a single <c>CREATE OR ALTER PROCEDURE</c> batch with no GO separators)
    /// and returns its qualified name in canonical <c>schema.name</c> form (defaults schema to <c>dbo</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The script is not a single CREATE OR ALTER PROCEDURE batch.</exception>
    public static string ParseQualifiedName(string sql, string scriptName)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new InvalidOperationException($"Stored procedure script '{scriptName}' is empty.");

        // SqlBatch is the single reader of GO in this library. A regex over the whole text used to
        // stand here, and it saw a GO inside a string literal or a block comment as a separator,
        // rejecting scripts the runtime applier splits correctly.
        if (SqlBatch.ContainsSeparator(sql))
            throw new InvalidOperationException(
                $"Stored procedure script '{scriptName}' contains a 'GO' batch separator. Each procedure must be a single CREATE OR ALTER PROCEDURE batch (no GO).");

        var match = CreateOrAlterRegex().Match(sql);
        if (!match.Success)
            throw new InvalidOperationException(
                $"Stored procedure script '{scriptName}' must start with 'CREATE OR ALTER PROCEDURE' (idempotency requires CREATE OR ALTER, not plain CREATE).");

        var first = Unbracket(match.Groups[1].Value);
        var second = match.Groups[2].Success ? Unbracket(match.Groups[2].Value) : null;

        var schema = second is null ? "dbo" : first;
        var name = second ?? first;
        return $"{schema}.{name}";
    }

    /// <summary>Wraps a canonical <c>schema.name</c> as <c>[schema].[name]</c> for use in T-SQL.</summary>
    public static string Bracket(string qualifiedName)
    {
        var dot = qualifiedName.IndexOf('.');
        return dot < 0
            ? $"[{qualifiedName}]"
            : $"[{qualifiedName[..dot]}].[{qualifiedName[(dot + 1)..]}]";
    }

    private static string Unbracket(string ident) => ident.Trim().Trim('[', ']');
}
