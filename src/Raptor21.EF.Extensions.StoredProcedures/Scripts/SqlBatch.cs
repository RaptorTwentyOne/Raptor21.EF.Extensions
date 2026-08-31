using System.Text;

namespace Raptor21.EF.Extensions.StoredProcedures.Scripts;

/// <summary>
/// The one place this library decides what a <c>GO</c> batch separator is. One scanner answers both
/// questions that can be asked of a script: whether it contains a separator at all, which is what the
/// migration side gates on, and where the separators fall, which is what <see cref="Split"/> returns for
/// a consumer reading <c>.sql</c> of its own. Two readers of that rule disagreed once - a regex matching
/// <c>^\s*GO\s*$</c> anywhere in the text rejected scripts a character-aware scanner handled correctly -
/// and a script that one half accepts and the other refuses is the worst failure this library can have,
/// because it appears at deployment time on a file that built cleanly.
/// </summary>
public static class SqlBatch
{
    /// <summary>Splits a script into the batches a server should receive, honouring <c>GO</c> separators.</summary>
    /// <remarks>Whitespace-only batches are dropped, so the result never contains an empty command.</remarks>
    public static List<string> Split(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var batches = new List<string>();
        var sb = new StringBuilder();
        SqlScanState state = default;
        var batchHasLines = false;

        foreach (var raw in sql.Split('\n'))
        {
            // Only the '\r' that splitting on '\n' orphaned comes off. Any other trailing whitespace can
            // sit inside a multi-line string literal, where it is part of the value, so trimming it
            // would change what the batch means.
            var line = raw.TrimEnd('\r', '\n');

            if (IsSeparator(line, state))
            {
                FlushBatch(sb, batches);
                batchHasLines = false;
                continue;
            }

            // Whether a separator is owed is tracked explicitly instead of being read off sb.Length, so a
            // batch opening with blank lines keeps them and the line numbers SQL Server reports still point
            // at the .sql source. The separator is '\n' and not AppendLine, which writes
            // Environment.NewLine: one file would otherwise become CRLF-joined text from a Windows deploy
            // and LF-joined text from Linux CI, so both the SQL executed and its hash would record which
            // machine ran the deployment rather than what the script says.
            if (batchHasLines)
                sb.Append('\n');

            sb.Append(line);
            batchHasLines = true;

            ScanLine(line, ref state);
        }

        FlushBatch(sb, batches);
        return batches;
    }

    /// <summary>
    /// Reports whether the script contains a real <c>GO</c> batch separator. A <c>GO</c> inside a string
    /// literal, a bracketed identifier, a <c>"..."</c> construct or a block comment is content, not a
    /// separator.
    /// </summary>
    public static bool ContainsSeparator(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        SqlScanState state = default;

        foreach (var raw in sql.Split('\n'))
        {
            var line = raw.TrimEnd('\r', '\n');
            if (IsSeparator(line, state))
                return true;

            ScanLine(line, ref state);
        }

        return false;
    }

    // A GO separates only where the lines before it left the scanner at statement level: inside a literal,
    // a bracketed identifier, a "..." construct or a block comment it is content, and cutting there hands
    // the server two fragments neither of which parses. The comparison reads a trimmed view rather than
    // trimming the line itself, because that same line is what gets appended and has to keep its indentation.
    private static bool IsSeparator(string line, in SqlScanState state) =>
        state.AtStatementLevel && line.AsSpan().Trim().Equals("GO", StringComparison.OrdinalIgnoreCase);

    private static void FlushBatch(StringBuilder buffer, List<string> batches)
    {
        var batch = buffer.ToString();
        buffer.Clear();

        // A buffer holding nothing but whitespace - the blank line left after a trailing GO, a file of
        // blank lines - is dropped rather than emitted, so a caller never gets an empty command to send.
        if (!string.IsNullOrWhiteSpace(batch))
            batches.Add(batch);
    }

    /// <summary>
    /// As much of a T-SQL lexer as batch splitting needs: enough state to decide, at a line boundary,
    /// whether a "GO" on the next line separates batches or sits inside something that outlives the break.
    /// </summary>
    private struct SqlScanState
    {
        public bool InStringLiteral;
        public bool InBracketedIdentifier;

        /// <summary>
        /// A "..." construct, whatever it currently means. Under QUOTED_IDENTIFIER ON - SqlClient's
        /// default - it is a delimited identifier; with it OFF it is a string literal. One flag covers
        /// both because the two readings agree on everything this scanner needs: the contents are not
        /// statement text, "" is the escape, and the construct outlives a line break. Which of the two a
        /// given script means cannot be known here anyway, since SET QUOTED_IDENTIFIER can be issued in
        /// a batch this scanner has already handed on.
        /// </summary>
        public bool InDoubleQuotedConstruct;

        /// <summary>Block comments nest in T-SQL, so an inner /* has to be counted rather than ignored.</summary>
        public int BlockCommentDepth;

        public readonly bool AtStatementLevel =>
            !InStringLiteral && !InBracketedIdentifier && !InDoubleQuotedConstruct && BlockCommentDepth == 0;
    }

    /// <summary>
    /// Advances <paramref name="state"/> across one line. A "--" comment carries no state of its own since
    /// it ends at the line break, but it still has to be consumed here so that an apostrophe or a "/*"
    /// written inside one does not open a construct that then never closes.
    /// </summary>
    private static void ScanLine(string line, ref SqlScanState state)
    {
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            // '\0' as the end-of-line sentinel is safe because every character this method pairs on is
            // printable, so a missing successor can never complete a token.
            var next = i + 1 < line.Length ? line[i + 1] : '\0';

            if (state.InStringLiteral)
            {
                if (c != '\'')
                    continue;

                // '' is an escaped quote and leaves the literal open.
                if (next == '\'')
                    i++;
                else
                    state.InStringLiteral = false;
            }
            else if (state.InBracketedIdentifier)
            {
                if (c != ']')
                    continue;

                // ]] escapes a literal ] inside brackets, mirroring '' inside a string.
                if (next == ']')
                    i++;
                else
                    state.InBracketedIdentifier = false;
            }
            else if (state.InDoubleQuotedConstruct)
            {
                if (c != '"')
                    continue;

                // "" escapes a literal " inside the construct, under both readings of it, mirroring ''
                // inside a string and ]] inside brackets.
                if (next == '"')
                    i++;
                else
                    state.InDoubleQuotedConstruct = false;
            }
            else if (state.BlockCommentDepth > 0)
            {
                if (c == '/' && next == '*')
                {
                    state.BlockCommentDepth++;
                    i++;
                }
                else if (c == '*' && next == '/')
                {
                    state.BlockCommentDepth--;
                    i++;
                }
            }
            else if (c == '-' && next == '-')
            {
                // The rest of the line is a comment, and it takes nothing across the break with it.
                return;
            }
            else if (c == '/' && next == '*')
            {
                state.BlockCommentDepth++;
                i++;
            }
            else if (c == '\'')
            {
                state.InStringLiteral = true;
            }
            else if (c == '[')
            {
                state.InBracketedIdentifier = true;
            }
            else if (c == '"')
            {
                // Opening this state is what stops a GO inside a multi-line "..." from cutting the
                // construct in half, and equally what stops an apostrophe or a '[' written inside one
                // from opening a state that never closes and swallowing a real GO further down the file.
                // The second failure is the more damaging of the two: a swallowed separator merges two
                // batches, which is exactly how CREATE OR ALTER PROCEDURE stops being the first
                // statement in its batch.
                state.InDoubleQuotedConstruct = true;
            }
        }
    }
}
