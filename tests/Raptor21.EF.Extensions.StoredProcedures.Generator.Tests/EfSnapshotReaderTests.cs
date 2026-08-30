using Xunit;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator.Tests;

/// <summary>
/// The EF <c>ModelSnapshot</c> reader and the ambiguity rule, exercised through the generator.
/// </summary>
/// <remarks>
/// <para>
/// Everything here runs the real generator through the real driver, because that is the only way to
/// reach the reader at all: <c>EfSnapshotReader.Read</c> takes a <c>GeneratorAttributeSyntaxContext</c>,
/// which a test cannot construct, and <c>EfModelIndex</c> is fed from a provider. Every claim about the
/// reader is therefore stated as a claim about what a bound row emits or which diagnostic it reports -
/// which is the only form in which any of it reaches a consumer anyway.
/// </para>
/// <para>
/// The refusal clauses are what most of this file is about, and they all fail in the same direction on
/// purpose: an entity type binds only if its columns are exclusively its own. Three ways a naive reader
/// fails OPEN - permitting inference on exactly the shapes the rule exists to forbid - each get a test
/// here, and each is closed by an ordering inside <c>EfModelIndex</c> rather than by a check that could
/// be forgotten: mapping targets are resolved through <c>HasBaseType</c> before the sharing count is
/// taken, owned blocks are recursed into and default to the owner's target, and every block naming one
/// entity is merged before anything is decided.
/// </para>
/// </remarks>
public class EfSnapshotReaderTests
{
    private static string Norm(string text) => text.Replace("\r\n", "\n");

    private static void AssertNoDiagnostics(RunResult result) =>
        Assert.True(result.GeneratorDiagnostics.IsDefaultOrEmpty, result.DiagnosticSummary());

    private static void AssertCompiles(RunResult result) =>
        Assert.True(result.OutputErrors.IsDefaultOrEmpty, result.OutputErrorSummary());

    /// <summary>One compilation: the EF vocabulary, the probe types, one snapshot and the row types.</summary>
    private static RunResult Run(string types, string buildModelBody, string rows) =>
        GeneratorTestHarness.Run(Source(types, buildModelBody, rows));

    /// <summary>The composed source, for a test that needs to say where in it a diagnostic landed.</summary>
    private static string Source(string types, string buildModelBody, string rows) =>
        EfSnapshotShim.Compose(
            EfSnapshotShim.Types(types),
            EfSnapshotShim.Snapshot("ProbeSnapshot", buildModelBody),
            EfSnapshotShim.Rows(rows));

    /// <summary>Asserts that a diagnostic landed on exactly the row type's own declaration.</summary>
    /// <remarks>
    /// Every snapshot-derived refusal is attached to the row type, never to a span inside the snapshot -
    /// that file is rewritten wholesale by <c>dotnet ef migrations add</c>, so a location in it would
    /// move on every model change, and the model the reader carries holds no Location at all. What the
    /// developer needs pointed at is the declaration they wrote, which is the one they can change.
    /// </remarks>
    private static void AssertLocatedOn(string source, Microsoft.CodeAnalysis.Diagnostic diagnostic, string rowTypeName)
    {
        var span = diagnostic.Location.SourceSpan;
        Assert.Equal(rowTypeName, source.Substring(span.Start, span.Length));
    }

    private static string MessageOf(RunResult result, string id)
    {
        var diagnostic = Assert.Single(result.WithId(id));
        return diagnostic.GetMessage();
    }

    /// <summary>Asserts that a refused row produced no partial at all.</summary>
    /// <remarks>
    /// Named per row rather than "no partial record anywhere": the attributes file the generator always
    /// emits documents <c>public partial record MyRow;</c> in <c>SqlRowAttribute.Entity</c>'s own XML
    /// comment, so a bare substring search for the declaration matches text that is always there.
    /// </remarks>
    private static void AssertNoRowEmitted(RunResult result, params string[] rowTypeNames)
    {
        foreach (var name in rowTypeNames)
            Assert.DoesNotContain("partial record " + name, result.GeneratedText);
    }

    // ------------------------------------------------------------ which class is the snapshot

    [Fact]
    public void DesignerPartialCarryingDbContextAttributeIsNotReadAsASnapshot()
    {
        // Every per-migration .Designer.cs carries the same [DbContext(typeof(T))] the snapshot carries.
        // Its BuildTargetModel body is a byte-identical copy of the model TODAY, which is exactly why
        // reading the wrong one fails a year later rather than now - the copy goes stale the day a second
        // migration is added. So the two bodies here describe DIFFERENT models, and reading the Designer
        // is observable: the entity would be described twice, differently, and SPG029 would refuse it.
        var result = GeneratorTestHarness.Run(EfSnapshotShim.Compose(
            EfSnapshotShim.Types("""
                    public class Product
                    {
                        public int Id { get; set; }
                        public string Name { get; set; }
                        public decimal Price { get; set; }
                    }
                """),
            EfSnapshotShim.Snapshot("ProbeSnapshot", EfSnapshotShim.Block("Probe.Product", """
                            b.Property<int>("Id").HasColumnType("int");
                            b.Property<string>("Name").IsRequired().HasColumnType("varchar(64)");
                            b.HasKey("Id");
                            b.ToTable("Product", (string)null);
                """)),
            EfSnapshotShim.DesignerMigration("Initial", "20260101000000_Initial", EfSnapshotShim.Block("Probe.Product", """
                            b.Property<int>("Id").HasColumnType("int");
                            b.Property<string>("Name").IsRequired().HasColumnType("varchar(64)");
                            b.Property<decimal>("Price").HasColumnType("decimal(18,2)");
                            b.HasKey("Id");
                            b.ToTable("LegacyProduct", (string)null);
                """)),
            EfSnapshotShim.Rows(EfSnapshotShim.BoundRow("ProductRow", "Product"))));

        AssertNoDiagnostics(result);
        AssertCompiles(result);

        // The model came from BuildModel, and only from it: Price is in the stale copy alone.
        Assert.Contains("int Id,", Norm(result.GeneratedText));
        Assert.Contains("string Name)", Norm(result.GeneratedText));
        Assert.DoesNotContain("Price", result.GeneratedText);
    }

    // ------------------------------------------------------------ the whole-model passes

    [Fact]
    public void AllBlocksNamingTheSameEntityAreMerged()
    {
        // A snapshot emits an entity's properties in one block and its owned types, relationships and
        // navigations in later ones, so neither the first block nor the last is the entity. Reading one
        // block per name loses whichever half is in the other.
        var result = Run(
            types: """
                    public class Cust
                    {
                        public int Id { get; set; }
                        public string Name { get; set; }
                    }
                """,
            buildModelBody:
                EfSnapshotShim.Block("Probe.Cust", """
                            b.Property<int>("Id").HasColumnType("int");
                            b.HasKey("Id");
                            b.ToTable("Cust", (string)null);
                """)
                + EfSnapshotShim.Block("Probe.Cust", """
                            b.Property<string>("Name").IsRequired().HasColumnType("varchar(64)");
                """),
            rows: EfSnapshotShim.BoundRow("CustRow", "Cust"));

        AssertNoDiagnostics(result);
        AssertCompiles(result);
        Assert.Contains(Norm("""
                partial record CustRow(
                    int Id,
                    string Name) : global::Raptor21.EF.Extensions.StoredProcedures.Execution.IFromDataRecord<CustRow>
            """).TrimEnd(), Norm(result.GeneratedText));
    }

    [Fact]
    public void UseIdentityColumnDoesNotRegisterASecondIdProperty()
    {
        // The root-expression rule, not a name whitelist: a statement counts only when it roots at the
        // enclosing lambda's OWN parameter. SqlServerPropertyBuilderExtensions.UseIdentityColumn(
        // b.Property<int>("Id")) roots at the extension class and the b.Property occurrence is nested in
        // an argument list, so it is a reference to a property rather than a declaration of one.
        //
        // 'Secret' is what makes that observable. A walker that followed nested occurrences would
        // register it from the IsSparse call, and since that call states no HasColumnType the row would
        // be refused by SPG031's JSON arm rather than quietly gaining a member.
        var result = Run(
            types: """
                    public class Widget
                    {
                        public int Id { get; set; }
                        public string Name { get; set; }
                        public string Secret { get; set; }
                    }
                """,
            buildModelBody: EfSnapshotShim.Block("Probe.Widget", """
                            b.Property<int>("Id")
                                .ValueGeneratedOnAdd()
                                .HasColumnType("int");

                            SqlServerPropertyBuilderExtensions.UseIdentityColumn(b.Property<int>("Id"));

                            b.Property<string>("Name").IsRequired().HasColumnType("varchar(64)");

                            SqlServerPropertyBuilderExtensions.IsSparse(b.Property<string>("Secret"));

                            b.HasKey("Id");
                            b.ToTable("Widget", (string)null);
                """),
            rows: EfSnapshotShim.BoundRow("WidgetRow", "Widget"));

        AssertNoDiagnostics(result);
        AssertCompiles(result);
        Assert.DoesNotContain("Secret", result.GeneratedText);
        Assert.Contains(Norm("""
                partial record WidgetRow(
                    int Id,
                    string Name) : global::Raptor21.EF.Extensions.StoredProcedures.Execution.IFromDataRecord<WidgetRow>
                {
                    public static WidgetRow FromDataRecord(global::System.Data.IDataRecord record)
                    {
                        return new WidgetRow(
                            record.GetInt32(record.GetOrdinal("Id")),
                            record.IsDBNull(record.GetOrdinal("Name")) ? null : record.GetString(record.GetOrdinal("Name")));
                    }
                }
            """).TrimEnd(), Norm(result.GeneratedText));
    }

    // ------------------------------------------------------------ reading a mapping call

    /// <summary>
    /// Every <c>ToTable</c> shape EF emits says the same thing about the table, and the reader has to
    /// agree - matched on the invocation NAME and the leading string-literal arguments, never on
    /// argument count, and with the schema folded as <c>explicit ?? HasDefaultSchema ?? "dbo"</c>.
    /// </summary>
    /// <remarks>
    /// Observed through SPG023, which is the only place a resolved target is spelled out loud: two
    /// entities that resolve to one target refuse each other and the message names it. The spec lists
    /// this and the two facts below as one entry; they are three tests because they assert three
    /// different outcomes.
    /// </remarks>
    [Theory]
    [InlineData("b.ToTable(\"A\");", "b.ToTable(\"A\");", "dbo.A")]
    [InlineData("b.ToTable(\"A\", (string)null);", "b.ToTable(\"A\");", "dbo.A")]
    [InlineData("b.ToTable(\"A\", \"s\");", "b.ToTable(\"A\", \"s\");", "s.A")]
    [InlineData("b.ToTable(\"A\", t => { });", "b.ToTable(\"A\");", "dbo.A")]
    [InlineData("b.ToTable(\"A\", \"s\", t => { });", "b.ToTable(\"A\", \"s\");", "s.A")]
    [InlineData("b.ToTable(\"A\");", "b.ToTable(\"a\");", "dbo.A")]
    public void ToTableIsReadAtEveryArityAndWithACastNullSchema(string firstMapping, string secondMapping, string expectedTarget)
    {
        var result = Run(
            types: """
                    public class A1 { public int Id { get; set; } }

                    public class A2 { public int Id { get; set; } }
                """,
            buildModelBody:
                EfSnapshotShim.Block("Probe.A1", """
                            b.Property<int>("Id").HasColumnType("int");
                            b.HasKey("Id");
                """ + "\n            " + firstMapping)
                + EfSnapshotShim.Block("Probe.A2", """
                            b.Property<int>("Id").HasColumnType("int");
                            b.HasKey("Id");
                """ + "\n            " + secondMapping),
            rows: EfSnapshotShim.BoundRow("A1Row", "A1"));

        var message = MessageOf(result, "SPG023");
        Assert.Contains("Entity type 'Probe.A1' is mapped to '" + expectedTarget + "'", message);
        Assert.Contains("which entity type 'Probe.A2' is also mapped to", message);
        Assert.DoesNotContain("partial record A1Row", result.GeneratedText);
    }

    [Fact]
    public void ToTableWithACastNullNameMeansNoTableRatherThanATableNamedNull()
    {
        // The cast is not syntax noise to be stripped blindly: (string)null in the FIRST position changes
        // the meaning entirely. A reader that read it as a name would put both of these entities on one
        // table called "null" and refuse both - which is how this test fails if the distinction is lost.
        var result = Run(
            types: """
                    public class Shape1 { public int Id { get; set; } }

                    public class Shape2 { public int Id { get; set; } }
                """,
            buildModelBody:
                EfSnapshotShim.Block("Probe.Shape1", """
                            b.Property<int>("Id").HasColumnType("int");
                            b.ToTable((string)null);
                """)
                + EfSnapshotShim.Block("Probe.Shape2", """
                            b.Property<int>("Id").HasColumnType("int");
                            b.ToTable((string)null);
                """),
            rows: EfSnapshotShim.BoundRow("Shape1Row", "Shape1") + "\n\n" + EfSnapshotShim.BoundRow("Shape2Row", "Shape2"));

        AssertNoDiagnostics(result);
        AssertCompiles(result);
        Assert.Contains("partial record Shape1Row(", result.GeneratedText);
        Assert.Contains("partial record Shape2Row(", result.GeneratedText);
    }

    [Fact]
    public void AnUnreadableToTableArgumentRefusesEveryEntityInTheFile()
    {
        // An unparsed mapping must reduce confidence, never leave a DIFFERENT entity looking like a clean
        // sole occupant - and a per-entity quarantine cannot do that, because the unreadable entity might
        // be the one sharing the table. So the whole file is refused, including this entity, whose own
        // ToTable is perfectly readable.
        var result = Run(
            types: """
                    public class Clean { public int Id { get; set; } }

                    public class HandEdited { public int Id { get; set; } }
                """,
            buildModelBody:
                EfSnapshotShim.Block("Probe.Clean", """
                            b.Property<int>("Id").HasColumnType("int");
                            b.HasKey("Id");
                            b.ToTable("Clean", (string)null);
                """)
                + EfSnapshotShim.Block("Probe.HandEdited", """
                            b.Property<int>("Id").HasColumnType("int");
                            b.HasKey("Id");
                            b.ToTable(TableNames.Legacy);
                """),
            rows: EfSnapshotShim.BoundRow("CleanRow", "Clean"));

        Assert.Contains(
            "Entity type 'Probe.Clean' is mapped through a mapping argument this reader cannot read",
            MessageOf(result, "SPG026"));
        Assert.DoesNotContain("partial record CleanRow", result.GeneratedText);
    }

    // ------------------------------------------------------------ the join key

    [Fact]
    public void NestedEntityTypeIsJoinedByItsMetadataNameNotItsDisplayString()
    {
        const string Types = """
                public class Outer
                {
                    public class Nested
                    {
                        public int Id { get; set; }
                    }
                }
            """;

        const string Body = """
                        b.Property<int>("Id").HasColumnType("int");
                        b.HasKey("Id");
                        b.ToTable("Nested", (string)null);
            """;

        var metadataName = Run(Types, EfSnapshotShim.Block("Probe.Outer+Nested", Body), EfSnapshotShim.BoundRow("NestedRow", "Outer.Nested"));

        AssertNoDiagnostics(metadataName);
        AssertCompiles(metadataName);
        Assert.Contains("partial record NestedRow(", metadataName.GeneratedText);

        // The negative control, and the reason the join key is built by hand: ToDisplayString and
        // FullyQualifiedFormat both write 'Outer.Nested', which would silently never match anything EF
        // wrote - surfacing as SPG028 pointing at the developer's spelling rather than at ours.
        var displayString = Run(Types, EfSnapshotShim.Block("Probe.Outer.Nested", Body), EfSnapshotShim.BoundRow("NestedRow", "Outer.Nested"));

        Assert.Contains("binds its members to 'Probe.Outer+Nested'", MessageOf(displayString, "SPG028"));
        Assert.DoesNotContain("partial record NestedRow", displayString.GeneratedText);
    }

    // ------------------------------------------------------------ the three fail-open holes

    [Fact]
    public void TphDerivedTypeWithNoToTableStillCountsAgainstTheBasesTable()
    {
        // TPH is EF's DEFAULT inheritance strategy and its derived types emit no ToTable at all, so a
        // reader that counted literal mapping arguments would read a whole hierarchy's table as having
        // one occupant. It is closed by resolving targets through HasBaseType BEFORE the count is taken.
        //
        // 'Probe.Loose' is what makes that observable. It shares the table with three entity types, and
        // the one SPG023 names is the first occupant in declaration order - which is 'Probe.Book' only
        // because a derived type with no mapping call of its own was resolved onto the base's table. EF
        // emits entity blocks in name order, so the derived type really does come first here.
        var source = Source(
            types: """
                    public class Book { public int Id { get; set; } }

                    public class Item { public int Id { get; set; } }

                    public class Loose { public int Id { get; set; } }
                """,
            buildModelBody:
                EfSnapshotShim.Block("Probe.Book", """
                            b.HasBaseType("Probe.Item");

                            b.Property<string>("Isbn").HasColumnType("varchar(32)");

                            b.HasDiscriminator().HasValue("Book");
                """)
                + EfSnapshotShim.Block("Probe.Item", """
                            b.Property<int>("Id").HasColumnType("int");
                            b.Property<string>("Discriminator").IsRequired().HasColumnType("varchar(13)");
                            b.HasKey("Id");
                            b.ToTable("Item", (string)null);
                            b.HasDiscriminator<string>("Discriminator").HasValue("Item");
                """)
                + EfSnapshotShim.Block("Probe.Loose", """
                            b.Property<int>("Id").HasColumnType("int");
                            b.HasKey("Id");
                            b.ToTable("Item", (string)null);
                """),
            rows:
                EfSnapshotShim.BoundRow("BookRow", "Book") + "\n\n"
                + EfSnapshotShim.BoundRow("ItemRow", "Item") + "\n\n"
                + EfSnapshotShim.BoundRow("LooseRow", "Loose"));

        var result = GeneratorTestHarness.Run(source);

        var shared = MessageOf(result, "SPG023");
        Assert.Contains("Entity type 'Probe.Loose' is mapped to 'dbo.Item'", shared);
        Assert.Contains("which entity type 'Probe.Book' is also mapped to", shared);

        // Both ends of the hierarchy edge, refused for the hierarchy rather than for the sharing: the
        // clause order is what decides which of the two messages a developer reads.
        Assert.Equal(2, result.CountOf("SPG024"));
        Assert.Contains(result.WithId("SPG024"), d => d.GetMessage().Contains("Entity type 'Probe.Book' is a derived type of 'Probe.Item'"));
        Assert.Contains(result.WithId("SPG024"), d => d.GetMessage().Contains("Entity type 'Probe.Item' is the base type of 'Probe.Book'"));
        AssertNoRowEmitted(result, "BookRow", "ItemRow", "LooseRow");

        // Three refusals, each located on the declaration the developer wrote and can change, and none of
        // them anywhere in the snapshot.
        AssertLocatedOn(source, Assert.Single(result.WithId("SPG023")), "LooseRow");
        AssertLocatedOn(source, Assert.Single(result.WithId("SPG024"), d => d.GetMessage().Contains("'Probe.Book' is a derived type")), "BookRow");
        AssertLocatedOn(source, Assert.Single(result.WithId("SPG024"), d => d.GetMessage().Contains("'Probe.Item' is the base type")), "ItemRow");
    }

    [Fact]
    public void TptDerivedTypeIsRefusedEvenThoughItsTableCountIsOne()
    {
        // The hole the both-ends hierarchy rule closes, and the reason the rule is stated over the entity
        // instead of over the table: Animal -> Animals and Dog -> Dogs each count exactly one occupant,
        // so a per-table count permits both. A row built from Dog's block would silently be missing every
        // inherited member, because a derived block never repeats what it inherits.
        var result = Run(
            types: """
                    public class Animal
                    {
                        public int Id { get; set; }
                        public string Name { get; set; }
                    }

                    public class Dog
                    {
                        public int Id { get; set; }
                        public string Name { get; set; }
                        public string Breed { get; set; }
                    }
                """,
            buildModelBody:
                EfSnapshotShim.Block("Probe.Animal", """
                            b.Property<int>("Id").HasColumnType("int");
                            b.Property<string>("Name").IsRequired().HasColumnType("varchar(64)");
                            b.HasKey("Id");
                            b.ToTable("Animals", (string)null);
                            b.UseTptMappingStrategy();
                """)
                + EfSnapshotShim.Block("Probe.Dog", """
                            b.HasBaseType("Probe.Animal");

                            b.Property<string>("Breed").HasColumnType("varchar(32)");

                            b.ToTable("Dogs", (string)null);
                """),
            rows: EfSnapshotShim.BoundRow("AnimalRow", "Animal") + "\n\n" + EfSnapshotShim.BoundRow("DogRow", "Dog"));

        Assert.Equal(0, result.CountOf("SPG023"));
        Assert.Equal(2, result.CountOf("SPG024"));
        Assert.Contains(result.WithId("SPG024"), d => d.GetMessage().Contains("Entity type 'Probe.Dog' is a derived type of 'Probe.Animal'"));
        Assert.Contains(result.WithId("SPG024"), d => d.GetMessage().Contains("Entity type 'Probe.Animal' is the base type of 'Probe.Dog'"));
        AssertNoRowEmitted(result, "AnimalRow", "DogRow");
    }

    [Fact]
    public void OwnedTypeWithNoToTableOfItsOwnStillSharesTheOwnersTable()
    {
        // Two holes in one construct. The OwnsOne block is nested inside a lambda, so a walker that only
        // visited top-level modelBuilder.Entity(...) calls would never see the owned type and would count
        // the owner's table as having one occupant; and it lives in a SECOND block for the same entity
        // name, so a walker that read one block per name would miss it too. The owned type states no
        // ToTable, which is the default, so it can only reach the owner's table by inheriting it.
        var result = Run(
            types: """
                    public class Customer
                    {
                        public int Id { get; set; }
                        public string Name { get; set; }
                    }

                    public class Address
                    {
                        public int CustomerId { get; set; }
                        public string City { get; set; }
                    }
                """,
            buildModelBody:
                EfSnapshotShim.Block("Probe.Customer", """
                            b.Property<int>("Id").HasColumnType("int");
                            b.Property<string>("Name").IsRequired().HasColumnType("varchar(64)");
                            b.HasKey("Id");
                            b.ToTable("Customer", (string)null);
                """)
                + EfSnapshotShim.Block("Probe.Customer", """
                            b.OwnsOne("Probe.Address", "Billing", b1 =>
                                {
                                    b1.Property<int>("CustomerId").HasColumnType("int");

                                    b1.Property<string>("City").HasColumnType("varchar(64)");

                                    b1.HasKey("CustomerId");

                                    b1.WithOwner().HasForeignKey("CustomerId");
                                });
                """),
            rows: EfSnapshotShim.BoundRow("CustomerRow", "Customer") + "\n\n" + EfSnapshotShim.BoundRow("AddressRow", "Address"));

        var shared = MessageOf(result, "SPG023");
        Assert.Contains("Entity type 'Probe.Customer' is mapped to 'dbo.Customer'", shared);
        Assert.Contains("which entity type 'Probe.Address' is also mapped to", shared);

        // The owned type is refused separately and for a different reason: its real column names carry a
        // navigation prefix the snapshot never writes down.
        var owned = MessageOf(result, "SPG025");
        Assert.Contains("Entity type 'Probe.Address' is owned by 'Probe.Customer'", owned);
        Assert.Contains("the snapshot says 'City' where the table says 'Address_City'", owned);
        AssertNoRowEmitted(result, "CustomerRow", "AddressRow");
    }

    // ------------------------------------------------------------ version tolerance

    [Fact]
    public void UnrecognisedFluentCallsAreIgnoredRatherThanFailing()
    {
        // Version-agnosticism as a test rather than a claim. The snapshot is written by whatever
        // `dotnet ef` tooling last ran, which need not match the referenced EF package, so ProductVersion
        // is never asserted on and an unrecognised call is skipped rather than refused. FutureEfCall is
        // vocabulary no shipped EF emits, which is exactly what makes it worth having in the shim.
        //
        // The HasAnnotation chain matters more than it looks: this library writes every one of its
        // procedure scripts into the snapshot as an "Sp:" annotation, so a reader that did not skip
        // HasAnnotation would rebuild the model on every procedure edit.
        var result = Run(
            types: """
                    public class Gadget
                    {
                        public int Id { get; set; }
                        public string Sku { get; set; }
                    }
                """,
            buildModelBody: """
                        modelBuilder
                            .HasAnnotation("ProductVersion", "10.0.9")
                            .HasAnnotation("Relational:MaxIdentifierLength", 128)
                            .HasAnnotation("Sp:dbo.Product_Upsert", "CREATE OR ALTER PROCEDURE dbo.Product_Upsert AS BEGIN SET NOCOUNT ON; END");

                        SqlServerModelBuilderExtensions.UseIdentityColumns(modelBuilder);

                """ + EfSnapshotShim.Block("Probe.Gadget", """
                            b.Property<int>("Id")
                                .ValueGeneratedOnAdd()
                                .HasColumnType("int")
                                .HasAnnotation("SqlServer:ValueGenerationStrategy", 1);

                            b.Property<string>("Sku")
                                .IsRequired()
                                .HasMaxLength(32)
                                .IsUnicode(false)
                                .IsConcurrencyToken()
                                .ValueGeneratedOnAddOrUpdate()
                                .HasColumnType("varchar(32)");

                            b.HasKey("Id");

                            b.HasIndex("Sku").IsUnique();

                            b.ToTable("Gadget", (string)null);

                            b.UseTphMappingStrategy();

                            b.FutureEfCall("something this reader has never heard of");
                """),
            rows: EfSnapshotShim.BoundRow("GadgetRow", "Gadget"));

        AssertNoDiagnostics(result);
        AssertCompiles(result);
        Assert.Contains(Norm("""
                partial record GadgetRow(
                    int Id,
                    string Sku) : global::Raptor21.EF.Extensions.StoredProcedures.Execution.IFromDataRecord<GadgetRow>
            """).TrimEnd(), Norm(result.GeneratedText));
    }
}
