using Raptor21.EF.Extensions.StoredProcedures.Scripts;
using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Tests;

/// <summary>
/// Covers <c>EmbeddedScriptApplier.SplitBatches</c>, the runtime half of the recorded GO disagreement.
/// </summary>
/// <remarks>
/// Two components in this repository decide what a GO separator is and they do not agree. The design-time
/// half, <c>StoredProcedureScript.ParseQualifiedName</c>, matches <c>^\s*GO\s*$</c> with
/// <c>RegexOptions.IgnoreCase | RegexOptions.Multiline</c> and rejects the file outright if it hits; the
/// runtime half below right-trims each line and compares it to the literal <c>"GO"</c>. Where the two
/// disagree, the same .sql file is a hard error at Add-Migration time and a silent corruption at apply
/// time, or the reverse — so the tests below pin both halves' shared behaviour and name each divergence.
/// <para>
/// Every input is written with explicit <c>"\n"</c> escapes and every expectation is a literal. An
/// expected value built from <see cref="Environment.NewLine"/> would agree with the implementation by
/// construction and pass on both platforms while proving nothing, which is exactly the bug two of these
/// tests exist to catch.
/// </para>
/// </remarks>
public class SqlBatchSplittingTests
{
    /// <summary>
    /// The only script shape a consumer of the Migrations package can legally ship, since
    /// <c>ParseQualifiedName</c> rejects any script containing a GO at all.
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
        // The comparison is OrdinalIgnoreCase, which is the one point where this and the migration
        // parser's RegexOptions.IgnoreCase provably agree.
        Assert.Equal(2, SqlBatch.Split(sql).Count);
    }

    [Theory]
    [InlineData("A\nGO   \nB")]
    [InlineData("A\nGO\t\nB")]
    [InlineData("A\nGO  \r\nB")]
    public void SplitBatches_TrailingWhitespaceAfterGo_IsTolerated(string sql)
    {
        // The line is right-trimmed before the comparison, so a GO followed by spaces, a tab, or a
        // carriage return orphaned by the '\n' split still separates.
        Assert.Equal(2, SqlBatch.Split(sql).Count);
    }

    [Fact]
    public void SplitBatches_CrLfLineEndings_AreHandled()
    {
        var batches = SqlBatch.Split("SELECT 1\r\nGO\r\nSELECT 2\r\n");

        Assert.Equal(2, batches.Count);
        Assert.Equal("SELECT 1", batches[0]);

        // The final "\r\n" leaves an empty trailing line that is appended to the last batch as a bare
        // newline, so this deliberately asserts a prefix: an exact expectation here would have to name
        // Environment.NewLine and would therefore assert nothing about the join.
        Assert.StartsWith("SELECT 2", batches[1]);
    }

    [Fact]
    public void SplitBatches_LoneCrLineEndings_AreInvisible()
    {
        var batches = SqlBatch.Split("SELECT 1\rGO\rSELECT 2");

        // Pinned deliberately rather than reported as a defect. A classic-Mac file splits into a single
        // enormous line and the GO is never seen; the migration parser is blind in exactly the same way,
        // because .NET's Multiline '^' anchors after '\n' and not after '\r'. The two halves being
        // consistently wrong matters before anyone "fixes" one of them in isolation.
        var only = Assert.Single(batches);
        Assert.Contains("GO", only);
    }

    [Fact]
    public void SplitBatches_ConsecutiveOrLeadingGo_ProduceNoEmptyBatches()
    {
        var batches = SqlBatch.Split("GO\nGO\nSELECT 1\nGO\nGO");

        // A GO seen while the buffer is empty is discarded, so leading and repeated separators cannot
        // produce an empty batch. This is what makes ApplyOneAsync's IsNullOrWhiteSpace guard redundant.
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
        // Every line right-trims to nothing and nothing is ever appended to an empty buffer, so the
        // applier never sends a command for a blank .sql file.
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
    [InlineData(ProcedureBody)]
    public void SplitBatches_NeverReturnsAWhitespaceOnlyBatch(string sql)
    {
        // Swept over every input this file uses, to show by construction that ApplyOneAsync's
        // `if (string.IsNullOrWhiteSpace(batch)) continue;` guard is unreachable: the buffer only ever
        // becomes non-empty by appending a right-trimmed line, and a line that right-trims to nothing
        // leaves an empty buffer empty, so every emitted batch holds at least one non-whitespace
        // character. The guard reads as protection against consecutive GOs, which the separator branch
        // already prevents by refusing to flush an empty buffer.
        Assert.DoesNotContain(SqlBatch.Split(sql), string.IsNullOrWhiteSpace);
    }

    [Fact]
    public void SplitBatches_GoInsideALineComment_IsNotASeparator()
    {
        // Right, but incidentally so: "-- GO" survives only because the comparison demands exact
        // equality, not because anything here understands comments. The next two tests show the cost.
        Assert.Single(SqlBatch.Split("SELECT 1\n-- GO\nSELECT 2"));
    }

    [Fact]
    public void SplitBatches_IndentedGo_IsASeparator()
    {
        var batches = SqlBatch.Split(
            "CREATE TABLE dbo.T (Id int);\n    GO\nCREATE OR ALTER PROCEDURE dbo.X AS SELECT 1");

        // DEFECT: SplitBatches does not trim leading whitespace before the GO comparison while the
        // migration parser's ^\s*GO\s* does. Only the end of the line is trimmed and exact equality is
        // then demanded, so the literal "    GO" stays inside the batch and SQL Server rejects it. The
        // same file is therefore a hard rejection at Add-Migration time and a server-side syntax error
        // at apply time. Note for whoever fixes this: TrimEnd -> Trim on that line is wrong, because the
        // same local is what gets appended to the buffer, so every batch would lose its indentation —
        // the comparison needs its own trimmed copy.
        Assert.Equal(2, batches.Count);
    }

    [Fact]
    public void SplitBatches_GoInsideAStringLiteral_DoesNotSplit()
    {
        // DEFECT: SplitBatches keeps no lexer state — no quote tracking, no bracket-identifier tracking —
        // so a GO on its own line inside a string literal cuts this valid script into "PRINT '" and "';",
        // two batches that are each invalid T-SQL. Handed the same text the migration parser rejects the
        // file outright, so identical input is a loud failure on one side and silent corruption on the
        // other.
        Assert.Single(SqlBatch.Split("PRINT '\nGO\n';"));
    }

    [Fact]
    public void SplitBatches_GoInsideABlockComment_DoesNotSplit()
    {
        // DEFECT: the same missing lexer state. Today this yields two batches, the first of which ends
        // in an unterminated "/*" and the second of which opens with a stray "*/".
        Assert.Single(SqlBatch.Split("SELECT 1\n/*\nGO\n*/\nSELECT 2"));
    }

    [Theory]
    [InlineData("INSERT INTO T VALUES (1)\nGO 5\n")]
    [InlineData("SELECT 1\nGO -- done\nSELECT 2")]
    [InlineData("SELECT 1\nGO;\nSELECT 2")]
    public void SplitBatches_GoWithABatchCountOrTrailingComment_IsLeftInTheBatch(string sql)
    {
        // Pinned on purpose, with no claim that it is right. sqlcmd treats all three as separators, and
        // "GO 5" as a separator with a repeat count; the migration parser's ^\s*GO\s* matches none of
        // them either, so these are scripts the design-time gate waves through and the applier then
        // hands to the server as syntax errors. Splitting here would silently drop the repeat count and
        // change what the script does, so the resolution is a product decision, not an assertion.
        var only = Assert.Single(SqlBatch.Split(sql));
        Assert.Contains("GO", only);
    }

    [Fact]
    public void SplitBatches_PreservesTrailingWhitespaceInsideAStringLiteral()
    {
        var batches = SqlBatch.Split("SET @msg = 'abc   \ndef';");

        // DEFECT: the unconditional second TrimEnd() is applied to the very line that is appended to the
        // buffer, so the three spaces inside this multi-line literal are deleted and the server stores
        // 'abc\ndef'. The value the script assigns is silently different from the value in the file —
        // and because ComputeSha256 hashes the original content, the text executed is never the text
        // hashed, so the history table records a checksum for SQL that was never run.
        Assert.Contains("'abc   \n", Assert.Single(batches));
    }

    [Fact]
    public void SplitBatches_JoinsBatchLinesWithLineFeedNotEnvironmentNewLine()
    {
        var batches = SqlBatch.Split("SELECT 1\nSELECT 2");

        // DEFECT: lines are rejoined with StringBuilder.AppendLine, which writes Environment.NewLine, so
        // one .sql file becomes CRLF-joined text from a Windows deploy and LF-joined text from Linux CI.
        // sys.sql_modules.definition — the text any future drift check would have to compare against — is
        // therefore already platform-dependent before that check exists. The expectation is the literal
        // "\n" on purpose: an expected value built from Environment.NewLine would pass vacuously on the
        // ubuntu-latest runner and fail only on a developer's Windows machine.
        Assert.Equal("SELECT 1\nSELECT 2", Assert.Single(batches));
    }

    [Fact]
    public void SplitBatches_PreservesLeadingBlankLinesOfABatch()
    {
        var batches = SqlBatch.Split("\n\n\nSELECT 1/0");

        // DEFECT: a blank line contributes nothing while the buffer is still empty, so a batch's leading
        // blank lines are swallowed and this batch starts at "SELECT 1/0". SQL Server then reports the
        // divide-by-zero at Line 1 while the .sql file has it at line 4, with a different offset for
        // every batch. For a component whose entire job is executing someone else's SQL, and which logs
        // nothing itself, that discards the only diagnostic it had to offer.
        Assert.StartsWith("\n\n\n", Assert.Single(batches));
    }
}
