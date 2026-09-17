using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Design;
using Microsoft.EntityFrameworkCore.SqlServer.Design.Internal;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Raptor21.EF.Extensions.Migrations.Tests;

/// <summary>
/// The round trip the feature rests on: every declaration is an annotation, the model snapshot
/// <c>dotnet ef migrations add</c> writes must carry it, and a model rebuilt from that snapshot must diff
/// clean against the model that produced it. The first half runs EF's real <c>CSharpSnapshotGenerator</c>;
/// the second builds a context whose <c>OnModelCreating</c> is the snapshot's own calls.
/// </summary>
public class SnapshotRoundTripTests
{
    [Fact]
    public void SnapshotGenerator_WritesEveryDeclaration()
    {
        using var context = new Declared();

        // The service set `dotnet ef` builds: EF's design-time services, the provider's, and the context's.
        var services = new ServiceCollection().AddEntityFrameworkDesignTimeServices();
        new SqlServerDesignTimeServices().ConfigureDesignTimeServices(services);
        services.AddDbContextDesignTimeServices(context);
        using var provider = services.BuildServiceProvider();

        var snapshot = provider.GetRequiredService<IMigrationsCodeGenerator>()
            .GenerateSnapshot("Fx.Migrations", typeof(Declared), "DeclaredModelSnapshot", context.GetService<IDesignTimeModel>().Model);

        // Model-level declarations, as HasAnnotation on the model builder.
        Assert.Contains(
            ".HasAnnotation(\"Raptor21:PartitionFunction:pf_EventsMonth\", \"v1|datetime2(3)|RIGHT|'20260801','20260901'\")",
            snapshot, StringComparison.Ordinal);
        Assert.Contains(
            ".HasAnnotation(\"Raptor21:PartitionScheme:ps_Events\", \"v1|pf_EventsMonth|PRIMARY\")",
            snapshot, StringComparison.Ordinal);

        // Entity-level declarations, as HasAnnotation on the entity builder — the PROPERTY name for the
        // column, which is what the snapshot can express.
        Assert.Contains(".HasAnnotation(\"Raptor21:Partition:Scheme\", \"ps_Events\")", snapshot, StringComparison.Ordinal);
        Assert.Contains(".HasAnnotation(\"Raptor21:Partition:Column\", \"OccurredAt\")", snapshot, StringComparison.Ordinal);
        Assert.Contains(".HasAnnotation(\"Raptor21:ClusteredColumnstoreIndex\", \"cci_Events\")", snapshot, StringComparison.Ordinal);

        // Full-text: the catalog on the model, the index on the entity — property names, key index and tracking.
        Assert.Contains(".HasAnnotation(\"Raptor21:FullTextCatalog:ft_Account\", \"v1\")", snapshot, StringComparison.Ordinal);
        Assert.Contains(".HasAnnotation(\"Raptor21:FullTextIndex\", \"v1|ft_Account||AUTO|Name:1055,Email\")", snapshot, StringComparison.Ordinal);
    }

    [Fact]
    public void AModelRebuiltFromTheSnapshotsCalls_DiffsClean()
    {
        using var declared = new Declared();
        using var fromSnapshot = new RebuiltFromSnapshot();

        var differ = Assert.IsType<CodeFirstDatabaseObjectsModelDiffer>(declared.GetService<IMigrationsModelDiffer>());
        var snapshotModel = RelationalModel(fromSnapshot);
        var currentModel = RelationalModel(declared);

        // Both directions, because EF asks the differ both ways and the snapshot is the SOURCE in the one that
        // matters: a difference here is a migration scaffolded from an unchanged model.
        Assert.Empty(differ.GetDifferences(snapshotModel, currentModel));
        Assert.False(differ.HasDifferences(snapshotModel, currentModel));
        Assert.Empty(differ.GetDifferences(currentModel, snapshotModel));
    }

    private static IRelationalModel RelationalModel(DbContext context)
        => context.GetService<IDesignTimeModel>().Model.GetRelationalModel();

    private abstract class Variant : DbContext
    {
        protected sealed override void OnConfiguring(DbContextOptionsBuilder optionsBuilder)
            => optionsBuilder
                .UseSqlServer("Server=host-that-must-never-be-contacted;Database=none")
                .UseCodeFirstDatabaseObjects();
    }

    private sealed class Declared : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.HasDefaultSchema("dbo");
            modelBuilder.HasPartitionFunction(
                "pf_EventsMonth", "datetime2(3)", PartitionRange.Right, PartitionBoundaries.Monthly(new DateOnly(2026, 8, 1), 2));
            modelBuilder.HasPartitionScheme("ps_Events", "pf_EventsMonth");

            var events = modelBuilder.Entity<Event>();
            events.ToTable("Events");
            events.Property(e => e.OccurredAt).HasColumnName("OccurredAtUtc").HasColumnType("datetime2(3)");
            events.HasKey(e => new { e.Id, e.OccurredAt }).IsClustered(false);
            events.HasIndex(e => e.Kind);
            events.OnPartitionScheme("ps_Events", e => e.OccurredAt);
            events.HasClusteredColumnstoreIndex("cci_Events");

            modelBuilder.HasFullTextCatalog("ft_Account");
            var customers = modelBuilder.Entity<Customer>();
            customers.ToTable("Customers");
            customers.Property(c => c.Name).HasColumnType("nvarchar(200)");
            customers.Property(c => c.Email).HasColumnType("nvarchar(254)");
            customers.HasFullTextIndex("ft_Account", [new FullTextColumn("Name", 1055), new FullTextColumn("Email")]);
        }
    }

    // What the generated snapshot says, written the way the snapshot writes it: no extension method from this
    // package in sight, only HasAnnotation with the serialized values.
    private sealed class RebuiltFromSnapshot : Variant
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder
                .HasDefaultSchema("dbo")
                .HasAnnotation("Raptor21:PartitionFunction:pf_EventsMonth", "v1|datetime2(3)|RIGHT|'20260801','20260901'")
                .HasAnnotation("Raptor21:PartitionScheme:ps_Events", "v1|pf_EventsMonth|PRIMARY")
                .HasAnnotation("Raptor21:FullTextCatalog:ft_Account", "v1");

            modelBuilder.Entity<Event>(b =>
            {
                b.Property(e => e.OccurredAt).HasColumnName("OccurredAtUtc").HasColumnType("datetime2(3)");
                b.HasKey(e => new { e.Id, e.OccurredAt }).IsClustered(false);
                b.HasIndex(e => e.Kind);
                b.ToTable("Events", "dbo");
                b.HasAnnotation("Raptor21:Partition:Scheme", "ps_Events");
                b.HasAnnotation("Raptor21:Partition:Column", "OccurredAt");
                b.HasAnnotation("Raptor21:ClusteredColumnstoreIndex", "cci_Events");
            });

            modelBuilder.Entity<Customer>(b =>
            {
                b.Property(c => c.Name).HasColumnType("nvarchar(200)");
                b.Property(c => c.Email).HasColumnType("nvarchar(254)");
                b.ToTable("Customers", "dbo");
                b.HasAnnotation("Raptor21:FullTextIndex", "v1|ft_Account||AUTO|Name:1055,Email");
            });
        }
    }

    private sealed class Customer
    {
        public long Id { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Email { get; set; }
    }

    private sealed class Event
    {
        public long Id { get; set; }

        public DateTime OccurredAt { get; set; }

        public string? Kind { get; set; }
    }
}
