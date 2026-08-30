using System.Globalization;
using System.Text;
using Raptor21.EF.Extensions.StoredProcedures.Scripts;
using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Tests;

/// <summary>
/// Covers the two halves of <see cref="EmbeddedScriptApplier"/> that decide <em>which</em> scripts exist
/// and <em>whether</em> one has already been applied: resource discovery and script identity. Everything
/// here runs against a <see cref="FakeResourceAssembly"/> and plain strings, so no test opens a
/// connection, reads a file or embeds a .sql resource of its own.
/// </summary>
public class EmbeddedScriptCatalogTests
{
    /// <summary>
    /// The sample calls both entry points with the same constant but not the same string: Program.cs
    /// appends a dot before calling ApplyEmbeddedScriptsAsync while CatalogDbContext passes it bare to
    /// RegisterStoredProcedures. Both sides normalise with the identical TrimEnd('.') + "." expression,
    /// and they have to, or the migration model and the applied database would disagree about which
    /// scripts exist.
    /// </summary>
    [Theory]
    [InlineData("Fx.Scripts")]
    [InlineData("Fx.Scripts.")]
    [InlineData("Fx.Scripts.....")]
    public void LoadScripts_PrefixTrailingDots_AreNormalised(string prefix)
    {
        var assembly = new FakeResourceAssembly().With("Fx.Scripts.A.sql", "SELECT 1");

        var scripts = EmbeddedScriptApplier.LoadScripts(assembly, prefix);

        var entry = Assert.Single(scripts);
        Assert.Equal("A.sql", entry.Key);
        Assert.Equal("SELECT 1", entry.Value);
    }

    [Fact]
    public void LoadScripts_FilterIsCaseInsensitiveOnPrefixAndExtension()
    {
        // MSBuild derives a resource name from the file's path on disk, so its casing is whatever the
        // author happened to type; matching ordinally would silently skip an "A.SQL" file.
        var assembly = new FakeResourceAssembly()
            .With("Fx.Scripts.a.SQL", "SELECT 1")
            .With("fx.scripts.b.Sql", "SELECT 2")
            .With("Fx.Scripts.c.txt", "not a script")
            .With("Other.Scripts.d.sql", "SELECT 3");

        var scripts = EmbeddedScriptApplier.LoadScripts(assembly, "Fx.Scripts.");

        Assert.Equal(2, scripts.Count);

        // Compared ordinally on purpose. The returned dictionary is OrdinalIgnoreCase, so ContainsKey
        // would pass even if the key had been case-folded on the way in — and the key is what lands in
        // dbo.__DbScriptHistory.ScriptName, where a changed spelling means a re-application.
        Assert.Contains("a.SQL", scripts.Keys, StringComparer.Ordinal);
        Assert.Contains("b.Sql", scripts.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("c.txt", scripts.Keys, StringComparer.Ordinal);
        Assert.DoesNotContain("d.sql", scripts.Keys, StringComparer.Ordinal);
    }

    [Fact]
    public void LoadScripts_ScriptNameIsTheResourceNameMinusThePrefix()
    {
        // MSBuild flattens directory separators into dots, so a file at DbScripts/Sub/A.sql is embedded
        // under exactly this name and the folder structure is not recoverable from it afterwards.
        var assembly = new FakeResourceAssembly().With("Fx.DbScripts.Sub.A.sql", "SELECT 1");

        var scripts = EmbeddedScriptApplier.LoadScripts(assembly, "Fx.DbScripts.");

        // This string is the primary key written to the history table, which is why moving a .sql file
        // between folders re-applies it: the name changes, so the history row no longer matches.
        var entry = Assert.Single(scripts);
        Assert.Equal("Sub.A.sql", entry.Key);
    }

    [Fact]
    public void LoadScripts_NoMatchingResources_ReturnsEmpty()
    {
        var textOnly = new FakeResourceAssembly().With("Fx.Db.readme.txt", "no scripts here");
        Assert.Empty(EmbeddedScriptApplier.LoadScripts(textOnly, "Fx.Db."));

        var elsewhere = new FakeResourceAssembly().With("Fx.Db.a.sql", "SELECT 1");
        Assert.Empty(EmbeddedScriptApplier.LoadScripts(elsewhere, "Other.Db."));

        // "." is neither null nor whitespace, so it survives ApplyEmbeddedScriptsAsync's argument guard
        // and then normalises back to "." — matching nothing. A mistyped prefix is an error nowhere:
        // the caller returns at the scripts.Count == 0 check without opening a connection, and a
        // deployment that applied no script at all is indistinguishable from one that had none to apply.
        Assert.Empty(EmbeddedScriptApplier.LoadScripts(elsewhere, "."));
    }

    /// <summary>
    /// EXPECTED TO FAIL — defect: LoadScripts drops one of two resources whose names collide after
    /// prefix-stripping, with no error. The accumulator is keyed OrdinalIgnoreCase and filled with a
    /// plain indexer assignment, so whichever name enumerates last silently overwrites the other.
    /// Manifest resource names are case-sensitive, so these are two genuinely distinct files; the same
    /// collapse happens without any casing involved for DbScripts/Sub/A.sql against DbScripts/Sub.A.sql,
    /// which flatten to the same "Sub.A.sql". The symptom in production is a validator "does not exist"
    /// error naming a procedure whose .sql file is plainly in the repository. Whether the fix is a throw
    /// or two surviving entries is a product decision; that no script may be lost in silence is not.
    /// The decision on record is the throw. dbo.__DbScriptHistory.ScriptName is a primary key under the
    /// database collation, which is case-insensitive by default, so two entries surviving here would
    /// contend for one history row and both re-run on every deployment.
    /// </summary>
    [Fact]
    public void LoadScripts_NamesCollidingAfterPrefixStrip_Throws()
    {
        var assembly = new FakeResourceAssembly()
            .With("Fx.Db.orders.x.sql", "CREATE OR ALTER PROCEDURE dbo.orders_x AS SELECT 1")
            .With("Fx.Db.Orders.X.sql", "CREATE OR ALTER PROCEDURE dbo.Orders_X AS SELECT 2");

        var ex = Assert.Throws<InvalidOperationException>(
            () => EmbeddedScriptApplier.LoadScripts(assembly, "Fx.Db."));

        // Both resource names, not just the one that lost. Order between them is whatever the assembly
        // enumerates, so assert on presence rather than position.
        Assert.Contains("Fx.Db.orders.x.sql", ex.Message, StringComparison.Ordinal);
        Assert.Contains("Fx.Db.Orders.X.sql", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void LoadScripts_Utf8Bom_IsStrippedBeforeHashing()
    {
        var body = Encoding.UTF8.GetBytes("SELECT 1");
        var assembly = new FakeResourceAssembly()
            .WithBytes("Fx.Db.a.sql", [0xEF, 0xBB, 0xBF, .. body])
            .WithBytes("Fx.Db.b.sql", body);

        var scripts = EmbeddedScriptApplier.LoadScripts(assembly, "Fx.Db.");

        // new StreamReader(stream, Encoding.UTF8) binds the overload that detects byte-order marks, so the
        // mark is consumed rather than surfacing as a leading U+FEFF. RegisterStoredProcedures builds its
        // reader the same way, so the migration side reads the identical text.
        Assert.Equal("SELECT 1", scripts["a.sql"]);
        Assert.Equal(scripts["a.sql"], scripts["b.sql"]);

        // Load-bearing because the hash is the applied/not-applied decision: an editor that re-saves a
        // script with a BOM must not make it look like a changed body and re-run it.
        Assert.Equal(
            EmbeddedScriptApplier.ComputeSha256(scripts["a.sql"]),
            EmbeddedScriptApplier.ComputeSha256(scripts["b.sql"]));
    }

    /// <summary>
    /// EXPECTED TO FAIL — defect: the null-forgiving operator on GetManifestResourceStream turns a null
    /// stream into an ArgumentNullException naming "stream". Returning null is documented behaviour, and
    /// the assembly is a caller-supplied parameter, so this is reachable in the field — a name that
    /// enumerates without bytes. What the consumer then sees mentions neither the resource, the prefix
    /// nor the assembly, from inside a method whose entire job is resource loading.
    /// </summary>
    [Fact]
    public void LoadScripts_ResourceStreamIsNull_ThrowsNamingTheResource()
    {
        var assembly = new FakeResourceAssembly().WithNullStream("Fx.Db.a.sql");

        var ex = Record.Exception(() => EmbeddedScriptApplier.LoadScripts(assembly, "Fx.Db."));

        Assert.NotNull(ex);
        Assert.Contains("Fx.Db.a.sql", ex!.Message);
    }

    [Fact]
    public void ComputeSha256_ReturnsLowercaseHexOfTheUtf8Bytes()
    {
        const string content = "CREATE OR ALTER PROCEDURE dbo.X AS SELECT 1";

        var hash = EmbeddedScriptApplier.ComputeSha256(content);

        // An independent oracle rather than a second run of the implementation: this digest was produced
        // outside .NET with sha256sum over the UTF-8 bytes of the literal above, with no trailing newline.
        Assert.Equal("2d45fbda881ebe35f807c81e503a722aeed6b08262fa9b24e8f8ba2e93efbb26", hash);

        // The length is load-bearing on its own — dbo.__DbScriptHistory.Sha256 is char(64), so a shorter
        // digest would be stored blank-padded and an upper-case one would compare unequal under a
        // case-sensitive collation, in both cases re-applying every script forever.
        Assert.Equal(64, hash.Length);
        Assert.Matches("^[0-9a-f]{64}$", hash);
    }

    [Fact]
    public void ComputeSha256_CrlfAndLfCopies_ProduceDifferentHashes()
    {
        // Escapes, not a raw multi-line literal, so the test states its own line endings instead of
        // inheriting whatever git checked this file out with.
        const string lf = "CREATE OR ALTER PROCEDURE dbo.X AS\nSELECT 1\n";
        var crlf = lf.Replace("\n", "\r\n");

        // PINS CURRENT BEHAVIOUR rather than endorsing it. StoredProcedureDiff.Normalize deliberately does
        // the opposite — it folds CRLF to LF "so CRLF/LF differences don't look like a real body change" —
        // so on a checkout with core.autocrlf=true the two halves of the library disagree about the same
        // file: the differ reports no change while the applier sees a new hash and re-applies every script
        // on every cross-platform deploy. Which identity rule ought to win is a design decision a test
        // must not make; that the disagreement exists is a fact worth freezing, so that a future fix has
        // to arrive here and in StoredProcedureDiff together.
        Assert.NotEqual(
            EmbeddedScriptApplier.ComputeSha256(lf),
            EmbeddedScriptApplier.ComputeSha256(crlf));
    }

    [Fact]
    public void ComputeSha256_UnderTurkishCulture_ProducesTheSameHash()
    {
        var previous = CultureInfo.CurrentCulture;
        string invariantHash;
        string turkishHash;

        // Synchronous by construction: CurrentCulture is per-thread, and with no await in the try block
        // there is no continuation that could resume on another thread and leak tr-TR into a parallel test.
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            invariantHash = EmbeddedScriptApplier.ComputeSha256("SELECT 1");

            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            turkishHash = EmbeddedScriptApplier.ComputeSha256("SELECT 1");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }

        // Honest about its reach: Convert.ToHexString emits only 0-9 and A-F, and "I" is not among them,
        // so a plain ToLower() would agree with ToLowerInvariant on a hex digest and this test cannot tell
        // the two apart. What it does pin is that the whole path from string to digest is culture-free, on
        // the locale that is both the repository owner's own and the classic source of casing bugs — so a
        // future step that is culture-sensitive (a format, a comparison, a normalisation) is caught here
        // rather than by every script re-applying on one developer's machine.
        Assert.Equal(invariantHash, turkishHash);
        Assert.Equal("e004ebd5b5532a4b85984a62f8ad48a81aa3460c1ca07701f386135d72cdecf5", turkishHash);
    }
}
