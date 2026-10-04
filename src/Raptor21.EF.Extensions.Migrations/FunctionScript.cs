using System.Text.RegularExpressions;
using Raptor21.EF.Extensions.StoredProcedures.Scripts;

namespace Raptor21.EF.Extensions.Migrations;

/// <summary>The three kinds of T-SQL user-defined function, which <c>CREATE OR ALTER</c> cannot convert between.</summary>
public enum FunctionKind
{
    /// <summary><c>RETURNS &lt;type&gt;</c> — a scalar function (<c>sys.objects.type = 'FN'</c>).</summary>
    Scalar,

    /// <summary><c>RETURNS TABLE</c> — an inline table-valued function (<c>'IF'</c>).</summary>
    InlineTableValued,

    /// <summary><c>RETURNS @result TABLE (...)</c> — a multi-statement table-valued function (<c>'TF'</c>).</summary>
    MultiStatementTableValued,
}

/// <summary>Parses and validates a single user-defined function script (one <c>CREATE OR ALTER FUNCTION</c> batch).</summary>
/// <remarks>
/// The rules are the procedure rules of <see cref="StoredProcedureScript"/>: one batch, no <c>GO</c>,
/// <c>CREATE OR ALTER</c> rather than plain <c>CREATE</c>, an unqualified name meaning <c>dbo</c>, and an
/// optional <see cref="ModuleScriptPreamble"/> ahead of the header. A function script is deployed through the
/// same <see cref="StoredProcedureScript.WrapInExec"/>, because the first-in-batch rule is the same.
/// </remarks>
public static partial class FunctionScript
{
    /// <summary>Model-annotation key prefix under which function scripts are stored (e.g. "Fn:dbo.GetAccountID").</summary>
    public const string AnnotationPrefix = "Fn:";

    [GeneratedRegex(@"\bCREATE\s+OR\s+ALTER\s+FUNCTION\s+(\[?[A-Za-z0-9_]+\]?)(?:\s*\.\s*(\[?[A-Za-z0-9_]+\]?))?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex CreateOrAlterRegex();

    /// <summary>
    /// Validates the script (must be a single <c>CREATE OR ALTER FUNCTION</c> batch with no GO separators)
    /// and returns its qualified name in canonical <c>schema.name</c> form (defaults schema to <c>dbo</c>).
    /// </summary>
    /// <exception cref="InvalidOperationException">The script is not a single CREATE OR ALTER FUNCTION batch.</exception>
    public static string ParseQualifiedName(string sql, string scriptName)
        => Header(sql, scriptName).QualifiedName;

    /// <summary>
    /// Reports which kind of function the script creates, read from the <c>RETURNS</c> clause that follows
    /// the parameter list: <c>RETURNS TABLE</c> is inline, <c>RETURNS @name TABLE</c> is multi-statement, and
    /// anything else is scalar.
    /// </summary>
    /// <exception cref="InvalidOperationException">The script is not a single CREATE OR ALTER FUNCTION batch, or it has no RETURNS clause.</exception>
    public static FunctionKind DetectKind(string sql, string scriptName)
    {
        var (_, headerEnd) = Header(sql, scriptName);

        var returns = FindKeyword(sql, headerEnd, "RETURNS");
        if (returns < 0)
            throw new InvalidOperationException(
                $"Function script '{scriptName}' has no RETURNS clause after its CREATE OR ALTER FUNCTION header.");

        var next = NextToken(sql, returns + "RETURNS".Length);
        if (next.Equals("TABLE", StringComparison.OrdinalIgnoreCase))
            return FunctionKind.InlineTableValued;

        if (next.StartsWith('@'))
        {
            var afterVariable = SkipTrivia(sql, IndexAfterToken(sql, returns + "RETURNS".Length));
            if (NextToken(sql, afterVariable).Equals("TABLE", StringComparison.OrdinalIgnoreCase))
                return FunctionKind.MultiStatementTableValued;
        }

        return FunctionKind.Scalar;
    }

    private static (string QualifiedName, int HeaderEnd) Header(string sql, string scriptName)
    {
        if (string.IsNullOrWhiteSpace(sql))
            throw new InvalidOperationException($"Function script '{scriptName}' is empty.");

        if (SqlBatch.ContainsSeparator(sql))
            throw new InvalidOperationException(
                $"Function script '{scriptName}' contains a 'GO' batch separator. Each function must be a single CREATE OR ALTER FUNCTION batch (no GO).");

        var match = CreateOrAlterRegex().Match(sql);
        if (!match.Success)
            throw new InvalidOperationException(
                $"Function script '{scriptName}' must start with 'CREATE OR ALTER FUNCTION' (idempotency requires CREATE OR ALTER, not plain CREATE).");

        var first = Unbracket(match.Groups[1].Value);
        var second = match.Groups[2].Success ? Unbracket(match.Groups[2].Value) : null;

        var schema = second is null ? "dbo" : first;
        var name = second ?? first;
        return ($"{schema}.{name}", match.Index + match.Length);
    }

    private static string Unbracket(string ident) => ident.Trim().Trim('[', ']');

    // The first occurrence of a keyword at statement level, from `start`: comments, string literals,
    // bracketed identifiers and "..." are stepped over, so a parameter list annotated with
    // `-- returns the id` or a default of 'RETURNS' does not decide the function's kind.
    private static int FindKeyword(string sql, int start, string keyword)
    {
        var i = start;
        while (i < sql.Length)
        {
            var c = sql[i];
            if (c == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
            {
                i = SkipLineComment(sql, i);
            }
            else if (c == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                i = SkipBlockComment(sql, i);
            }
            else if (c is '\'' or '"' or '[')
            {
                i = SkipDelimited(sql, i);
            }
            else if (IsWordChar(c))
            {
                var wordStart = i;
                while (i < sql.Length && IsWordChar(sql[i]))
                    i++;

                if (i - wordStart == keyword.Length
                    && string.Compare(sql, wordStart, keyword, 0, keyword.Length, StringComparison.OrdinalIgnoreCase) == 0)
                    return wordStart;
            }
            else
            {
                i++;
            }
        }

        return -1;
    }

    private static string NextToken(string sql, int start)
    {
        var i = SkipTrivia(sql, start);
        var end = IndexAfterToken(sql, i);
        return sql[i..end];
    }

    private static int IndexAfterToken(string sql, int start)
    {
        var i = SkipTrivia(sql, start);
        if (i < sql.Length && sql[i] == '@')
            i++;
        while (i < sql.Length && IsWordChar(sql[i]))
            i++;
        return i;
    }

    private static int SkipTrivia(string sql, int i)
    {
        while (i < sql.Length)
        {
            if (char.IsWhiteSpace(sql[i]))
                i++;
            else if (sql[i] == '-' && i + 1 < sql.Length && sql[i + 1] == '-')
                i = SkipLineComment(sql, i);
            else if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
                i = SkipBlockComment(sql, i);
            else
                break;
        }

        return i;
    }

    private static int SkipLineComment(string sql, int i)
    {
        var end = sql.IndexOf('\n', i);
        return end < 0 ? sql.Length : end + 1;
    }

    // Block comments nest in T-SQL.
    private static int SkipBlockComment(string sql, int i)
    {
        var depth = 0;
        while (i < sql.Length)
        {
            if (sql[i] == '/' && i + 1 < sql.Length && sql[i + 1] == '*')
            {
                depth++;
                i += 2;
            }
            else if (sql[i] == '*' && i + 1 < sql.Length && sql[i + 1] == '/')
            {
                depth--;
                i += 2;
                if (depth == 0)
                    return i;
            }
            else
            {
                i++;
            }
        }

        return sql.Length;
    }

    // '...' and "..." close on their own character, [...] on ']'; each escapes itself by doubling.
    private static int SkipDelimited(string sql, int i)
    {
        var close = sql[i] == '[' ? ']' : sql[i];
        i++;
        while (i < sql.Length)
        {
            if (sql[i] == close)
            {
                if (i + 1 < sql.Length && sql[i + 1] == close)
                {
                    i += 2;
                    continue;
                }

                return i + 1;
            }

            i++;
        }

        return sql.Length;
    }

    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '_' or '@' or '#' or '$';
}
