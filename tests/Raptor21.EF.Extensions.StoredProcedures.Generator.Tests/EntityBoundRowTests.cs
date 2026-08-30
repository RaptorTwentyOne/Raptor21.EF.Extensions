using System.Reflection;
using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator.Tests;

/// <summary>
/// <c>[SqlRow(Entity = typeof(X))]</c> end to end: what it emits, what it refuses, and what it leaves
/// exactly as it was.
/// </summary>
/// <remarks>
/// <para>
/// The feature's two guarantees are asserted here rather than argued. Byte-identity is structural -
/// <c>RowResolver.Resolve</c> returns its input reference for a row that states no entity, so a
/// hand-written positional record travels the path it travels today and the model index cannot reach its
/// emitted text even in principle - and the test for it compares full generated text with and without a
/// snapshot in the compilation. Zero-new-errors is structural too: <c>Entity</c> is a new named property
/// on an attribute the generator emits into the consumer's own compilation, and no source that compiles
/// today can spell it.
/// </para>
/// <para>
/// The emitted member type is the model's STORE type, not the class's. That is not a compromise: a
/// result-set reader hands back the stored value, and turning a <c>string</c> into an
/// <c>OrderStatus</c> needs the value converter, which is an EF object this generator neither
/// references nor could execute. Where the two differ the binding is refused by name rather than
/// silently resolved, which is what several tests below pin.
/// </para>
/// <para>
/// Member order is the model's - primary key first, then alphabetical - because
/// <c>StoredProcedureValidator</c> compares result columns POSITIONALLY while the generated reader looks
/// them up by name. A SELECT in another order therefore fails with a column-name mismatch rather than
/// with a wrong value, which is why the order is a documented contract and not an implementation detail.
/// </para>
/// </remarks>
public class EntityBoundRowTests
{
    private static string Norm(string text) => text.Replace("\r\n", "\n");

    private static void AssertNoDiagnostics(RunResult result) =>
        Assert.True(result.GeneratorDiagnostics.IsDefaultOrEmpty, result.DiagnosticSummary());

    private static void AssertCompiles(RunResult result) =>
        Assert.True(result.OutputErrors.IsDefaultOrEmpty, result.OutputErrorSummary());

    /// <summary>One compilation: the EF vocabulary, the probe types, one snapshot and the row types.</summary>
    private static RunResult Run(string types, string buildModelBody, string rows) =>
        GeneratorTestHarness.Run(EfSnapshotShim.Compose(
            EfSnapshotShim.Types(types),
            EfSnapshotShim.Snapshot("ProbeSnapshot", buildModelBody),
            EfSnapshotShim.Rows(rows)));

    /// <summary>A procedure group whose one method returns the named row, so the ColumnSpec[] is emitted too.</summary>
    /// <remarks>
    /// The row partial alone proves what the reader materialises; the <c>ColumnSpec[]</c> is the other
    /// half of the contract and the half <c>StoredProcedureValidator</c> checks at startup. Several
    /// assertions here are about the two agreeing, which needs both in the same run.
    /// </remarks>
    private static string Group(string rowTypeName) => $$"""
            [StoredProcedureGroup]
            public partial class Procs
            {
                [StoredProcedure("dbo.Thing_List")]
                public partial Task<IReadOnlyList<{{rowTypeName}}>> ListAsync(CancellationToken ct = default);
            }
        """;

    private static string MessageOf(RunResult result, string id) => Assert.Single(result.WithId(id)).GetMessage();

    /// <summary>The slice of the generated text from <paramref name="start"/> through <paramref name="end"/>.</summary>
    private static string Between(string text, string start, string end)
    {
        var normalized = Norm(text);
        var from = normalized.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"expected to find '{start}' in:\n{normalized}");
        var to = normalized.IndexOf(end, from + start.Length, StringComparison.Ordinal);
        Assert.True(to >= 0, $"expected to find '{end}' after '{start}' in:\n{normalized}");
        return normalized.Substring(from, to - from + end.Length);
    }

    private static string ReaderBody(string text, string rowTypeName) =>
        Between(text, $"public static {rowTypeName} FromDataRecord", "\n        }");

    private static string ColumnSpecArray(string text) =>
        Between(text, "new global::Raptor21.EF.Extensions.StoredProcedures.Contracts.ColumnSpec[]", "});");

    // ---------------------------------------------------------------- the headline

    private const string ProductTypes = """
            public class Product
            {
                public int Id { get; set; }
                public string Name { get; set; }
                public decimal Price { get; set; }
                public string Sku { get; set; }
                public DateTime UpdatedUtc { get; set; }
            }
        """;

    /// <summary>The model's own order: primary key first, then alphabetical.</summary>
    private static readonly string ProductBlock = EfSnapshotShim.Block("Probe.Product", """
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("int");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("varchar(128)");

                    b.Property<decimal>("Price")
                        .HasColumnType("decimal(18,2)");

                    b.Property<string>("Sku")
                        .IsRequired()
                        .HasColumnType("varchar(32)");

                    b.Property<DateTime>("UpdatedUtc")
                        .HasColumnType("datetime");

                    b.HasKey("Id");

                    b.ToTable("Product", (string)null);
        """);

    [Fact]
    public void SoleOccupantTableProducesTheSameTextAsTheHandWrittenPositionalRecord()
    {
        // The claim the feature is measured by, asserted by comparing text rather than argued. The two
        // runs cannot be compared file for file - the entity-bound partial carries the primary constructor
        // the generator wrote and the hand-written one does not - so what is compared is everything the
        // row means at runtime: the reader's body and the contract's column list, which are the two
        // things a wrong binding would move.
        var bound = Run(
            ProductTypes,
            ProductBlock,
            EfSnapshotShim.BoundRow("ProductRow", "Product") + "\n\n" + Group("ProductRow"));

        var handWritten = GeneratorTestHarness.Run(EfSnapshotShim.Compose(EfSnapshotShim.Rows("""
                [SqlRow]
                public partial record ProductRow(
                    int Id,
                    string Name,
                    decimal Price,
                    string Sku,
                    DateTime UpdatedUtc);
            """ + "\n\n" + Group("ProductRow"))));

        AssertNoDiagnostics(bound);
        AssertNoDiagnostics(handWritten);
        AssertCompiles(bound);
        AssertCompiles(handWritten);

        Assert.Equal(
            ReaderBody(handWritten.GeneratedText, "ProductRow"),
            ReaderBody(bound.GeneratedText, "ProductRow"));
        Assert.Equal(
            ColumnSpecArray(handWritten.GeneratedText),
            ColumnSpecArray(bound.GeneratedText));

        // And the parameter list the generator wrote, which is the one thing the hand-written form has
        // that the bound form did not have to be given.
        Assert.Contains(Norm("""
                partial record ProductRow(
                    int Id,
                    string Name,
                    decimal Price,
                    string Sku,
                    global::System.DateTime UpdatedUtc) : global::Raptor21.EF.Extensions.StoredProcedures.Execution.IFromDataRecord<ProductRow>
            """).TrimEnd(), Norm(bound.GeneratedText));
    }

    [Fact]
    public void KeylessEntityWithToViewBindsAndNeedsNoTable()
    {
        // The owner's explicit requirement, and the reason Item 1 is stated over an entity's columns
        // rather than over a table: a keyless type declared HasNoKey().ToView(...) - EF's designated way
        // to declare a raw-SQL or procedure result shape, and the shape a team arriving from FromSqlRaw
        // already has - owns no table, shares nothing, and therefore has nothing that can diverge.
        //
        // HasNoKey is never written into a snapshot at all, so keylessness here is the ABSENCE of HasKey
        // in a block with no HasBaseType. The view is never queried and never created - EF excludes
        // ToView from migrations - so the object need not exist in the database.
        var result = Run(
            types: """
                    public class ProductListResult
                    {
                        public int Id { get; set; }
                        public string Name { get; set; }
                        public decimal Price { get; set; }
                        public string Sku { get; set; }
                    }
                """,
            buildModelBody: EfSnapshotShim.Block("Probe.ProductListResult", """
                            b.Property<int>("Id").HasColumnType("int");

                            b.Property<string>("Name").IsRequired().HasColumnType("varchar(128)");

                            b.Property<decimal>("Price").HasColumnType("decimal(18,2)");

                            b.Property<string>("Sku").IsRequired().HasColumnType("varchar(32)");

                            b.ToTable((string)null);

                            b.ToView("vw_ProductList", (string)null);
                """),
            rows: EfSnapshotShim.BoundRow("ProductListRow", "ProductListResult"));

        AssertNoDiagnostics(result);
        AssertCompiles(result);
        Assert.Contains(Norm("""
                partial record ProductListRow(
                    int Id,
                    string Name,
                    decimal Price,
                    string Sku) : global::Raptor21.EF.Extensions.StoredProcedures.Execution.IFromDataRecord<ProductListRow>
                {
                    public static ProductListRow FromDataRecord(global::System.Data.IDataRecord record)
                    {
                        return new ProductListRow(
                            record.GetInt32(record.GetOrdinal("Id")),
                            record.IsDBNull(record.GetOrdinal("Name")) ? null : record.GetString(record.GetOrdinal("Name")),
                            record.GetDecimal(record.GetOrdinal("Price")),
                            record.IsDBNull(record.GetOrdinal("Sku")) ? null : record.GetString(record.GetOrdinal("Sku")));
                    }
                }
            """).TrimEnd(), Norm(result.GeneratedText));
    }

    // ---------------------------------------------------------------- nullability, both directions

    [Fact]
    public void ValueTypeNullabilityFollowsTheGenericArgumentAndReferenceTypeFollowsIsRequiredOrHasKey()
    {
        // The exact inverse of what CSharpSnapshotGenerator emits. For a VALUE type the column is nullable
        // if and only if the Property<T> argument carries '?', and IsRequired() is irrelevant. For a
        // REFERENCE type the column is NOT NULL if IsRequired() is present OR the property is the key -
        // and the second half is the inverse trap: a string primary key is emitted with NO IsRequired()
        // and its column is NOT NULL, because the emitter's condition is `false != (true && false)` and
        // the call is skipped. 'Code' below is that case.
        //
        // Reference reads are IsDBNull-guarded either way and the validator exempts reference types from
        // its nullability check, so the reference half changes no emitted byte - which is precisely why
        // it has to be written down somewhere before a later feature invents a different rule.
        var result = Run(
            types: """
                    public class Reading
                    {
                        public string Code { get; set; }
                        public int A { get; set; }
                        public int? B { get; set; }
                        public string C { get; set; }
                    }
                """,
            buildModelBody: EfSnapshotShim.Block("Probe.Reading", """
                            b.Property<string>("Code").HasColumnType("varchar(16)");

                            b.Property<int>("A").HasColumnType("int");

                            b.Property<int?>("B").HasColumnType("int");

                            b.Property<string>("C").HasColumnType("varchar(64)");

                            b.HasKey("Code");

                            b.ToTable("Reading", (string)null);
                """),
            rows: EfSnapshotShim.BoundRow("ReadingRow", "Reading") + "\n\n" + Group("ReadingRow"));

        AssertNoDiagnostics(result);
        AssertCompiles(result);

        Assert.Contains(Norm("""
                partial record ReadingRow(
                    string Code,
                    int A,
                    int? B,
                    string C) : global::Raptor21.EF.Extensions.StoredProcedures.Execution.IFromDataRecord<ReadingRow>
                {
                    public static ReadingRow FromDataRecord(global::System.Data.IDataRecord record)
                    {
                        return new ReadingRow(
                            record.IsDBNull(record.GetOrdinal("Code")) ? null : record.GetString(record.GetOrdinal("Code")),
                            record.GetInt32(record.GetOrdinal("A")),
                            record.IsDBNull(record.GetOrdinal("B")) ? (int?)null : record.GetInt32(record.GetOrdinal("B")),
                            record.IsDBNull(record.GetOrdinal("C")) ? null : record.GetString(record.GetOrdinal("C")));
                    }
                }
            """).TrimEnd(), Norm(result.GeneratedText));

        // typeof(string), never typeof(string?): the ColumnSpec renders it inside typeof(), where an
        // annotated reference type is CS8639.
        var specs = ColumnSpecArray(result.GeneratedText);
        Assert.Contains("ColumnSpec(\"Code\", typeof(string))", specs);
        Assert.Contains("ColumnSpec(\"A\", typeof(int))", specs);
        Assert.Contains("ColumnSpec(\"B\", typeof(int?))", specs);
        Assert.Contains("ColumnSpec(\"C\", typeof(string))", specs);
        Assert.DoesNotContain("typeof(string?)", result.GeneratedText);
    }

    [Fact]
    public void HasColumnNameNamesTheColumnAndLeavesTheMemberNamed()
    {
        // The member is the model's property name and the column is HasColumnName ?? that name. This is
        // also where SPG023's soundness is cashed in: "column name = HasColumnName ?? property name" is
        // universally true only because owned and complex types - the sole constructs whose column names
        // carry an undeclared navigation prefix the snapshot never writes down - are refused outright.
        var result = Run(
            types: """
                    public class Legacy
                    {
                        public int Id { get; set; }
                        public string Name { get; set; }
                    }
                """,
            buildModelBody: EfSnapshotShim.Block("Probe.Legacy", """
                            b.Property<int>("Id").HasColumnName("legacy_id").HasColumnType("int");

                            b.Property<string>("Name").IsRequired().HasColumnName("legacy_name").HasColumnType("varchar(64)");

                            b.HasKey("Id");

                            b.ToTable("Legacy", (string)null);
                """),
            rows: EfSnapshotShim.BoundRow("LegacyRow", "Legacy") + "\n\n" + Group("LegacyRow"));

        AssertNoDiagnostics(result);
        AssertCompiles(result);

        Assert.Contains("record.GetInt32(record.GetOrdinal(\"legacy_id\"))", result.GeneratedText);
        Assert.Contains("record.GetString(record.GetOrdinal(\"legacy_name\"))", result.GeneratedText);
        Assert.Contains("ColumnSpec(\"legacy_id\", typeof(int))", result.GeneratedText);
        Assert.Contains("ColumnSpec(\"legacy_name\", typeof(string))", result.GeneratedText);

        // The members keep the model's property names, which is what the developer's own code sees.
        Assert.Contains(Norm("""
                partial record LegacyRow(
                    int Id,
                    string Name)
            """).TrimEnd(), Norm(result.GeneratedText));
    }

    [Fact]
    public void ShadowPropertyWithNoClassMemberIsSkippedSilently()
    {
        // The class is the authority on which members exist; the model is the authority on the column
        // each one maps to. A shadow property, a shadow foreign key or a TPH discriminator has no class
        // member, so it is skipped - and in silence, because a row that did not ask about it is not told
        // anything. The skip shortens BOTH halves of the contract at once, which is the property that
        // matters: a ColumnSpec[] shorter than the row's arity is clean at build and fatal at startup.
        var result = Run(
            types: """
                    public class Note
                    {
                        public int Id { get; set; }
                        public string Text { get; set; }
                    }
                """,
            buildModelBody: EfSnapshotShim.Block("Probe.Note", """
                            b.Property<int>("Id").HasColumnType("int");

                            b.Property<int>("OwnerId").HasColumnType("int");

                            b.Property<string>("ShadowNote").HasColumnType("varchar(16)");

                            b.Property<string>("Text").IsRequired().HasColumnType("varchar(64)");

                            b.HasKey("Id");

                            b.ToTable("Note", (string)null);
                """),
            rows: EfSnapshotShim.BoundRow("NoteRow", "Note") + "\n\n" + Group("NoteRow"));

        AssertNoDiagnostics(result);
        AssertCompiles(result);
        Assert.DoesNotContain("ShadowNote", result.GeneratedText);
        Assert.DoesNotContain("OwnerId", result.GeneratedText);

        Assert.Contains(Norm("""
                partial record NoteRow(
                    int Id,
                    string Text)
            """).TrimEnd(), Norm(result.GeneratedText));

        // Two members, two ColumnSpecs. The two lists are produced by one enumeration over one filtered
        // list precisely so they cannot shrink apart.
        var specs = ColumnSpecArray(result.GeneratedText);
        Assert.Equal(2, specs.Split("ColumnSpec(\"").Length - 1);
    }

    // ---------------------------------------------------------------- the refusals

    /// <summary>
    /// Every construct the ambiguity rule refuses, each reporting its own diagnostic and emitting no row.
    /// </summary>
    /// <remarks>
    /// The subject and its sibling share a shape so that only the mapping statements vary. What is being
    /// pinned is not that the reader dislikes these constructs but that each one is named: a developer
    /// who gets SPG024 knows to write the row out by hand, and a developer who gets nothing at all knows
    /// only that the generator did nothing.
    /// </remarks>
    [Theory]
    [InlineData("SPG023", "b.ToTable(\"Shared\", (string)null);", "b.ToTable(\"Shared\", (string)null);")]
    [InlineData("SPG023", "b.ToView(\"vShared\");", "b.ToView(\"vShared\");")]
    [InlineData("SPG024", "b.HasBaseType(\"Probe.Sibling\");", "b.ToTable(\"Sibling\", (string)null);")]
    [InlineData("SPG024", "b.ToTable(\"Subject\", (string)null);", "b.HasBaseType(\"Probe.Subject\");")]
    [InlineData("SPG026", "b.ToTable(\"Subject\", (string)null); b.ComplexProperty<Money>(\"Total\", \"Probe.Subject.Total#Money\", b1 => { b1.Property<decimal>(\"Amount\").HasColumnType(\"decimal(18,2)\"); });", "b.ToTable(\"Sibling\", (string)null);")]
    [InlineData("SPG026", "b.ToTable(\"Subject\", (string)null); b.SplitToTable(\"SubjectDetails\", t => { t.Property(\"Id\").HasColumnName(\"SubjectId\"); });", "b.ToTable(\"Sibling\", (string)null);")]
    [InlineData("SPG026", "b.ToTable(\"Subject\", (string)null);", "b.ToTable(TableNames.Legacy);")]
    public void EveryRefusalConstructReportsItsOwnDiagnosticAndEmitsNoRow(string expectedId, string subjectMapping, string siblingMapping)
    {
        var result = Run(
            types: """
                    public class Subject { public int Id { get; set; } }

                    public class Sibling { public int Id { get; set; } }

                    public class Money { public decimal Amount { get; set; } }
                """,
            buildModelBody:
                EfSnapshotShim.Block("Probe.Subject", """
                            b.Property<int>("Id").HasColumnType("int");
                """ + "\n            " + subjectMapping)
                + EfSnapshotShim.Block("Probe.Sibling", """
                            b.Property<int>("Id").HasColumnType("int");
                """ + "\n            " + siblingMapping),
            rows: EfSnapshotShim.BoundRow("SubjectRow", "Subject"));

        var diagnostic = Assert.Single(result.GeneratorDiagnostics);
        Assert.Equal(expectedId, diagnostic.Id);
        Assert.Equal(Microsoft.CodeAnalysis.DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.Contains("Probe.Subject", diagnostic.GetMessage());
        Assert.DoesNotContain("partial record SubjectRow", result.GeneratedText);
    }

    /// <summary>
    /// The three type tables that have to be intersected, each catching what the others let through.
    /// </summary>
    /// <remarks>
    /// The reader's getter table admits a <c>Property&lt;int&gt;</c> on a <c>varchar</c> column that the
    /// validator rejects; the validator's allowed-types table admits <c>time</c> to <c>TimeSpan</c> and
    /// <c>datetimeoffset</c> to <c>DateTimeOffset</c>, for which this generator emits no reader at all;
    /// and the model's own <c>HasColumnType</c> is what names the SQL side of both. No two of the three
    /// are sufficient.
    /// </remarks>
    [Theory]
    [InlineData("SPG030", "public TimeSpan Duration { get; set; }", "b.Property<TimeSpan>(\"Duration\").HasColumnType(\"time\");")]
    [InlineData("SPG030", "public DateTimeOffset At { get; set; }", "b.Property<DateTimeOffset>(\"At\").HasColumnType(\"datetimeoffset\");")]
    [InlineData("SPG031", "public int Code { get; set; }", "b.Property<int>(\"Code\").HasColumnType(\"varchar(32)\");")]
    [InlineData("SPG031", "public string Doc { get; set; }", "b.Property<string>(\"Doc\").IsRequired();")]
    [InlineData("SPG030", "public List<string> Tags { get; set; }", "b.PrimitiveCollection<string>(\"Tags\").HasColumnType(\"nvarchar(max)\");")]
    public void PerPropertyRefusalsCoverTheThreeTablesThatMustBeIntersected(string expectedId, string classMember, string propertyStatement)
    {
        var result = Run(
            types: "    public class Probed\n    {\n        public int Id { get; set; }\n" + classMember + "\n    }",
            buildModelBody: EfSnapshotShim.Block("Probe.Probed", """
                            b.Property<int>("Id").HasColumnType("int");
                """ + "\n            " + propertyStatement + """

                            b.HasKey("Id");

                            b.ToTable("Probed", (string)null);
                """),
            rows: EfSnapshotShim.BoundRow("ProbedRow", "Probed"));

        var diagnostic = Assert.Single(result.GeneratorDiagnostics);
        Assert.Equal(expectedId, diagnostic.Id);
        Assert.Contains("Probe.Probed", diagnostic.GetMessage());
        Assert.DoesNotContain("partial record ProbedRow", result.GeneratedText);
    }

    [Fact]
    public void PerPropertyRefusalMessagesNameTheCauseRatherThanJustTheProperty()
    {
        // The messages carry the sentence a developer needs, and each arm carries a different one. This is
        // the one place they are read, because a refusal that does not say why leaves the developer
        // looking at a generator that did nothing.
        var noReader = Run(
            types: "    public class Probed\n    {\n        public int Id { get; set; }\n        public TimeSpan Duration { get; set; }\n    }",
            buildModelBody: EfSnapshotShim.Block("Probe.Probed", """
                            b.Property<int>("Id").HasColumnType("int");

                            b.Property<TimeSpan>("Duration").HasColumnType("time");

                            b.HasKey("Id");

                            b.ToTable("Probed", (string)null);
                """),
            rows: EfSnapshotShim.BoundRow("ProbedRow", "Probed"));

        var message = MessageOf(noReader, "SPG030");
        Assert.Contains("Property 'Duration' of entity type 'Probe.Probed' is stored as 'global::System.TimeSpan'", message);
        Assert.Contains("'time' binds TimeSpan and 'datetimeoffset' binds DateTimeOffset", message);

        var wrongColumn = Run(
            types: "    public class Probed\n    {\n        public int Id { get; set; }\n        public int Code { get; set; }\n    }",
            buildModelBody: EfSnapshotShim.Block("Probe.Probed", """
                            b.Property<int>("Id").HasColumnType("int");

                            b.Property<int>("Code").HasColumnType("varchar(32)");

                            b.HasKey("Id");

                            b.ToTable("Probed", (string)null);
                """),
            rows: EfSnapshotShim.BoundRow("ProbedRow", "Probed"));

        var columnMessage = MessageOf(wrongColumn, "SPG031");
        Assert.Contains("has column type 'varchar(32)', which StoredProcedureValidator does not accept for .NET type 'int'", columnMessage);
        Assert.Contains("Allowed for 'varchar': string.", columnMessage);

        var json = Run(
            types: "    public class Probed\n    {\n        public int Id { get; set; }\n        public string Doc { get; set; }\n    }",
            buildModelBody: EfSnapshotShim.Block("Probe.Probed", """
                            b.Property<int>("Id").HasColumnType("int");

                            b.Property<string>("Doc").IsRequired();

                            b.HasKey("Id");

                            b.ToTable("Probed", (string)null);
                """),
            rows: EfSnapshotShim.BoundRow("ProbedRow", "Probed"));

        Assert.Contains(
            "The snapshot records no column type for it at all, which is how EF writes a property mapped into a JSON document",
            MessageOf(json, "SPG031"));
    }

    [Fact]
    public void ValueConvertedAndEnumPropertiesAreRefusedRatherThanEmittedUnderTheClassMembersName()
    {
        // HasConversion is never written into a snapshot - zero occurrences anywhere - so the converter is
        // invisible to the reader and the class's member type is the only evidence that one exists. Rather
        // than emit a 'string Status' under a member the class declares as 'OrderStatus', the binding is
        // refused and both types are named, so the developer decides which one their SELECT returns.
        var converted = Run(
            types: """
                    public enum OrderStatus { New, Shipped }

                    public class Order
                    {
                        public int Id { get; set; }
                        public OrderStatus Status { get; set; }
                    }
                """,
            buildModelBody: EfSnapshotShim.Block("Probe.Order", """
                            b.Property<int>("Id").HasColumnType("int");

                            b.Property<string>("Status").IsRequired().HasColumnType("varchar(16)");

                            b.HasKey("Id");

                            b.ToTable("Order", (string)null);
                """),
            rows: EfSnapshotShim.BoundRow("OrderRow", "Order"));

        var message = MessageOf(converted, "SPG032");
        Assert.Contains("stores property 'Status' as 'string'", message);
        Assert.Contains("the member 'Status' on the class is 'global::Probe.OrderStatus'", message);
        Assert.DoesNotContain("partial record OrderRow", converted.GeneratedText);

        // An unconverted enum: the model stores the int and the class says the enum. Same refusal, and it
        // is SPG032 rather than SPG030 only because the model and the class are compared FIRST - an enum
        // member has no IDataRecord getter, so the getter check would otherwise fire and report that
        // 'int' has no reader, which is not true of int.
        var unconverted = Run(
            types: """
                    public enum OrderKind { Retail, Wholesale }

                    public class Order
                    {
                        public int Id { get; set; }
                        public OrderKind Kind { get; set; }
                    }
                """,
            buildModelBody: EfSnapshotShim.Block("Probe.Order", """
                            b.Property<int>("Id").HasColumnType("int");

                            b.Property<int>("Kind").HasColumnType("int");

                            b.HasKey("Id");

                            b.ToTable("Order", (string)null);
                """),
            rows: EfSnapshotShim.BoundRow("OrderRow", "Order"));

        Assert.Contains("the member 'Kind' on the class is 'global::Probe.OrderKind'", MessageOf(unconverted, "SPG032"));

        // And the nullability direction: a member declared int? over a NOT NULL column. The validator
        // rejects that pairing in both directions at startup, so it is refused here in both directions.
        var nullability = Run(
            types: """
                    public class Order
                    {
                        public int Id { get; set; }
                        public int? Count { get; set; }
                    }
                """,
            buildModelBody: EfSnapshotShim.Block("Probe.Order", """
                            b.Property<int>("Id").HasColumnType("int");

                            b.Property<int>("Count").HasColumnType("int");

                            b.HasKey("Id");

                            b.ToTable("Order", (string)null);
                """),
            rows: EfSnapshotShim.BoundRow("OrderRow", "Order"));

        var nullabilityMessage = MessageOf(nullability, "SPG032");
        Assert.Contains("stores property 'Count' as 'int'", nullabilityMessage);
        Assert.Contains("the member 'Count' on the class is 'int?'", nullabilityMessage);
    }

    // ---------------------------------------------------------------- the row type's own shape

    [Fact]
    public void EntityBoundRowMustBeAPartialRecordWithNoParameterListOfItsOwn()
    {
        // Two ways of saying what a row's members are; only one can win, and merging them would produce a
        // contract neither spelling describes. Both are refused before the model is consulted at all,
        // which is also what keeps the resolver's identity return the only path a bindingless row takes.
        var notPartial = Run(
            ProductTypes,
            ProductBlock,
            "    [SqlRow(Entity = typeof(Probe.Product))]\n    public record ProductRow;");

        Assert.Contains(
            "binds its members to entity type 'Probe.Product', so the generator writes its primary constructor",
            MessageOf(notPartial, "SPG021"));

        var positional = Run(
            ProductTypes,
            ProductBlock,
            "    [SqlRow(Entity = typeof(Probe.Product))]\n    public partial record ProductRow(int Id);");

        Assert.Contains(
            "declares primary-constructor members and also binds to entity type 'Probe.Product'",
            MessageOf(positional, "SPG022"));

        // A plain class has no RecordDeclarationSyntax at all, so it lands on SPG021 rather than on
        // nothing: the generator writes a primary constructor, and only a record can carry one.
        var plainClass = Run(
            ProductTypes,
            ProductBlock,
            "    [SqlRow(Entity = typeof(Probe.Product))]\n    public partial class ProductRow { }");

        Assert.Single(plainClass.WithId("SPG021"));
    }

    // ---------------------------------------------------------------- lookup outcomes

    [Fact]
    public void AbsentSnapshotIsCompletelySilentUnlessARowAsks()
    {
        // Guarantee (b)'s structural half, and the state of most consumers: no migration added yet,
        // Migrations excluded from compilation, migrations in another project, or EF not referenced at
        // all. None of those is reportable on its own, at any severity - a row type that did not ask is
        // never told anything.
        const string Header = """
            #nullable disable
            using System.Collections.Generic;
            using System.Threading;
            using System.Threading.Tasks;
            using Raptor21.EF.Extensions.StoredProcedures.Generated;
            """;

        const string Declarations = """
                [SqlRow]
                public partial record ProductRow(int Id, string Name);

                [StoredProcedureGroup]
                public partial class Procs
                {
                    [StoredProcedure("dbo.Product_List")]
                    public partial Task<IReadOnlyList<ProductRow>> ListAsync(CancellationToken ct = default);

                    [StoredProcedure("dbo.Product_Touch")]
                    public partial Task<int> TouchAsync(CancellationToken ct = default);
                }
            """;

        // No EF vocabulary, no snapshot, nothing that mentions a model: the state of a consumer who has
        // not run 'dotnet ef migrations add', which is most of them.
        var quiet = GeneratorTestHarness.Run(Header + "\n\nnamespace Demo\n{\n" + Declarations + "\n}\n");
        AssertNoDiagnostics(quiet);
        AssertCompiles(quiet);

        // The same compilation plus one row that asks. Exactly one diagnostic, and it names the cause
        // rather than leaving the developer to guess why nothing was generated.
        const string Asking = """
                public class Widget { public int Id { get; set; } }

                [SqlRow(Entity = typeof(Widget))]
                public partial record WidgetRow;
            """;

        var asking = GeneratorTestHarness.Run(Header + "\n\nnamespace Demo\n{\n" + Asking + "\n\n" + Declarations + "\n}\n");

        var diagnostic = Assert.Single(asking.GeneratorDiagnostics);
        Assert.Equal("SPG027", diagnostic.Id);
        Assert.Contains("this compilation contains no EF Core model snapshot", diagnostic.GetMessage());
        Assert.Contains("if migrations live in a separate project", diagnostic.GetMessage());
        AssertCompiles(asking);
    }

    [Fact]
    public void EntityNamingATypeNoSnapshotDescribesReportsSPG028()
    {
        // Snapshots exist and none of them names this type. The message says how EF names an entity type
        // - metadata name, so a nested type is 'Outer+Nested' - because the two spellings a developer
        // would try are the two that cannot match.
        var result = Run(
            ProductTypes + "\n\n    public class Unmapped { public int Id { get; set; } }",
            ProductBlock,
            EfSnapshotShim.BoundRow("UnmappedRow", "Unmapped"));

        var message = MessageOf(result, "SPG028");
        Assert.Contains("binds its members to 'Probe.Unmapped', which is not an entity type in any model snapshot", message);
        Assert.DoesNotContain("partial record UnmappedRow", result.GeneratedText);
    }

    [Fact]
    public void TwoSnapshotsAgreeingAnswerSilentlyAndTwoDisagreeingReportSPG029()
    {
        // Two DbContexts in one assembly may map one CLR type twice, and 'Entity = typeof(X)' names
        // neither of them. When the two descriptions are identical the ambiguity is unobservable and is
        // answered in silence - the same gate the whole feature keeps: a fill that cannot move the emitted
        // contract says nothing. When they differ, picking one by declaration order would make the
        // emitted contract depend on something the developer cannot see.
        string Block(string table) => EfSnapshotShim.Block("Probe.Thing", """
                        b.Property<int>("Id").HasColumnType("int");

                        b.Property<string>("Name").IsRequired().HasColumnType("varchar(64)");

                        b.HasKey("Id");

            """ + $"                b.ToTable(\"{table}\", (string)null);");

        RunResult RunTwo(string secondTable) => GeneratorTestHarness.Run(EfSnapshotShim.Compose(
            EfSnapshotShim.Types("""
                    public class Thing
                    {
                        public int Id { get; set; }
                        public string Name { get; set; }
                    }
                """),
            EfSnapshotShim.Snapshot("SnapshotOne", Block("Thing")),
            EfSnapshotShim.Snapshot("SnapshotTwo", Block(secondTable), "ProbeContextTwo"),
            EfSnapshotShim.Rows(EfSnapshotShim.BoundRow("ThingRow", "Thing"))));

        var agreeing = RunTwo("Thing");
        AssertNoDiagnostics(agreeing);
        AssertCompiles(agreeing);
        Assert.Contains("partial record ThingRow(", agreeing.GeneratedText);

        var disagreeing = RunTwo("OtherThing");
        var message = MessageOf(disagreeing, "SPG029");
        Assert.Contains("Entity type 'Probe.Thing' appears in model snapshots 'Probe.SnapshotOne' and 'Probe.SnapshotTwo'", message);
        Assert.DoesNotContain("partial record ThingRow", disagreeing.GeneratedText);
    }

    // ---------------------------------------------------------------- the byte-identity guarantee

    [Fact]
    public void PositionalRecordEmitsIdenticallyWithAndWithoutASnapshotInTheCompilation()
    {
        // The direct assertion of guarantee (a) for the old path, and the negative control for
        // RowResolver.Resolve's identity return. The snapshot below even describes an entity type whose
        // name matches the row's, which is the closest thing to an accident this pipeline could have -
        // and nothing asks, so nothing happens.
        const string Rows = """
                [SqlRow]
                public partial record ProductRow(int Id, string Name, decimal Price);

                [StoredProcedureGroup]
                public partial class Procs
                {
                    [StoredProcedure("dbo.Product_List")]
                    public partial Task<IReadOnlyList<ProductRow>> ListAsync(CancellationToken ct = default);
                }
            """;

        var without = GeneratorTestHarness.Run(EfSnapshotShim.Compose(EfSnapshotShim.Rows(Rows)));

        var with = GeneratorTestHarness.Run(EfSnapshotShim.Compose(
            EfSnapshotShim.Types("""
                    public class ProductRow
                    {
                        public int Id { get; set; }
                        public string Name { get; set; }
                        public decimal Price { get; set; }
                    }
                """),
            EfSnapshotShim.Snapshot("ProbeSnapshot", EfSnapshotShim.Block("Probe.ProductRow", """
                            b.Property<int>("Id").HasColumnType("int");

                            b.Property<string>("Name").IsRequired().HasColumnType("varchar(64)");

                            b.Property<decimal>("Price").HasColumnType("decimal(18,2)");

                            b.HasKey("Id");

                            b.ToTable("ProductRow", (string)null);
                """)),
            EfSnapshotShim.Rows(Rows)));

        AssertNoDiagnostics(without);
        AssertNoDiagnostics(with);
        AssertCompiles(without);
        AssertCompiles(with);
        Assert.Equal(Norm(without.GeneratedText), Norm(with.GeneratedText));
    }

    // ---------------------------------------------------------------- the duplicated type table

    [Fact]
    public void GeneratorsCopyOfTheValidatorTypeTableAgreesWithTheRuntimes()
    {
        // A real duplication: the generator is netstandard2.0 - that is where Roslyn loads analyzers - and
        // cannot reference the net8.0/net10.0 runtime library that owns the original. This test is the
        // only thing standing between the two copies and a silent drift, and drift surfaces as a startup
        // InvalidOperationException, which is the failure the whole binding exists to eliminate.
        //
        // The runtime's table lists T and T? separately because it holds Type objects; the generator's
        // holds element types, because a member's nullability is carried beside it and checked against the
        // model rather than against this table. So the runtime's pairs are collapsed before comparing.
        //
        // The name list below is the third thing to update: both tables are switch statements, so neither
        // can be enumerated, and a case added to one without being added here is the one drift this test
        // cannot see.
        var runtimeTable = typeof(global::Raptor21.EF.Extensions.StoredProcedures.Validation.StoredProcedureValidator)
            .GetMethod("GetAllowedDotNetTypesForSqlType", BindingFlags.Static | BindingFlags.NonPublic);
        Assert.NotNull(runtimeTable);

        string[] known =
        {
            "tinyint", "smallint", "int", "bigint", "bit",
            "varchar", "nvarchar", "char", "nchar", "text", "ntext",
            "binary", "varbinary", "image",
            "datetime", "datetime2", "smalldatetime", "date",
            "time", "datetimeoffset", "uniqueidentifier",
            "decimal", "numeric", "real", "float", "money", "smallmoney",
        };

        // Types the validator has never accepted, asserted so that "both empty" is a checked agreement
        // rather than an untested corner.
        string[] unknown = { "xml", "sql_variant", "geography", "hierarchyid", "rowversion", "timestamp", "sysname", "" };

        foreach (var sqlType in known.Concat(unknown))
        {
            var runtimeTypes = (IEnumerable<Type>)runtimeTable!.Invoke(null, new object[] { sqlType })!;
            var expected = runtimeTypes
                .Select(ElementText)
                .Distinct()
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

            var actual = RowResolver.AllowedElementTypesForSqlType(sqlType)
                .OrderBy(s => s, StringComparer.Ordinal)
                .ToArray();

            Assert.Equal(expected, actual);
        }
    }

    /// <summary>Renders one runtime <see cref="Type"/> the way the generator writes an element type.</summary>
    /// <remarks>
    /// The generator's spelling is <c>TypeFmtNonNullable</c>: C# keywords for the special types and a
    /// <c>global::</c>-qualified name for everything else. Nullable value types collapse onto their
    /// element, which is the mapping that makes the two tables comparable at all.
    /// </remarks>
    private static string ElementText(Type type)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;

        if (t == typeof(byte[])) return "byte[]";
        if (t == typeof(byte)) return "byte";
        if (t == typeof(short)) return "short";
        if (t == typeof(int)) return "int";
        if (t == typeof(long)) return "long";
        if (t == typeof(bool)) return "bool";
        if (t == typeof(decimal)) return "decimal";
        if (t == typeof(float)) return "float";
        if (t == typeof(double)) return "double";
        if (t == typeof(string)) return "string";

        return "global::" + t.FullName;
    }
}
