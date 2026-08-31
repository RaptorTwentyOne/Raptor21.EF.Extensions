using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Migrations;

namespace Raptor21.EF.Extensions.Migrations;

/// <summary>Installs this package's migration services on a <see cref="DbContextOptionsBuilder"/>.</summary>
public static class CodeFirstDbContextOptionsBuilderExtensions
{
    /// <summary>
    /// Replaces EF's migration model differ with <see cref="CodeFirstDatabaseObjectsModelDiffer"/> and the SQL
    /// Server migrations SQL generator with <see cref="CodeFirstDatabaseObjectsMigrationsSqlGenerator"/>, so
    /// that stored procedures, partition functions and schemes, table placement and clustered columnstore
    /// indexes declared on the model are diffed into migrations and written as SQL Server accepts them.
    /// </summary>
    /// <remarks>
    /// Call it after <c>UseSqlServer(...)</c>. A context that declares procedures and nothing else may keep the
    /// single <c>ReplaceService&lt;IMigrationsModelDiffer, StoredProcedureModelDiffer&gt;()</c> it has today; a
    /// context that places a table on a partition scheme needs both replacements, because only the generator
    /// writes the <c>ON</c> clause, and the differ refuses to scaffold such a model with any other generator.
    /// Both replacements are required for design time and run time alike — the same options must reach
    /// <c>dotnet ef</c> and the application, or the two disagree about what a migration contains.
    /// </remarks>
    public static DbContextOptionsBuilder UseCodeFirstDatabaseObjects(this DbContextOptionsBuilder optionsBuilder)
    {
        ArgumentNullException.ThrowIfNull(optionsBuilder);

        return optionsBuilder
            .ReplaceService<IMigrationsModelDiffer, CodeFirstDatabaseObjectsModelDiffer>()
            .ReplaceService<IMigrationsSqlGenerator, CodeFirstDatabaseObjectsMigrationsSqlGenerator>();
    }

    /// <inheritdoc cref="UseCodeFirstDatabaseObjects(DbContextOptionsBuilder)"/>
    public static DbContextOptionsBuilder<TContext> UseCodeFirstDatabaseObjects<TContext>(
        this DbContextOptionsBuilder<TContext> optionsBuilder)
        where TContext : DbContext
        => (DbContextOptionsBuilder<TContext>)UseCodeFirstDatabaseObjects((DbContextOptionsBuilder)optionsBuilder);
}
