using Microsoft.EntityFrameworkCore;
using Raptor21.EF.Extensions.Migrations;

namespace Raptor21.EF.Extensions.Sample.Data;

/// <summary>
/// The whole point of the sample: one DbContext where tables AND stored procedures are both code-first.
/// Tables come from the entity model as usual. Procedures come from the embedded .sql files registered
/// below, which become model annotations, get diffed by <see cref="StoredProcedureModelDiffer"/> and land
/// in the same migration as the table changes.
/// </summary>
public sealed class CatalogDbContext(DbContextOptions<CatalogDbContext> options) : DbContext(options)
{
    /// <summary>Resource prefix of the embedded procedure scripts. Matches the DbScripts folder.</summary>
    public const string ScriptResourcePrefix = "Raptor21.EF.Extensions.Sample.DbScripts";

    public DbSet<Product> Products => Set<Product>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Product>(e =>
        {
            e.ToTable("Product");
            e.HasKey(p => p.Id);
            e.Property(p => p.Sku).HasColumnType("varchar(32)").IsRequired();
            e.Property(p => p.Name).HasColumnType("varchar(128)").IsRequired();
            e.Property(p => p.Price).HasColumnType("decimal(18,2)");
            e.Property(p => p.UpdatedUtc).HasColumnType("datetime");
            e.HasIndex(p => p.Sku).IsUnique();
        });

        // One call, and every .sql under DbScripts/ is part of the model. Add a procedure file, run
        // `dotnet ef migrations add`, and the CREATE OR ALTER is in the migration next to the table DDL.
        modelBuilder.RegisterStoredProcedures(typeof(CatalogDbContext).Assembly, ScriptResourcePrefix);
    }
}
