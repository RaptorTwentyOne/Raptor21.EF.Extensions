using System;
using System.Text;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator;

/// <summary>A restore point: everything <see cref="SqlScanner"/> remembers.</summary>
internal readonly struct SqlScannerState
{
    /// <summary>Creates a restore point.</summary>
    public SqlScannerState(int index, int line, int lineStart, int lastEnd)
    {
        Index = index;
        Line = line;
        LineStart = lineStart;
        LastEnd = lastEnd;
    }

    /// <summary>Absolute character offset of the cursor.</summary>
    public int Index { get; }

    /// <summary>Zero-based line of the cursor.</summary>
    public int Line { get; }

    /// <summary>Absolute offset of the first character of the cursor's line.</summary>
    public int LineStart { get; }

    /// <summary>Index just past the last significant character consumed.</summary>
    public int LastEnd { get; }
}

/// <summary>
/// One forward pass over a .sql script, tracking line and column as it goes. Every read is bounds
/// checked and no read throws, which is what lets its callers promise the same.
/// </summary>
/// <remarks>
/// <para>
/// This was <c>ProcedureHeaderParser.Scanner</c>, promoted verbatim to a file of its own so that
/// comments, string literals and delimited identifiers are read through <em>one</em> lexer rather than
/// through a second one written to the same description. Two scanners would be two answers to "is this
/// <c>GO</c> inside a comment", and the whole point of the parser above it is that the answer is lexical
/// rather than textual.
/// </para>
/// <para>
/// It is a lexer and nothing more: it knows characters, trivia, words and delimited names, and knows no
/// T-SQL statement, clause or keyword. The grammar lives in <see cref="ProcedureHeaderParser"/>, which
/// stops at the body's <c>AS</c> - the boundary that keeps a body-only edit a no-op on the header branch
/// of the pipeline. Promoting the lexer does not move that boundary; it only stops it being
/// re-implemented.
/// </para>
/// <para>
/// Dependency-free on purpose (<c>System</c>, <c>System.Text</c> - no Roslyn, no regex, no IO), for the
/// same reason the header parser is: <c>Raptor21.EF.Extensions.Migrations</c> can adopt both as linked
/// source files.
/// </para>
/// </remarks>
internal sealed class SqlScanner
{
    private readonly string _text;
    private int _index;
    private int _line;
    private int _lineStart;
    private int _lastEnd;

    /// <summary>Creates a scanner positioned at the start of <paramref name="text"/>.</summary>
    public SqlScanner(string text) => _text = text;

    /// <summary>Whether the whole script has been consumed.</summary>
    public bool Eof => _index >= _text.Length;

    /// <summary>Index just past the last <em>significant</em> character consumed (trivia excluded).</summary>
    public int LastEnd => _lastEnd;

    /// <summary>Absolute character offset of the cursor.</summary>
    /// <remarks>
    /// Already reachable through <see cref="Save"/>; named here because the body parser compares it
    /// against a body's end offset on every step, and <c>s.Save().Index</c> would say nothing about why.
    /// </remarks>
    public int Index => _index;

    /// <summary>Zero-based line of the cursor (LinePosition convention).</summary>
    public int Line => _line;

    /// <summary>
    /// Absolute offset of the first character of the cursor's line.
    /// </summary>
    /// <remarks>
    /// The body parser needs this to decide whether a <c>GO</c> is <em>line-initial</em>, which is the
    /// only thing that makes it a batch terminator rather than an identifier. Nothing textual can answer
    /// that: the characters before it on the line have to have been lexed to know they were whitespace
    /// and not the tail of a block comment.
    /// </remarks>
    public int LineStart => _lineStart;

    /// <summary>Keyword comparison, in the one spelling both parsers use.</summary>
    /// <remarks>
    /// Ordinal-ignore-case over a whole <see cref="ReadWordRun"/>, never a prefix or a substring: that
    /// pairing is what stops <c>@Total</c> and <c>2CREATE</c> reading as keywords, because
    /// <see cref="IsWordChar"/> pulls the whole run in first.
    /// </remarks>
    public static bool Eq(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a character can begin a keyword.</summary>
    public static bool IsWordStart(char c) => char.IsLetter(c) || c == '_';

    /// <summary>Whether a character can appear inside an identifier run.</summary>
    public static bool IsWordChar(char c) =>
        char.IsLetterOrDigit(c) || c == '_' || c == '@' || c == '#' || c == '$';

    /// <summary>The character at the cursor, or NUL at end of file.</summary>
    public char Peek() => _index < _text.Length ? _text[_index] : '\0';

    /// <summary>The character <paramref name="offset"/> ahead of the cursor, or NUL past the end.</summary>
    public char Peek(int offset) => _index + offset < _text.Length ? _text[_index + offset] : '\0';

    /// <summary>The character at absolute offset <paramref name="index"/>, or NUL outside the script.</summary>
    /// <remarks>
    /// For the line-initial test only, which has to look <em>behind</em> the cursor at characters the
    /// scanner has already passed. Reading is all it does - the cursor does not move.
    /// </remarks>
    public char CharAt(int index) => index >= 0 && index < _text.Length ? _text[index] : '\0';

    /// <summary>Captures a restore point.</summary>
    public SqlScannerState Save() => new SqlScannerState(_index, _line, _lineStart, _lastEnd);

    /// <summary>Rewinds to a restore point, line and column included.</summary>
    public void Restore(SqlScannerState state)
    {
        _index = state.Index;
        _line = state.Line;
        _lineStart = state.LineStart;
        _lastEnd = state.LastEnd;
    }

    /// <summary>Consumes significant characters, extending <see cref="LastEnd"/>.</summary>
    public void Consume(int count)
    {
        Advance(count);
        _lastEnd = _index;
    }

    /// <summary>Consumes whitespace, <c>--</c> line comments and nested <c>/* */</c> block comments.</summary>
    public void SkipTrivia()
    {
        while (_index < _text.Length)
        {
            var c = _text[_index];

            if (char.IsWhiteSpace(c))
            {
                Advance(1);
                continue;
            }

            if (c == '-' && Peek(1) == '-')
            {
                while (_index < _text.Length && _text[_index] != '\n')
                    Advance(1);
                continue;
            }

            if (c == '/' && Peek(1) == '*')
            {
                // Depth counting, not first-'*/' matching: nested block comments are legal T-SQL.
                // An unterminated one runs to end of file, which abandons the header in progress.
                var depth = 0;
                while (_index < _text.Length)
                {
                    if (_text[_index] == '/' && Peek(1) == '*')
                    {
                        depth++;
                        Advance(2);
                        continue;
                    }

                    if (_text[_index] == '*' && Peek(1) == '/')
                    {
                        depth--;
                        Advance(2);
                        if (depth == 0)
                            break;
                        continue;
                    }

                    Advance(1);
                }

                continue;
            }

            return;
        }
    }

    /// <summary>Consumes a <c>'...'</c> literal, honouring the <c>''</c> escape. False if unterminated.</summary>
    public bool SkipStringLiteral()
    {
        if (Peek() != '\'')
            return false;

        Advance(1);
        while (_index < _text.Length)
        {
            if (_text[_index] == '\'')
            {
                if (Peek(1) == '\'')
                {
                    Advance(2);
                    continue;
                }

                Advance(1);
                _lastEnd = _index;
                return true;
            }

            Advance(1);
        }

        return false;
    }

    /// <summary>
    /// Consumes a delimited run - <c>[...]</c> or <c>"..."</c> - honouring the doubled-close escape,
    /// without building its value. False if unterminated.
    /// </summary>
    public bool SkipDelimited(char close)
    {
        Advance(1);
        while (_index < _text.Length)
        {
            if (_text[_index] == close)
            {
                if (Peek(1) == close)
                {
                    Advance(2);
                    continue;
                }

                Advance(1);
                _lastEnd = _index;
                return true;
            }

            Advance(1);
        }

        return false;
    }

    /// <summary>Consumes the maximal run of identifier characters at the cursor.</summary>
    public string ReadWordRun()
    {
        if (!IsWordChar(Peek()))
            return "";

        var start = _index;
        while (_index < _text.Length && IsWordChar(_text[_index]))
            Advance(1);

        _lastEnd = _index;
        return _text.Substring(start, _index - start);
    }

    /// <summary>
    /// Reads a procedure or type name: a regular identifier, <c>[delimited]</c> or
    /// <c>"quoted"</c>, with their doubling escapes. This is exactly where a regex over the text
    /// goes wrong - <c>[My Proc]</c> truncated to <c>My</c>, <c>[Odd]]Name]</c> to <c>Odd</c>.
    /// </summary>
    public bool TryReadName(out string value)
    {
        var c = Peek();

        if (c == '[')
            return TryReadDelimitedName(']', out value);

        if (c == '"')
            return TryReadDelimitedName('"', out value);

        if (char.IsLetter(c) || c == '_' || c == '#')
        {
            value = ReadWordRun();
            return value.Length != 0;
        }

        value = "";
        return false;
    }

    /// <summary>Reads <c>'@'</c> followed by identifier characters.</summary>
    public bool TryReadParameterName(out string value)
    {
        if (Peek() != '@')
        {
            value = "";
            return false;
        }

        var start = _index;
        Advance(1);
        while (_index < _text.Length && IsWordChar(_text[_index]))
            Advance(1);

        _lastEnd = _index;
        value = _text.Substring(start, _index - start);
        return value.Length > 1;
    }

    /// <summary>Reads a decimal integer.</summary>
    /// <remarks>
    /// Lifted out of the header parser's type-argument reader, nine-digit cap and all: nine digits is
    /// already absurd for a length, a precision or a <c>GO</c> batch count, and rather than overflow or
    /// guess, the construct is treated as malformed. The cursor is left where the digits ran out, which
    /// is what the one existing caller has always done - both of its failures abandon the whole header.
    /// </remarks>
    public bool TryReadInteger(out int value)
    {
        value = 0;

        if (!IsAsciiDigit(Peek()))
            return false;

        var digits = 0;
        while (_index < _text.Length && IsAsciiDigit(_text[_index]))
        {
            if (digits == 9)
                return false;

            value = (value * 10) + (_text[_index] - '0');
            digits++;
            Advance(1);
        }

        _lastEnd = _index;
        return true;
    }

    private static bool IsAsciiDigit(char c) => c >= '0' && c <= '9';

    private bool TryReadDelimitedName(char close, out string value)
    {
        var builder = new StringBuilder();
        Advance(1);

        while (_index < _text.Length)
        {
            var c = _text[_index];
            if (c == close)
            {
                if (Peek(1) == close)
                {
                    builder.Append(close);
                    Advance(2);
                    continue;
                }

                Advance(1);
                _lastEnd = _index;
                value = builder.ToString();
                return value.Length != 0;
            }

            builder.Append(c);
            Advance(1);
        }

        value = "";
        return false;
    }

    /// <summary>Moves the cursor without extending <see cref="LastEnd"/>, keeping line and column current.</summary>
    private void Advance(int count)
    {
        for (var i = 0; i < count && _index < _text.Length; i++)
        {
            if (_text[_index] == '\n')
            {
                _line++;
                _lineStart = _index + 1;
            }

            _index++;
        }
    }
}
