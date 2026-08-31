using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace Raptor21.EF.Extensions.Migrations;

/// <summary>
/// Extends EF Core's migration model differ so that stored procedures registered via
/// <see cref="StoredProcedureModelExtensions.RegisterStoredProcedures"/> are diffed alongside tables.
/// Added/changed procedures become <c>CREATE OR ALTER</c> SQL operations; removed ones become
/// <c>DROP PROCEDURE IF EXISTS</c>. EF invokes the differ once per direction, so rollback is automatic.
/// Both entry points are overridden, so a procedure-only edit is reported by
/// <c>dotnet ef migrations has-pending-model-changes</c> as well as scaffolded by <c>migrations add</c>.
/// </summary>
/// <remarks>
/// <para>
/// Register with <c>optionsBuilder.UseSqlServer(...).ReplaceService&lt;IMigrationsModelDiffer, StoredProcedureModelDiffer&gt;()</c>.
/// That single replacement keeps working for a model that declares procedures and nothing else.
/// </para>
/// <para>
/// The behaviour lives in <see cref="CodeFirstDatabaseObjectsModelDiffer"/>, which also diffs partition
/// functions, schemes, table placement and clustered columnstore indexes; this type is that differ under
/// the name it had when procedures were the only object kind, kept so that existing registrations compile
/// unchanged. A model that places a table on a partition scheme needs the matching SQL generator as well —
/// configure it with <see cref="CodeFirstDbContextOptionsBuilderExtensions.UseCodeFirstDatabaseObjects(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder)"/>,
/// and the differ says so if it is missing.
/// </para>
/// </remarks>
public sealed class StoredProcedureModelDiffer(
    IRelationalTypeMappingSource typeMappingSource,
    IMigrationsAnnotationProvider migrationsAnnotationProvider,
    IRelationalAnnotationProvider relationalAnnotationProvider,
    IRowIdentityMapFactory rowIdentityMapFactory,
    CommandBatchPreparerDependencies commandBatchPreparerDependencies,
    IMigrationsSqlGenerator migrationsSqlGenerator)
    : CodeFirstDatabaseObjectsModelDiffer(
        typeMappingSource,
        migrationsAnnotationProvider,
        relationalAnnotationProvider,
        rowIdentityMapFactory,
        commandBatchPreparerDependencies,
        migrationsSqlGenerator);
