using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;
using Microsoft.EntityFrameworkCore.Migrations;
using Raptor21.EF.Extensions.Migrations;

namespace Raptor21.EF.Extensions.Sample.Data;

/// <summary>
/// Builds the context for <c>dotnet ef</c> and for the app itself, so both go through the same options -
/// including the differ replacement, without which `migrations add` would silently emit table changes
/// only and leave every procedure out of the migration.
/// </summary>
public sealed class CatalogDbContextFactory : IDesignTimeDbContextFactory<CatalogDbContext>
{
    /// <summary>Reads SAMPLE_SQL_CONNECTION, falling back to a LocalDB instance.</summary>
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("SAMPLE_SQL_CONNECTION")
        ?? "Server=(localdb)\\MSSQLLocalDB;Database=Raptor21EfExtensionsSample;Trusted_Connection=True;TrustServerCertificate=True";

    /// <summary>The one place the differ is wired in. Design time and run time must agree.</summary>
    public static DbContextOptions<CatalogDbContext> BuildOptions(string connectionString) =>
        new DbContextOptionsBuilder<CatalogDbContext>()
            .UseSqlServer(connectionString)
            .ReplaceService<IMigrationsModelDiffer, StoredProcedureModelDiffer>()
            .Options;

    public CatalogDbContext CreateDbContext(string[] args) => new(BuildOptions(ConnectionString));
}
