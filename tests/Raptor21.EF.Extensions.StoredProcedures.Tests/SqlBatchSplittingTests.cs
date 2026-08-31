using Raptor21.EF.Extensions.StoredProcedures.Scripts;
using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Tests;

/// <summary>
/// Covers <see cref="SqlBatch.Split"/>, the runtime half of the one component that decides what a
/// <c>GO</c> batch separator is, and — at the foot of the file — the agreement between it and
/// <see cref="SqlBatch.ContainsSeparator"/>, which is the half the migration side asks.
/// </summary>
/// <remarks>
/// Two components used to decide that question separately and disagreed. The design-time half,
/// <c>StoredProcedureScript.ParseQualifiedName</c>, matched <c>^\s*GO\s*$</c> over the whole text with
/// <c>RegexOptions.IgnoreCase | RegexOptions.Multiline</c> and rejected the file outright if it hit,
/// while the runtime half right-trimmed each line and compared it to the literal <c>"GO"</c>. Where the
/// two disagreed the same .sql file was a hard error at Add-Migration time and a silent corruption at
/// apply time, or the reverse. That regex is gone: ParseQualifiedName asks
/// <see cref="SqlBatch.ContainsSeparator"/>, which shares its separator test and its line scanner with
/// <see cref="SqlBatch.Split"/>, so every case below pins one answer both halves are bound to give.
/// <para>
/// Every input is written with explicit <c>"\n"</c> escapes and every expectation is a literal. An
/// expected value built from <see cref="Environment.NewLine"/> would agree with the implementation by
/// construction and pass on both platforms while proving nothing, which is exactly the bug the join test
/// at the foot of this file exists to catch.
/// </para>
/// </remarks>
public class SqlBatchSplittingTests
{
    /// <summary>
    /// A body in the shape the Migrations package requires: one <c>CREATE OR ALTER PROCEDURE</c> batch
    /// with no separator in it. What <c>ParseQualifiedName</c> refuses is a line that separates, not the
    /// two letters — a GO inside a string literal is content, and <c>IdempotentScriptTests</c> registers a
    /// procedure whose body contains exactly that.
    /// </summary>
    private const string ProcedureBody = """
        CREATE OR ALTER PROCEDURE [dbo].[Product_GetBySku]
            @Sku varchar(32)
        AS
        BEGIN
            SET NOCOUNT ON;
            SELECT Id, Sku, Price, UpdatedUtc
            FROM   dbo.Product
            WHERE  Sku = @Sku;
        END
        """;

    [Fact]
    public void SplitBatches_GoOnItsOwnLine_SplitsIntoTwoBatches()
    {
        var batches = SqlBatch.Split("SELECT 1\nGO\nSELECT 2");

        Assert.Equal(2, batches.Count);
        Assert.Equal("SELECT 1", batches[0]);
        Assert.Equal("SELECT 2", batches[1]);
    }

    [Theory]
    [InlineData("A\ngo\nB")]
    [InlineData("A\nGo\nB")]
    [InlineData("A\ngO\nB")]
    [InlineData("A\nGO\nB")]
    public void SplitBatches_GoIsCaseInsensitive(string sql)
    {
        // The comparison is OrdinalIgnoreCase, matching sqlcmd, which accepts any casing of the keyword.
        // Casing was also the one point the two readers agreed on while they were separate; they now reach
        // this comparison through the same IsSeparator and cannot drift apart on it again.
        Assert.Equal(2, SqlBatch.Split(sql).Count);
    }

    [Theory]
    [InlineData("A\nGO   \nB")]
    [InlineData("A\nGO\t\nB")]
    [InlineData("A\nGO  \r\nB")]
    public void SplitBatches_TrailingWhitespaceAfterGo_IsTolerated(string sql)
    {
        // The comparison reads a trimmed view of the line, so a GO followed by spaces, a tab, or the
        // carriage return orphaned by the '\n' split still separates. Only that '\r' comes off the line
        // itself, because the line is also what a batch gets built from.
        Assert.Equal(2, SqlBatch.Split(sql).Count);
    }

    [Fact]
    public void SplitBatches_CrLfLineEndings_AreHandled()
    {
        var batches = SqlBatch.Split("SELECT 1\r\nGO\r\nSELECT 2\r\n");

        Assert.Equal(2, batches.Count);
        Assert.Equal("SELECT 1", batches[0]);

        // The final "\r\n" leaves an empty trailing line, which joins onto the last batch as a bare
        // newline. The assertion stays a prefix because what this test is about is the separator surviving
        // CRLF endings; which byte the lines are joined with is pinned on its own further down.
        Assert.StartsWith("SELECT 2", batches[1]);
    }

    [Fact]
    public void SplitBatches_LoneCrLineEndings_AreInvisible()
    {
        var batches = SqlBatch.Split("SELECT 1\rGO\rSELECT 2");

        // Pinned deliberately rather than reported as a defect. A classic-Mac file splits into a single
        // enormous line and the GO is never seen. What makes that tolerable is that both halves are blind
        // in identical fashion — the design-time gate and the split are one reader, and it splits on
        // '\n' alone — so the file is consistently a single batch instead of being waved through by one
        // half and cut by the other. That consistency is what to keep if anyone teaches this about '\r'.
        var only = Assert.Single(batches);
        Assert.Contains("GO", only);
    }

    [Fact]
    public void SplitBatches_ConsecutiveOrLeadingGo_ProduceNoEmptyBatches()
    {
        var batches = SqlBatch.Split("GO\nGO\nSELECT 1\nGO\nGO");

        // A separator flushes the buffer, and the flush drops a buffer holding nothing but whitespace, so
        // leading and repeated separators cannot produce an empty batch. This is what makes ApplyOneAsync's
        // IsNullOrWhiteSpace guard redundant.
        var only = Assert.Single(batches);
        Assert.Equal("SELECT 1", only);
    }

    [Theory]
    [InlineData("A\nGO\nB")]
    [InlineData("A\nGO\nB\nGO")]
    [InlineData("A\nGO\nB\nGO\n")]
    public void SplitBatches_ContentAfterTheLastGo_BecomesAFinalBatch(string sql)
    {
        // Whether the file ends mid-batch, on a GO, or on a GO plus a newline, the tail is flushed
        // exactly once and never as an extra empty batch.
        Assert.Equal(2, SqlBatch.Split(sql).Count);
    }

    [Fact]
    public void SplitBatches_ScriptWithNoGo_YieldsExactlyOneBatch()
    {
        Assert.Single(SqlBatch.Split(ProcedureBody));
    }

    [Theory]
    [InlineData("")]
    [InlineData("\n  \n\t\n")]
    [InlineData("   ")]
    public void SplitBatches_EmptyOrWhitespaceOnlyScript_YieldsNoBatches(string sql)
    {
        // Blank lines are kept while a batch is being built, so the buffer here really does fill with the
        // file's own whitespace; the flush is what refuses it, and a caller therefore never sends a
        // command for a blank .sql file.
        Assert.Empty(SqlBatch.Split(sql));
    }

    [Theory]
    [InlineData("SELECT 1\nGO\nSELECT 2")]
    [InlineData("A\nGO   \nB")]
    [InlineData("A\nGO  \r\nB")]
    [InlineData("SELECT 1\r\nGO\r\nSELECT 2\r\n")]
    [InlineData("SELECT 1\rGO\rSELECT 2")]
    [InlineData("GO\nGO\nSELECT 1\nGO\nGO")]
    [InlineData("A\nGO\nB\nGO\n")]
    [InlineData("SELECT 1\n\n\nSELECT 2")]
    [InlineData("\n\n\nSELECT 1/0")]
    [InlineData("")]
    [InlineData("\n  \n\t\n")]
    [InlineData("   ")]
    [InlineData("SELECT 1\n-- GO\nSELECT 2")]
    [InlineData("CREATE TABLE dbo.T (Id int);\n    GO\nCREATE OR ALTER PROCEDURE dbo.X AS SELECT 1")]
    [InlineData("PRINT '\nGO\n';")]
    [InlineData("SELECT 1\n/*\nGO\n*/\nSELECT 2")]
    [InlineData("INSERT INTO T VALUES (1)\nGO 5\n")]
    [InlineData("SELECT 1\nGO -- done\nSELECT 2")]
    [InlineData("SELECT 1\nGO;\nSELECT 2")]
    [InlineData("SET @msg = 'abc   \ndef';")]
    [InlineData("SELECT 1\nSELECT 2")]
    [InlineData("PRINT \"line one\nGO\nline two\";")]
    [InlineData("SELECT \"it's\";\nGO\nSELECT 2;")]
    [InlineData("SELECT \"a\"\"b\";\nGO\nSELECT 2;")]
    [InlineData("-- a stray \" here\nGO\nSELECT 2;")]
    [InlineData(ProcedureBody)]
    public void SplitBatches_NeverReturnsAWhitespaceOnlyBatch(string sql)
    {
        // Swept over every input this file uses, to show by construction that ApplyOneAsync's
        // `if (string.IsNullOrWhiteSpace(batch)) continue;` guard is unreachable: the flush is the only
        // path that adds to the result and it applies that very test before adding, so no emitted batch
        // can be whitespace-only. The guard reads as protection against consecutive GOs, which the flush
        // already prevents by refusing to emit an empty buffer.
        Assert.DoesNotContain(SqlBatch.Split(sql), string.IsNullOrWhiteSpace);
    }

    [Fact]
    public void SplitBatches_GoInsideALineComment_IsNotASeparator()
    {
        // A commented-out GO is content twice over: the trimmed line is "-- GO", which is not equal to
        // "GO", and the scanner consumes the rest of the line so that an apostrophe or a "/*" typed inside
        // a comment cannot open a construct that then swallows the remainder of the file.
        Assert.Single(SqlBatch.Split("SELECT 1\n-- GO\nSELECT 2"));
    }

    [Fact]
    public void SplitBatches_IndentedGo_IsASeparator()
    {
        var batches = SqlBatch.Split(
            "CREATE TABLE dbo.T (Id int);\n    GO\nCREATE OR ALTER PROCEDURE dbo.X AS SELECT 1");

        // An indented GO separates, as it does in sqlcmd. The design-time gate reaches the same test, so
        // this file cannot be rejected at Add-Migration time and then split differently here, nor accepted
        // there and handed to the server with a bare "    GO" left sitting inside a batch. The comparison
        // works on a trimmed copy rather than trimming the line: the untrimmed line is what gets appended
        // to the buffer, so trimming in place would flatten the indentation of every batch.
        Assert.Equal(2, batches.Count);
    }

    [Fact]
    public void SplitBatches_GoInsideAStringLiteral_DoesNotSplit()
    {
        // The scanner carries an open string literal across the line break, so a GO written inside one is
        // content and this stays a single batch instead of being cut into "PRINT '" and "';", two
        // fragments neither of which parses. The design-time gate reads the same state, which is what lets
        // IdempotentScriptTests register a procedure whose body contains a line reading GO.
        Assert.Single(SqlBatch.Split("PRINT '\nGO\n';"));
    }

    [Fact]
    public void SplitBatches_GoInsideABlockComment_DoesNotSplit()
    {
        // The same carried state, for the other construct that outlives a line break. Without it the first
        // batch would end in an unterminated "/*" and the second would open with a stray "*/".
        Assert.Single(SqlBatch.Split("SELECT 1\n/*\nGO\n*/\nSELECT 2"));
    }

    [Theory]
    [InlineData("SELECT 1;\nCREATE TABLE \"my\ntable\nGO\nname\" (a int);\nSELECT 2;", "a delimited identifier")]
    [InlineData("PRINT \"line one\nGO\nline two\";", "a literal under QUOTED_IDENTIFIER OFF")]
    [InlineData("PRINT \"an escaped \"\" quote\nGO\nand then the close\";", "a construct whose \"\" escape precedes the break")]
    public void SplitBatches_GoInsideADoubleQuotedConstruct_DoesNotSplit(string sql, string construct)
    {
        // The fourth construct that outlives a line break, and the one the scanner used to miss. What a
        // "..." means depends on QUOTED_IDENTIFIER — ON, SqlClient's default, it is a delimited
        // identifier; OFF, it is a string literal — and the scanner cannot know which, since the setting
        // can be changed by a batch it has already handed on. It does not have to: under both readings
        // the contents are not statement text, so a line reading GO inside one is content, and cutting
        // there hands the server two fragments neither of which parses.
        var batches = SqlBatch.Split(sql);

        Assert.True(
            batches.Count == 1,
            $"GO inside {construct} is content, not a separator; got {batches.Count} batches.");
    }

    [Theory]
    [InlineData("SELECT \"it's\";\nGO\nSELECT 2;", "an apostrophe")]
    [InlineData("SELECT \"a[b\";\nGO\nSELECT 2;", "an unmatched [")]
    [InlineData("SELECT \"a/*b\";\nGO\nSELECT 2;", "an unmatched /*")]
    [InlineData("SELECT \"a--b\";\nGO\nSELECT 2;", "a --")]
    public void SplitBatches_ContentInsideADoubleQuotedConstruct_DoesNotSwallowALaterGo(string sql, string content)
    {
        // The other half of the same defect, and the more damaging one. Untracked, a double quote let
        // whatever it contained be read as statement text: "it's" opened a string literal that never
        // closed, "a[b" opened a bracketed identifier, "a/*b" opened a block comment — and each of them
        // then swallowed a genuine GO further down the file, merging two batches into one. That is
        // precisely how CREATE OR ALTER PROCEDURE stops being the first statement in its batch, and on
        // the migration side it made ParseQualifiedName accept a multi-batch script it exists to refuse.
        // The last row was already correct — a -- ends at the line break either way — and is here so the
        // new arm cannot regress it into ending the scan from inside a construct.
        var batches = SqlBatch.Split(sql);

        Assert.True(
            batches.Count == 2,
            $"a GO after a closed \"...\" containing {content} is a real separator; got {batches.Count} batches.");
    }

    [Fact]
    public void SplitBatches_DoubleQuotedConstructClosedBeforeTheGo_StillSplits()
    {
        // "a""b" opens, escapes a quote and closes on the same line, so the GO below is a real separator.
        // It split into two batches before the scanner knew what a double quote was, for the wrong
        // reason — nothing was tracked at all — and it has to keep splitting now that something is. This
        // is the case an escape rule written as "" closes the construct would get wrong in the safe-
        // looking direction, by leaving it open and silently merging the two batches.
        var batches = SqlBatch.Split("SELECT \"a\"\"b\";\nGO\nSELECT 2;");

        Assert.Equal(2, batches.Count);
        Assert.Equal("SELECT \"a\"\"b\";", batches[0]);
        Assert.Equal("SELECT 2;", batches[1]);
    }

    [Theory]
    [InlineData("PRINT 'he said \"hi';\nGO\nSELECT 2;", "a string literal")]
    [InlineData("SELECT [a\"b];\nGO\nSELECT 2;", "a bracketed identifier")]
    [InlineData("-- a stray \" here\nGO\nSELECT 2;", "a line comment")]
    [InlineData("/* a stray \" here */\nGO\nSELECT 2;", "a block comment")]
    public void SplitBatches_DoubleQuoteInsideAnotherConstruct_OpensNothing(string sql, string construct)
    {
        // The arm that owns the scanner keeps it: a lone double quote inside a literal, a bracketed
        // identifier or either kind of comment is a character like any other. Were the new arm reachable
        // from inside them it would open a construct with no close anywhere, and the GO below would be
        // swallowed — the very failure the arm was added to fix, arriving from the other direction.
        var batches = SqlBatch.Split(sql);

        Assert.True(
            batches.Count == 2,
            $"a double quote inside {construct} opens nothing; got {batches.Count} batches.");
    }

    [Theory]
    [InlineData("INSERT INTO T VALUES (1)\nGO 5\n")]
    [InlineData("SELECT 1\nGO -- done\nSELECT 2")]
    [InlineData("SELECT 1\nGO;\nSELECT 2")]
    public void SplitBatches_GoWithABatchCountOrTrailingComment_IsLeftInTheBatch(string sql)
    {
        // Pinned on purpose, with no claim that it is right. sqlcmd treats all three as separators, and
        // "GO 5" as a separator with a repeat count; the separator test demands the trimmed line be
        // exactly "GO" and matches none of them, so both questions wave these scripts through and a caller
        // then hands them to the server as syntax errors. Splitting here would silently drop the count and
        // change what the script does, so the resolution is a product decision, not an assertion.
        var only = Assert.Single(SqlBatch.Split(sql));
        Assert.Contains("GO", only);
    }

    [Fact]
    public void SplitBatches_PreservesTrailingWhitespaceInsideAStringLiteral()
    {
        var batches = SqlBatch.Split("SET @msg = 'abc   \ndef';");

        // Nothing but the '\r' orphaned by splitting on '\n' is taken off a line. These three spaces sit
        // inside a multi-line literal, so trimming them would make the value the script assigns silently
        // different from the value in the file — a batch that is not what the developer wrote, differing
        // in exactly the character an editor does not show.
        Assert.Contains("'abc   \n", Assert.Single(batches));
    }

    [Fact]
    public void SplitBatches_JoinsBatchLinesWithLineFeedNotEnvironmentNewLine()
    {
        var batches = SqlBatch.Split("SELECT 1\nSELECT 2");

        // Lines are rejoined with a literal '\n' and not StringBuilder.AppendLine, which writes
        // Environment.NewLine: one .sql file would otherwise become CRLF-joined text from a Windows deploy
        // and LF-joined text from Linux CI, leaving sys.sql_modules.definition — the text any future drift
        // check has to compare against — a record of which machine ran the deployment rather than of what
        // the script says. The expectation is the literal "\n" on purpose: an expected value built from
        // Environment.NewLine would pass vacuously on the ubuntu-latest runner and fail only on a
        // developer's Windows machine.
        Assert.Equal("SELECT 1\nSELECT 2", Assert.Single(batches));
    }

    [Fact]
    public void SplitBatches_PreservesLeadingBlankLinesOfABatch()
    {
        var batches = SqlBatch.Split("\n\n\nSELECT 1/0");

        // A batch keeps the blank lines it opens with, because whether a separator is owed is tracked
        // explicitly instead of being read off the buffer's length. SQL Server therefore reports the
        // divide-by-zero at line 4, where the .sql file has it, rather than at Line 1 and with a different
        // offset for every batch. For a component whose entire job is executing someone else's SQL, and
        // which logs nothing itself, that message is the only diagnostic it has to offer.
        Assert.StartsWith("\n\n\n", Assert.Single(batches));
    }

    [Theory]
    [InlineData("SELECT 1\nGO\nSELECT 2", true)]
    [InlineData("CREATE TABLE dbo.T (Id int);\n    GO\nCREATE OR ALTER PROCEDURE dbo.X AS SELECT 1", true)]
    [InlineData("SELECT 1\n-- GO\nSELECT 2", false)]
    [InlineData("PRINT '\nGO\n';", false)]
    [InlineData("SELECT 1\n/*\nGO\n*/\nSELECT 2", false)]
    [InlineData("PRINT \"line one\nGO\nline two\";", false)]
    [InlineData("SELECT 1;\nCREATE TABLE \"my\ntable\nGO\nname\" (a int);\nSELECT 2;", false)]
    [InlineData("SELECT \"it's\";\nGO\nSELECT 2;", true)]
    [InlineData("SELECT \"a[b\";\nGO\nSELECT 2;", true)]
    [InlineData("SELECT \"a\"\"b\";\nGO\nSELECT 2;", true)]
    [InlineData("SELECT [a\"b];\nGO\nSELECT 2;", true)]
    public void ContainsSeparator_ReachesTheSameVerdictAsSplit(string sql, bool expected)
    {
        // The migration side's half of the same reader. ParseQualifiedName refuses a script that contains
        // a separator, because CREATE OR ALTER PROCEDURE has to be the first statement in its batch, and
        // it asks this method — so a construct one entry point understands and the other does not is a
        // file that registers at Add-Migration time and is then cut into fragments at deployment, or is
        // rejected at build time for a GO that was never a separator. Both entry points reach IsSeparator
        // and ScanLine, so the agreement is by construction; these rows are what would notice if a future
        // change gave one of them a scanner of its own.
        Assert.Equal(expected, SqlBatch.ContainsSeparator(sql));

        // For these inputs, and not as a general law: a separator at the very end of a file is real and
        // still leaves one batch, so the two answers coincide here only because every separating row
        // above has content after its GO.
        Assert.Equal(expected, SqlBatch.Split(sql).Count > 1);
    }
}
