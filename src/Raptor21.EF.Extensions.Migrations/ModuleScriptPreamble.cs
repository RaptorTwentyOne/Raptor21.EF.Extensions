using System.Text.RegularExpressions;

namespace Raptor21.EF.Extensions.Migrations;

/// <summary>
/// The <c>SET ANSI_NULLS</c> / <c>SET QUOTED_IDENTIFIER</c> statements a procedure or function script may
/// declare ahead of its <c>CREATE OR ALTER</c> header, split from the module text they apply to.
/// </summary>
/// <remarks>
/// <para>
/// SQL Server records both options on the module when it is created — <c>sys.sql_modules.uses_ansi_nulls</c>
/// and <c>uses_quoted_identifier</c> — and applies the recorded values every time the module runs, whatever
/// the caller's session says. A legacy module created with either option <c>OFF</c> therefore behaves
/// differently from the same text created through an ordinary client connection, which has both <c>ON</c>:
/// under <c>ANSI_NULLS OFF</c>, <c>WHERE col = @p</c> matches the NULL rows when <c>@p</c> is NULL, and under
/// <c>QUOTED_IDENTIFIER OFF</c>, <c>"abc"</c> is a string literal rather than a column name. The preamble is how
/// a script says which values its module must be created with.
/// </para>
/// <para>
/// The grammar is deliberately narrow: at the very start of the script (after whitespace or a byte-order mark),
/// one or more <c>SET ANSI_NULLS ON|OFF</c> / <c>SET QUOTED_IDENTIFIER ON|OFF</c> statements, case-insensitive,
/// each optionally terminated by <c>;</c>, separated by whitespace. The module text is everything after the
/// last statement and the line break that ends it, kept byte for byte. Anything else — another <c>SET</c>, a
/// comment, the header itself — ends the preamble; a script whose first statement is not one of these two
/// options has no preamble, and its text is the module text unchanged.
/// </para>
/// </remarks>
public sealed partial class ModuleScriptPreamble
{
    private ModuleScriptPreamble(IReadOnlyList<string> setStatements, string moduleText)
    {
        SetStatements = setStatements;
        ModuleText = moduleText;
    }

    /// <summary>
    /// The preamble's statements in the order the script states them, each in the canonical spelling
    /// <c>SET ANSI_NULLS OFF;</c>. Empty when the script has no preamble.
    /// </summary>
    public IReadOnlyList<string> SetStatements { get; }

    /// <summary>The script with the preamble removed: the text the module itself is created from.</summary>
    public string ModuleText { get; }

    /// <summary><see langword="true"/> when the script declares at least one option.</summary>
    public bool HasOptions => SetStatements.Count > 0;

    /// <summary>
    /// The value the preamble sets <c>ANSI_NULLS</c> to, or <see langword="null"/> when it does not set it.
    /// </summary>
    public bool? AnsiNulls { get; private init; }

    /// <summary>
    /// The value the preamble sets <c>QUOTED_IDENTIFIER</c> to, or <see langword="null"/> when it does not set it.
    /// </summary>
    public bool? QuotedIdentifier { get; private init; }

    // \G anchors each statement where the previous one (or the leading whitespace) ended, so a SET further
    // down the script — inside the module — is never mistaken for a preamble statement.
    [GeneratedRegex(@"\G[ \t\r\n]*SET[ \t]+(ANSI_NULLS|QUOTED_IDENTIFIER)[ \t]+(ON|OFF)\b[ \t]*;?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SetStatementRegex();

    /// <summary>Splits <paramref name="sql"/> into its preamble and its module text.</summary>
    /// <exception cref="InvalidOperationException">The preamble sets the same option twice.</exception>
    public static ModuleScriptPreamble Parse(string sql)
    {
        ArgumentNullException.ThrowIfNull(sql);

        var start = sql.Length > 0 && sql[0] == '﻿' ? 1 : 0;
        var statements = new List<string>();
        bool? ansiNulls = null;
        bool? quotedIdentifier = null;
        var end = start;

        var regex = SetStatementRegex();
        for (var match = regex.Match(sql, end); match.Success; match = regex.Match(sql, end))
        {
            var option = match.Groups[1].Value.ToUpperInvariant();
            var on = string.Equals(match.Groups[2].Value, "ON", StringComparison.OrdinalIgnoreCase);

            // Two statements for one option are not a contradiction SQL Server would object to — the last one
            // wins — but they are one a reader of the file would, and nothing is lost by refusing them.
            ref var slot = ref option == "ANSI_NULLS" ? ref ansiNulls : ref quotedIdentifier;
            if (slot is not null)
                throw new InvalidOperationException(
                    $"The script's preamble sets {option} twice. State each option at most once, ahead of the CREATE OR ALTER header.");

            slot = on;
            statements.Add($"SET {option} {(on ? "ON" : "OFF")};");
            end = match.Index + match.Length;
        }

        if (statements.Count == 0)
            return new ModuleScriptPreamble([], sql);

        // The line break that ends the last statement belongs to the preamble; everything after it is the
        // module, byte for byte — including any blank line the module itself opens with, so the line numbers
        // SQL Server reports keep pointing at the module's own text.
        while (end < sql.Length && (sql[end] == ' ' || sql[end] == '\t'))
            end++;
        if (end < sql.Length && sql[end] == '\r')
            end++;
        if (end < sql.Length && sql[end] == '\n')
            end++;

        return new ModuleScriptPreamble(statements, sql[end..])
        {
            AnsiNulls = ansiNulls,
            QuotedIdentifier = quotedIdentifier,
        };
    }
}
