using System.Text;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Raptor21.EF.Extensions.Migrations.Tests;

/// <summary>
/// Covers resource discovery in <see cref="StoredProcedureModelExtensions.RegisterStoredProcedures"/>,
/// which decides <em>which</em> scripts exist and therefore what a migration can carry. Since the runtime
/// script applier was removed this is the library's only loader: a file it silently drops is a procedure
/// that is in the repository, is an <c>EmbeddedResource</c>, and is in no migration.
/// </summary>
/// <remarks>
/// Everything here runs against a <see cref="FakeResourceAssembly"/> and an in-memory
/// <see cref="ModelBuilder"/>, so no test embeds a .sql resource, reads a file or opens a connection.
/// </remarks>
public class RegisterStoredProceduresTests
{
    private const string Prefix = "Fx.Scripts.";

    private static string Body(string name) => $"CREATE OR ALTER PROCEDURE {name} AS SELECT 1;";

    private static ModelBuilder Register(FakeResourceAssembly assembly, string prefix = Prefix)
    {
        var builder = new ModelBuilder();
        builder.RegisterStoredProcedures(assembly, prefix);
        return builder;
    }

    // Read back through the same public accessor the differ uses, over the finalised model, which is the
    // shape RegisterStoredProcedures' output actually reaches StoredProcedureModelDiffer in. Reading
    // builder.Model's annotations directly would pass on a model EF had refused to finalise.
    private static IReadOnlyDictionary<string, string> ScriptsOf(ModelBuilder builder) =>
        StoredProcedureModelExtensions.GetStoredProcedureScripts(builder.FinalizeModel());

    /// <summary>
    /// The prefix is a resource-name prefix, and a caller who spells it with or without the trailing dot
    /// means the same folder. The sample passes it bare from a const; a host that appends the dot itself
    /// must not get a different set of procedures for it.
    /// </summary>
    [Theory]
    [InlineData("Fx.Scripts")]
    [InlineData("Fx.Scripts.")]
    [InlineData("Fx.Scripts.....")]
    public void PrefixTrailingDots_AreNormalised(string prefix)
    {
        var assembly = new FakeResourceAssembly().With("Fx.Scripts.A.sql", Body("dbo.usp_A"));

        var scripts = ScriptsOf(Register(assembly, prefix));

        var entry = Assert.Single(scripts);
        Assert.Equal("dbo.usp_A", entry.Key);
    }

    [Fact]
    public void FilterIsCaseInsensitiveOnPrefixAndExtension()
    {
        // MSBuild derives a resource name from the file's path on disk, so its casing is whatever the
        // author happened to type; matching ordinally would silently skip an "A.SQL" file.
        var assembly = new FakeResourceAssembly()
            .With("Fx.Scripts.a.SQL", Body("dbo.usp_A"))
            .With("fx.scripts.b.Sql", Body("dbo.usp_B"))
            .With("Fx.Scripts.c.txt", "not a script")
            .With("Other.Scripts.d.sql", Body("dbo.usp_D"));

        var scripts = ScriptsOf(Register(assembly));

        Assert.Equal(2, scripts.Count);
        Assert.Contains("dbo.usp_A", scripts.Keys);
        Assert.Contains("dbo.usp_B", scripts.Keys);
        Assert.DoesNotContain("dbo.usp_D", scripts.Keys);
    }

    [Fact]
    public void NoMatchingResources_RegistersNothing()
    {
        var assembly = new FakeResourceAssembly().With("Other.Scripts.A.sql", Body("dbo.usp_A"));

        Assert.Empty(ScriptsOf(Register(assembly)));
    }

    /// <summary>
    /// The annotation key is the procedure's qualified name and not the file's, so two files declaring one
    /// procedure land on one key. Overwriting would drop a file that looks deployed from every angle a
    /// developer can see.
    /// </summary>
    [Fact]
    public void TwoResourcesDeclaringOneProcedure_ThrowsNamingBoth()
    {
        var assembly = new FakeResourceAssembly()
            .With("Fx.Scripts.010_Upsert.sql", Body("dbo.usp_Upsert"))
            .With("Fx.Scripts.020_Upsert_v2.sql", Body("[dbo].[usp_Upsert]"));

        var ex = Assert.Throws<InvalidOperationException>(() => Register(assembly));

        // Both files, not just the loser: naming one leaves the developer hunting for the other. The
        // bracketed spelling is deliberate - the collision is on the parsed name, so it survives a
        // difference in how the two files write it.
        Assert.Contains("010_Upsert.sql", ex.Message, StringComparison.Ordinal);
        Assert.Contains("020_Upsert_v2.sql", ex.Message, StringComparison.Ordinal);
        Assert.Contains("dbo.usp_Upsert", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// An unqualified name defaults to <c>dbo</c>, so <c>usp_X</c> and <c>dbo.usp_X</c> are one procedure
    /// and collide - which is the case a comparison of the written names would miss.
    /// </summary>
    [Fact]
    public void UnqualifiedAndDboQualified_AreTheSameProcedure()
    {
        var assembly = new FakeResourceAssembly()
            .With("Fx.Scripts.A.sql", Body("usp_X"))
            .With("Fx.Scripts.B.sql", Body("dbo.usp_X"));

        var ex = Assert.Throws<InvalidOperationException>(() => Register(assembly));

        Assert.Contains("dbo.usp_X", ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// GetManifestResourceStream is documented to return null and the assembly is a caller-supplied
    /// parameter, so this is reachable. It used to be dereferenced through a null-forgiving operator,
    /// which reported it as a NullReferenceException from inside StreamReader.
    /// </summary>
    [Fact]
    public void ResourceStreamIsNull_ThrowsNamingTheResource()
    {
        var assembly = new FakeResourceAssembly().WithNullStream("Fx.Scripts.A.sql");

        var ex = Assert.Throws<InvalidOperationException>(() => Register(assembly));

        Assert.Contains("Fx.Scripts.A.sql", ex.Message, StringComparison.Ordinal);
        Assert.Contains(nameof(FakeResourceAssembly), ex.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// A .sql file saved from Visual Studio or SSMS carries a UTF-8 byte-order mark, and the mark is not
    /// part of the script. Left in place it precedes <c>CREATE</c>, and the header parse - which anchors
    /// at the start of the text - fails on a file that looks perfectly ordinary in an editor.
    /// </summary>
    [Fact]
    public void Utf8Bom_IsStrippedBeforeTheHeaderIsParsed()
    {
        var body = Body("dbo.usp_A");
        var withBom = new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes(body)).ToArray();
        var assembly = new FakeResourceAssembly().WithBytes("Fx.Scripts.A.sql", withBom);

        var scripts = ScriptsOf(Register(assembly));

        var entry = Assert.Single(scripts);
        Assert.Equal("dbo.usp_A", entry.Key);

        // And the mark is out of the stored text too, not merely tolerated by the parse: the script is
        // what a migration emits, and a stray U+FEFF inside EXEC(N'...') reaches the server.
        Assert.Equal(body, entry.Value);
    }

    [Fact]
    public void ArgumentGuardsFireBeforeAnyResourceIsRead()
    {
        var builder = new ModelBuilder();
        var assembly = new FakeResourceAssembly();

        Assert.Throws<ArgumentNullException>(() => ((ModelBuilder)null!).RegisterStoredProcedures(assembly, Prefix));
        Assert.Equal("assembly", Assert.Throws<ArgumentNullException>(
            () => builder.RegisterStoredProcedures(null!, Prefix)).ParamName);
        Assert.Equal("resourcePrefix", Assert.Throws<ArgumentException>(
            () => builder.RegisterStoredProcedures(assembly, "  ")).ParamName);
    }
}
