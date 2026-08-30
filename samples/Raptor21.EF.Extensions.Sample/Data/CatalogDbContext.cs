using Microsoft.EntityFrameworkCore;
using Raptor21.EF.Extensions.Migrations;

namespace Raptor21.EF.Extensions.Sample.Data;

/// <summary>
/// The whole point of the sample: one DbContext where tables AND stored procedures are both code-first.
/// Tables come from the entity model as usual. Procedures come from the embedded .sql files registered
/// below, which become model annotations, get diffed by <see cref="StoredProcedureModelDiffer"/> and land
/// in the same migration as the table changes.
/// </summary>
/// <remarks>
/// This model is also what the source generator reads - not from here, but from the snapshot
/// <c>dotnet ef migrations add</c> writes out of it, which is the reconciled output of every
/// configuration route: data annotations, the fluent calls below, <c>IEntityTypeConfiguration</c>
/// classes and conventions all collapse into one file of ordinary C#. That is why
/// <c>[SqlRow(Entity = ...)]</c> needs no separate tool and no database connection.
/// </remarks>
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

        // A keyless result shape rather than a table - see ProductListResult for what ToView does and
        // does not promise. Nothing else maps to vw_ProductList, it has no base type, no derived types
        // and no owner, so its columns are exclusively its own and ProductListRow can bind straight to
        // it. There is deliberately no DbSet: nothing queries this type through EF.
        modelBuilder.Entity<ProductListResult>(e =>
        {
            e.HasNoKey();
            e.ToView("vw_ProductList");
            e.Property(p => p.Id).HasColumnType("int");
            e.Property(p => p.Name).HasColumnType("varchar(128)").IsRequired();
            e.Property(p => p.Price).HasColumnType("decimal(18,2)");
            e.Property(p => p.Sku).HasColumnType("varchar(32)").IsRequired();
        });

        // One call, and every .sql under DbScripts/ is part of the model. Add a procedure file, run
        // `dotnet ef migrations add`, and the CREATE OR ALTER is in the migration next to the table DDL.
        modelBuilder.RegisterStoredProcedures(typeof(CatalogDbContext).Assembly, ScriptResourcePrefix);
    }
}
