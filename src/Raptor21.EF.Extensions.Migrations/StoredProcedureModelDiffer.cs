using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Internal;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.EntityFrameworkCore.Update.Internal;

namespace Raptor21.EF.Extensions.Migrations;

/// <summary>
/// Extends EF Core's migration model differ so that stored procedures registered via
/// <see cref="StoredProcedureModelExtensions.RegisterStoredProcedures"/> are diffed alongside tables.
/// Added/changed procedures become <c>CREATE OR ALTER</c> SQL operations; removed ones become
/// <c>DROP PROCEDURE IF EXISTS</c>. EF invokes the differ once per direction, so rollback is automatic.
/// </summary>
/// <remarks>
/// Register with <c>optionsBuilder.UseSqlServer(...).ReplaceService&lt;IMigrationsModelDiffer, StoredProcedureModelDiffer&gt;()</c>.
/// Subclassing <see cref="MigrationsModelDiffer"/> requires forwarding its (internal-flagged) dependencies; EF1001 is suppressed at project level.
/// </remarks>
public sealed class StoredProcedureModelDiffer(
    IRelationalTypeMappingSource typeMappingSource,
    IMigrationsAnnotationProvider migrationsAnnotationProvider,
    IRelationalAnnotationProvider relationalAnnotationProvider,
    IRowIdentityMapFactory rowIdentityMapFactory,
    CommandBatchPreparerDependencies commandBatchPreparerDependencies)
    : MigrationsModelDiffer(
        typeMappingSource,
        migrationsAnnotationProvider,
        relationalAnnotationProvider,
        rowIdentityMapFactory,
        commandBatchPreparerDependencies)
{
    public override IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
    {
        var operations = new List<MigrationOperation>(base.GetDifferences(source, target));

        var sourceProcs = StoredProcedureModelExtensions.GetStoredProcedureScripts(source?.Model);
        var targetProcs = StoredProcedureModelExtensions.GetStoredProcedureScripts(target?.Model);

        // Procedure operations run after table operations (a procedure may reference tables).
        foreach (var op in StoredProcedureDiff.Compute(sourceProcs, targetProcs))
            operations.Add(new SqlOperation { Sql = op.Sql });

        return operations;
    }
}
