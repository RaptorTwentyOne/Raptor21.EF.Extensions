using System;
using System.Collections.Generic;
using System.Threading;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator;

/// <summary>
/// Reads <c>CREATE</c>/<c>ALTER PROCEDURE</c> <em>headers</em> - the qualified name and the parameter
/// list - out of a .sql script, and stops at the body's <c>AS</c>.
/// </summary>
/// <remarks>
/// <para>
/// Three properties make this parser what it is, and none of them is incidental.
/// </para>
/// <para>
/// <b>It is header-only.</b> Stopping at <c>AS</c> is what makes editing a procedure body a no-op in
/// the IDE pipeline, and what keeps a cold parse of several hundred scripts cheap - command-line
/// builds get no incrementality at all, so every script is parsed from scratch on every build. It also
/// means a body no parser could read (the old <c>*=</c> outer join, old-form <c>RAISERROR</c>, a
/// <c>SELECT * FORM</c> typo) still yields a perfectly good header, which a statement-level parser
/// such as ScriptDom cannot do.
/// </para>
/// <para>
/// <b>It never reports and never throws.</b> A file that does not parse, contains no procedure, or
/// matches no declaration contributes nothing and says nothing; every .sql-derived diagnostic is
/// attached to a C# method during resolution. That is the invariant that keeps a legacy corpus - which
/// this library must not break on upgrade - completely silent. An unreadable construct abandons the
/// current header and scanning resumes at the next <c>CREATE</c>/<c>ALTER</c>, so one bad header never
/// costs the others in the file.
/// </para>
/// <para>
/// <b>It does not need <c>SqlBatch</c>.</b> The comment/string/bracket lexer makes <c>GO</c> irrelevant
/// by construction: <c>CREATE PROCEDURE</c> is only recognised at statement level, so a <c>GO</c> in a
/// literal or a comment cannot mislead it, and a GO-separated multi-procedure file simply yields two
/// headers. It is deliberately standalone and dependency-free (<c>System</c>,
/// <c>System.Collections.Generic</c>, <c>System.Threading</c> and <see cref="SqlScanner"/> - no Roslyn,
/// no regex, no IO) so that <c>Raptor21.EF.Extensions.Migrations</c> can later adopt it as a linked
/// source file and retire its comment-blind <c>CreateOrAlterRegex</c>.
/// </para>
/// <para>
/// The lexer itself lives in <see cref="SqlScanner"/>, which was this class's own nested
/// <c>Scanner</c> until it was promoted to a file of its own. The move was exactly that - a move;
/// nothing above reads any further into a script than it did before, and the <c>AS</c> is still where
/// this parser stops.
/// </para>
/// </remarks>
internal static class ProcedureHeaderParser
{
    /// <summary>
    /// Every <c>CREATE</c>/<c>ALTER</c> procedure header in <paramref name="text"/>, in file order.
    /// </summary>
    /// <param name="filePath">The <c>AdditionalText.Path</c>, recorded verbatim on each header.</param>
    /// <param name="text">The whole script. Only the headers are read.</param>
    /// <param name="ct">Cancellation. This is the one exception a caller can see - see the remarks.</param>
    /// <remarks>
    /// Never throws for anything in the file: a truncated header, an unterminated block comment, a
    /// grouped or temporary procedure, an unreadable type argument - each abandons the current header
    /// and scanning resumes. Cancellation is deliberately propagated as
    /// <see cref="OperationCanceledException"/> rather than swallowed: it is not a property of the
    /// file, and returning a partial list would let Roslyn cache a truncated result for an input that
    /// never changed.
    /// </remarks>
    public static List<SqlProcHeader> Parse(string filePath, string text, CancellationToken ct)
    {
        var headers = new List<SqlProcHeader>();
        if (string.IsNullOrEmpty(text))
            return headers;

        var s = new SqlScanner(text);

        try
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();

                s.SkipTrivia();
                if (s.Eof)
                    break;

                var c = s.Peek();

                // Literals and delimited identifiers are opaque: a CREATE PROCEDURE inside one is text.
                if (c == '\'')
                {
                    if (!s.SkipStringLiteral())
                        break;
                    continue;
                }

                if (c == '"')
                {
                    if (!s.SkipDelimited('"'))
                        break;
                    continue;
                }

                if (c == '[')
                {
                    if (!s.SkipDelimited(']'))
                        break;
                    continue;
                }

                if (!SqlScanner.IsWordChar(c))
                {
                    s.Consume(1);
                    continue;
                }

                // A whole identifier run, so that '2CREATE' or '@CREATE' cannot look like a keyword.
                var start = s.Save();
                var word = s.ReadWordRun();
                var isCreate = Eq(word, "CREATE");
                if (!isCreate && !Eq(word, "ALTER"))
                    continue;

                var afterKeyword = s.Save();
                if (TryParseHeader(filePath, s, start, isCreate, out var header))
                {
                    headers.Add(header);
                    continue;
                }

                // Not a procedure, or a malformed one. Resume immediately after the keyword so the rest
                // of the file - and any later procedure in it - is still read.
                s.Restore(afterKeyword);
            }
        }
        catch (Exception ex) when (!(ex is OperationCanceledException))
        {
            // Structurally unreachable: the scanner is bounds-checked throughout and uses no exception
            // for control flow. It is caught anyway because this parser is advisory - it gates nothing
            // and reports nothing - so a defect here must cost the developer a hand-written [Sql],
            // never a crashed compiler. Whatever was read before the fault stands, which is exactly the
            // "abandon this header and move on" behaviour a malformed construct already gets.
        }

        return headers;
    }

    /// <summary>Parses one header, with the scanner positioned just after its CREATE/ALTER keyword.</summary>
    private static bool TryParseHeader(
        string filePath,
        SqlScanner s,
        SqlScannerState start,
        bool isCreate,
        out SqlProcHeader header)
    {
        header = default;

        s.SkipTrivia();
        var word = s.ReadWordRun();

        // CREATE OR ALTER. Plain CREATE and plain ALTER are accepted too: "CREATE OR ALTER only" is a
        // Migrations *deployment* policy and stays in StoredProcedureScript.ParseQualifiedName.
        // Refusing them here would make a legacy corpus contribute nothing.
        if (isCreate && Eq(word, "OR"))
        {
            s.SkipTrivia();
            if (!Eq(s.ReadWordRun(), "ALTER"))
                return false;

            s.SkipTrivia();
            word = s.ReadWordRun();
        }

        if (!Eq(word, "PROCEDURE") && !Eq(word, "PROC"))
            return false;

        if (!TryReadProcedureName(s, out var schema, out var name))
            return false;

        // ';number' procedure grouping. The executor's CommandText is '[schema].[name]' and cannot
        // address a grouped procedure, so the whole header is refused rather than half-read.
        s.SkipTrivia();
        if (s.Peek() == ';')
            return false;

        if (!TryReadParameterList(s, out var parameters))
            return false;

        if (!TryReadHeaderTerminator(s))
            return false;

        header = new SqlProcHeader(
            filePath,
            schema,
            name,
            new EquatableArray<SqlHeaderParam>(parameters.ToArray()),
            new SourceSpanInfo(start.Index, s.LastEnd - start.Index, start.Line, start.Index - start.LineStart));
        return true;
    }

    /// <summary>Reads the (optionally qualified, optionally delimited) procedure name.</summary>
    private static bool TryReadProcedureName(SqlScanner s, out string schema, out string name)
    {
        schema = "dbo";
        name = "";

        s.SkipTrivia();
        if (!s.TryReadName(out var part))
            return false;

        // Keep only the last two parts, so 'db.schema.proc' and 'server.db.schema.proc' still resolve.
        var qualifier = "";
        while (true)
        {
            s.SkipTrivia();
            if (s.Peek() != '.')
                break;

            s.Consume(1);
            s.SkipTrivia();
            if (!s.TryReadName(out var next))
                return false;

            qualifier = part;
            part = next;
        }

        // Temporary procedures (#name, ##name) cannot be addressed as [schema].[name].
        if (part.Length == 0 || part[0] == '#')
            return false;

        name = part;
        if (qualifier.Length != 0)
            schema = qualifier;
        return true;
    }

    /// <summary>Reads the parameter list, bare or wrapped in parentheses. An empty list is legal.</summary>
    private static bool TryReadParameterList(SqlScanner s, out List<SqlHeaderParam> parameters)
    {
        parameters = new List<SqlHeaderParam>();

        s.SkipTrivia();
        var parenthesised = s.Peek() == '(';
        if (parenthesised)
            s.Consume(1);

        while (true)
        {
            s.SkipTrivia();
            if (s.Eof)
                return false;

            var c = s.Peek();
            if (parenthesised && c == ')')
            {
                s.Consume(1);
                return true;
            }

            if (c != '@')
            {
                // A bare list ends where the terminator (AS / WITH / FOR) begins. Inside parentheses,
                // anything but ')' or a parameter is malformed.
                return !parenthesised;
            }

            if (!TryReadParameter(s, out var parameter))
                return false;

            parameters.Add(parameter);

            s.SkipTrivia();
            if (s.Peek() == ',')
            {
                s.Consume(1);
                continue;
            }

            if (!parenthesised)
                return true;

            s.SkipTrivia();
            if (s.Peek() != ')')
                return false;

            s.Consume(1);
            return true;
        }
    }

    /// <summary>
    /// Consumes what closes the parameter list: an optional <c>WITH</c> clause, an optional
    /// <c>FOR REPLICATION</c>, then the body's <c>AS</c>.
    /// </summary>
    private static bool TryReadHeaderTerminator(SqlScanner s)
    {
        s.SkipTrivia();
        var word = s.ReadWordRun();

        if (Eq(word, "WITH"))
        {
            if (!SkipWithClause(s))
                return false;

            s.SkipTrivia();
            word = s.ReadWordRun();
        }

        if (Eq(word, "FOR"))
        {
            s.SkipTrivia();
            if (!Eq(s.ReadWordRun(), "REPLICATION"))
                return false;

            s.SkipTrivia();
            word = s.ReadWordRun();
        }

        return Eq(word, "AS");
    }

    /// <summary>Consumes a whole <c>WITH</c> option list. Unknown option words are tolerated.</summary>
    private static bool SkipWithClause(SqlScanner s)
    {
        while (true)
        {
            s.SkipTrivia();
            var option = s.ReadWordRun();
            if (option.Length == 0)
                return false;

            // EXECUTE AS <CALLER|SELF|OWNER|'user'>: the principal has to be consumed, or the AS that
            // introduces it is mistaken for the AS that opens the body.
            if (Eq(option, "EXECUTE") || Eq(option, "EXEC"))
            {
                s.SkipTrivia();
                if (!Eq(s.ReadWordRun(), "AS"))
                    return false;

                s.SkipTrivia();
                var c = s.Peek();
                if (c == '\'')
                {
                    if (!s.SkipStringLiteral())
                        return false;
                }
                else if (c == '[')
                {
                    if (!s.SkipDelimited(']'))
                        return false;
                }
                else if (c == '"')
                {
                    if (!s.SkipDelimited('"'))
                        return false;
                }
                else if (s.ReadWordRun().Length == 0)
                {
                    return false;
                }
            }

            // RECOMPILE, ENCRYPTION, SCHEMABINDING, NATIVE_COMPILATION and anything else take no argument.
            s.SkipTrivia();
            if (s.Peek() != ',')
                return true;

            s.Consume(1);
        }
    }

    /// <summary>Reads one parameter: name, type, optional type arguments, default and modifiers.</summary>
    private static bool TryReadParameter(SqlScanner s, out SqlHeaderParam parameter)
    {
        parameter = default;

        var start = s.Save();

        // Parameter names are never delimited: '@[Bracket Param]' and '@"Quoted Param"' are not legal
        // T-SQL, so the token is '@' followed by identifier characters, full stop. Delimiter handling
        // is needed for the procedure name and the type name only.
        if (!s.TryReadParameterName(out var name))
            return false;

        s.SkipTrivia();
        if (!s.TryReadName(out var typePart))
            return false;

        var typeQualifier = "";
        while (true)
        {
            s.SkipTrivia();
            if (s.Peek() != '.')
                break;

            s.Consume(1);
            s.SkipTrivia();
            if (!s.TryReadName(out var next))
                return false;

            typeQualifier = typePart;
            typePart = next;
        }

        var arguments = new List<TypeArgument>();
        s.SkipTrivia();
        var hasArguments = s.Peek() == '(';
        if (hasArguments && !TryReadTypeArguments(s, arguments))
            return false;

        var shape = SqlParamShape.Ordinary;
        var typeName = "";
        int? length = null;
        byte? precision = null;
        byte? scale = null;
        var hasNoLengthArgument = false;

        if (typeQualifier.Length != 0 && !Eq(typeQualifier, "sys"))
        {
            // A user-defined type. ProcParamSpec has no field for one and ApplySqlType has no path to
            // bind one, so the parameter is parsed and then declined. A leading 'sys.' / '[sys].' is
            // unwrapped instead, and the unwrapped name is subject to the same table as any other.
            shape = SqlParamShape.UserDefined;
        }
        else if (Eq(typePart, "cursor"))
        {
            shape = SqlParamShape.Cursor;
        }
        else
        {
            // The family table is load-bearing for byte-identical emission: RenderSqlType has a hard
            // precedence (precision beats length, and the two are never emitted together), so routing
            // decimal(18,2)'s arguments to Length would pick the wrong branch and change emitted code.
            switch (Classify(typePart, out var normalised))
            {
                case SqlTypeFamily.Length:
                    typeName = normalised;
                    if (!hasArguments)
                        hasNoLengthArgument = true;
                    else if (arguments.Count > 0)
                        length = arguments[0].IsMax ? -1 : arguments[0].Value;
                    break;

                case SqlTypeFamily.Decimal:
                    typeName = normalised;
                    if (arguments.Count > 0 && !arguments[0].IsMax && arguments[0].Value <= byte.MaxValue)
                        precision = (byte)arguments[0].Value;
                    if (arguments.Count > 1 && !arguments[1].IsMax && arguments[1].Value <= byte.MaxValue)
                        scale = (byte)arguments[1].Value;
                    break;

                case SqlTypeFamily.NoArgument:
                case SqlTypeFamily.FractionalSeconds:
                case SqlTypeFamily.Approximate:
                    // datetime2(7), time(3), datetimeoffset(7) and float(24) are parsed and then not
                    // recorded: RenderSqlType has no scale-without-precision branch, ApplySqlType
                    // ignores the argument for those types, and every FLOAT maps to SqlDbType.Float.
                    typeName = normalised;
                    break;

                default:
                    // A type this parser deliberately does not model. Normalising sysname to
                    // nvarchar(128) here, for instance, would need matching additions to ApplySqlType
                    // and GetAllowedDotNetTypesForSqlType in the same change, or it becomes a third
                    // disagreeing table. Refusing costs nothing: sysname already throws at bind time.
                    typeName = "";
                    break;
            }
        }

        var isOutput = false;
        var hasDefault = false;

        while (true)
        {
            s.SkipTrivia();
            if (s.Eof)
                break;

            if (s.Peek() == '=')
            {
                s.Consume(1);
                hasDefault = true;
                if (!SkipDefaultExpression(s))
                    return false;
                continue;
            }

            if (!SqlScanner.IsWordStart(s.Peek()))
                break;

            var save = s.Save();
            var word = s.ReadWordRun();

            if (Eq(word, "OUT") || Eq(word, "OUTPUT"))
            {
                isOutput = true;
                continue;
            }

            if (Eq(word, "READONLY"))
            {
                shape = SqlParamShape.TableValued;
                continue;
            }

            if (Eq(word, "VARYING") || Eq(word, "NULL"))
                continue;

            if (Eq(word, "NOT"))
            {
                s.SkipTrivia();
                if (Eq(s.ReadWordRun(), "NULL"))
                    continue;

                s.Restore(save);
                break;
            }

            // The terminator (AS / WITH / FOR) or the next statement. Give it back untouched.
            s.Restore(save);
            break;
        }

        if (shape != SqlParamShape.Ordinary)
        {
            typeName = "";
            length = null;
            precision = null;
            scale = null;
            hasNoLengthArgument = false;
        }

        parameter = new SqlHeaderParam(
            name,
            typeName,
            length,
            precision,
            scale,
            isOutput,
            hasDefault,
            hasNoLengthArgument,
            shape,
            new SourceSpanInfo(start.Index, s.LastEnd - start.Index, start.Line, start.Index - start.LineStart));
        return true;
    }

    /// <summary>
    /// Skips a parameter default. The expression is balanced over parentheses and honours literals and
    /// comments, and is never evaluated - only its presence is recorded.
    /// </summary>
    private static bool SkipDefaultExpression(SqlScanner s)
    {
        var depth = 0;

        while (true)
        {
            s.SkipTrivia();
            if (s.Eof)
                return true;

            var c = s.Peek();

            if (c == '(')
            {
                depth++;
                s.Consume(1);
                continue;
            }

            if (c == ')')
            {
                if (depth == 0)
                    return true;

                depth--;
                s.Consume(1);
                continue;
            }

            if (c == ',' && depth == 0)
                return true;

            if (c == '\'')
            {
                if (!s.SkipStringLiteral())
                    return false;
                continue;
            }

            if (c == '"')
            {
                if (!s.SkipDelimited('"'))
                    return false;
                continue;
            }

            if (c == '[')
            {
                if (!s.SkipDelimited(']'))
                    return false;
                continue;
            }

            if (SqlScanner.IsWordStart(c))
            {
                var save = s.Save();
                var word = s.ReadWordRun();

                // NULL is absent on purpose: '= NULL' is a default, not the end of one.
                if (depth == 0
                    && (Eq(word, "OUT") || Eq(word, "OUTPUT") || Eq(word, "READONLY") || Eq(word, "VARYING")
                        || Eq(word, "NOT") || Eq(word, "AS") || Eq(word, "WITH") || Eq(word, "FOR")))
                {
                    s.Restore(save);
                    return true;
                }

                continue;
            }

            s.Consume(1);
        }
    }

    /// <summary>Reads a parenthesised type-argument list. Every argument is an integer or <c>MAX</c>.</summary>
    private static bool TryReadTypeArguments(SqlScanner s, List<TypeArgument> arguments)
    {
        s.Consume(1);

        while (true)
        {
            s.SkipTrivia();
            if (s.Eof)
                return false;

            if (s.Peek() == ')')
            {
                s.Consume(1);
                return arguments.Count > 0;
            }

            if (!TryReadTypeArgument(s, out var argument))
                return false;

            arguments.Add(argument);

            s.SkipTrivia();
            if (s.Peek() == ',')
            {
                s.Consume(1);
                continue;
            }

            if (s.Peek() == ')')
            {
                s.Consume(1);
                return true;
            }

            return false;
        }
    }

    /// <summary>Reads one type argument: a decimal integer, or <c>MAX</c> in any casing.</summary>
    /// <remarks>
    /// A parser concern rather than a lexer one - <c>MAX</c> is a T-SQL type-argument keyword and
    /// <see cref="TypeArgument"/> is this file's private shape - so it sits here rather than on
    /// <see cref="SqlScanner"/>, over the scanner's <see cref="SqlScanner.ReadWordRun"/> and
    /// <see cref="SqlScanner.TryReadInteger"/>. The nine-digit cap moved with the integer read and is
    /// unchanged: both failures abandon the whole header, so where the cursor stops does not matter.
    /// </remarks>
    private static bool TryReadTypeArgument(SqlScanner s, out TypeArgument argument)
    {
        argument = default;

        if (SqlScanner.IsWordStart(s.Peek()))
        {
            if (!Eq(s.ReadWordRun(), "max"))
                return false;

            argument = new TypeArgument(true, 0);
            return true;
        }

        if (!s.TryReadInteger(out var value))
            return false;

        argument = new TypeArgument(false, value);
        return true;
    }

    /// <summary>Normalises a type name and says which family - and therefore which arguments - it takes.</summary>
    private static SqlTypeFamily Classify(string rawTypeName, out string normalised)
    {
        normalised = rawTypeName.Trim().ToLowerInvariant();

        // The only synonym normalisation, and both entries earn their place. 'integer' matters most:
        // ApplySqlType accepts it, but CompareSqlType string-compares the contract's type name against
        // TYPE_NAME(), which returns 'int', so emitting 'integer' would fail startup validation.
        if (normalised == "integer")
            normalised = "int";
        else if (normalised == "dec")
            normalised = "decimal";

        switch (normalised)
        {
            case "char":
            case "varchar":
            case "nchar":
            case "nvarchar":
            case "binary":
            case "varbinary":
                return SqlTypeFamily.Length;

            case "decimal":
            case "numeric":
                return SqlTypeFamily.Decimal;

            case "bit":
            case "tinyint":
            case "smallint":
            case "int":
            case "bigint":
            case "money":
            case "smallmoney":
            case "real":
            case "date":
            case "datetime":
            case "smalldatetime":
            case "uniqueidentifier":
            case "text":
            case "ntext":
            case "image":
            case "xml":
                return SqlTypeFamily.NoArgument;

            case "datetime2":
            case "datetimeoffset":
            case "time":
                return SqlTypeFamily.FractionalSeconds;

            case "float":
                return SqlTypeFamily.Approximate;

            default:
                // timestamp, rowversion, sql_variant, sysname, geography, geometry, hierarchyid and
                // anything unrecognised. Emptied so a caller cannot mistake it for a usable fact.
                normalised = "";
                return SqlTypeFamily.None;
        }
    }

    /// <summary>Keyword comparison. One function, now shared with the body parser.</summary>
    private static bool Eq(string a, string b) => SqlScanner.Eq(a, b);

    /// <summary>Which arguments a SQL type takes, and what they mean.</summary>
    private enum SqlTypeFamily
    {
        /// <summary>A type this parser does not supply facts for.</summary>
        None,

        /// <summary>char, varchar, nchar, nvarchar, binary, varbinary: one argument, the length.</summary>
        Length,

        /// <summary>decimal, numeric: precision then scale.</summary>
        Decimal,

        /// <summary>Types whose arguments, if any, are meaningless here.</summary>
        NoArgument,

        /// <summary>datetime2, datetimeoffset, time: the argument is parsed but not recorded.</summary>
        FractionalSeconds,

        /// <summary>float: the mantissa argument is parsed but not recorded.</summary>
        Approximate,
    }

    /// <summary>One type argument: an integer, or <c>MAX</c>.</summary>
    private readonly struct TypeArgument
    {
        public TypeArgument(bool isMax, int value)
        {
            IsMax = isMax;
            Value = value;
        }

        public bool IsMax { get; }

        public int Value { get; }
    }
}
