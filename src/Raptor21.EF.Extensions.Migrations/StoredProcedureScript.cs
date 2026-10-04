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
        // stand here, and it saw a GO inside a string literal, a bracketed identifier, a "..." construct
        // or a block comment as a separator, rejecting scripts that are one perfectly ordinary batch.
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

    /// <summary>
    /// Wraps one procedure or function script as <c>EXEC(N'...')</c> so it survives being nested inside
    /// another statement. A script that opens with a <see cref="ModuleScriptPreamble"/> is wrapped twice:
    /// <c>EXEC(N'SET ANSI_NULLS OFF; EXEC(N''CREATE OR ALTER ...'')');</c>, so that the module is created
    /// under the options the preamble states.
    /// </summary>
    /// <exception cref="ArgumentException">The script contains a line reading <c>GO</c>.</exception>
    /// <exception cref="InvalidOperationException">The script's preamble sets one option twice.</exception>
    public static string WrapInExec(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        // Defence in depth: ParseQualifiedName already refuses such a script at registration, so nothing
        // on the differ's path arrives here carrying one. The method is public, so the precondition is
        // restated rather than left in another type.
        if (SqlBatch.ContainsSeparator(sql))
            throw new ArgumentException(
                "Script contains a line reading 'GO' and cannot be wrapped: EXEC runs its argument as a " +
                "single batch. Wrap one batch at a time.",
                nameof(sql));

        // CREATE OR ALTER PROCEDURE has to be the first statement in its batch, and it is first in none of
        // the scripts EF writes. `migrations script --idempotent` puts `IF NOT EXISTS (...) BEGIN` in front
        // of every command, and even the plain `migrations script` opens a BEGIN TRANSACTION and then
        // separates commands with a newline rather than a batch terminator. EXEC compiles its argument as
        // a batch of its own, which satisfies the rule in both - which is also why the wrap is
        // unconditional: gating it on the idempotent flag, the way EF's own SqlServerMigrationsSqlGenerator
        // gates GenerateExecWhenIdempotent, would leave the plain script just as invalid as it is today.
        //
        // THE ESCAPING RULE, in full: double every single quote, and emit exactly ONE N'...' literal.
        // Doubling is safe on text that already contains doubled quotes, because it escapes a character
        // rather than re-parsing a construct. Nothing else needs escaping: inside a string literal `--`,
        // `/* */` and the word GO are all just characters.
        //
        // One literal, never a concatenation. Each concatenated piece is typed on its own, which
        // reintroduces the 4,000-character cliff a single N'...' constant is promoted past, and a
        // truncated body is far worse than a syntax error because it deploys.
        var preamble = ModuleScriptPreamble.Parse(sql);
        var literal = preamble.ModuleText.TrimEnd('\n', '\r', ';').Replace("'", "''");
        if (!preamble.HasOptions)
            return $"EXEC(N'{literal}');";

        // THE PREAMBLE RULE. SQL Server records ANSI_NULLS and QUOTED_IDENTIFIER on the module from the
        // session that creates it, and the two options do not reach that session the same way.
        // QUOTED_IDENTIFIER is applied when a batch is PARSED: a SET QUOTED_IDENTIFIER OFF placed in front of
        // EXEC(N'CREATE ...') in the same batch is overridden at parse time by any SET ... ON that follows it in
        // that batch, so the restoring statement a migration needs would undo the very option being set - and
        // putting the restore in a separate command leaks OFF into everything EF runs in between. A dynamic
        // batch solves both: the SETs run at the start of the OUTER EXEC's own batch, the INNER EXEC is parsed
        // and created under them (sys.sql_modules records 0), and when the outer dynamic batch ends SQL Server
        // restores the caller's options, because SET options changed in a dynamic batch are scoped to it. That
        // also holds inside the migration's transaction and inside the IF NOT EXISTS ... BEGIN ... END of an
        // idempotent script - the two places EF puts this statement. The escaping is the same rule applied
        // twice: the module text is doubled into the inner literal, and the whole inner statement doubled
        // again into the outer one - still one literal per level, never a concatenation.
        var inner = $"{string.Join(' ', preamble.SetStatements)} EXEC(N'{literal}')";
        return $"EXEC(N'{inner.Replace("'", "''")}');";
    }
}
