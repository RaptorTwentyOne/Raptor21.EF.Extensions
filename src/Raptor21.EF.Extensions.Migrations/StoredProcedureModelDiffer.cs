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
/// Both entry points are overridden, so a procedure-only edit is reported by
/// <c>dotnet ef migrations has-pending-model-changes</c> as well as scaffolded by <c>migrations add</c>.
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
    /// <summary>
    /// Produces the operations that turn <paramref name="source"/> into <paramref name="target"/>: EF's own
    /// table and column operations, followed by one SQL operation per added, changed or removed procedure.
    /// </summary>
    public override IReadOnlyList<MigrationOperation> GetDifferences(IRelationalModel? source, IRelationalModel? target)
    {
        var operations = new List<MigrationOperation>(base.GetDifferences(source, target));

        // Procedure operations run after table operations (a procedure may reference tables).
        foreach (var op in DiffProcedures(source, target))
            operations.Add(new SqlOperation { Sql = op.Sql });

        return operations;
    }

    /// <summary>
    /// Reports whether the two models differ, counting a procedure whose script was added, changed or
    /// removed as a difference.
    /// </summary>
    public override bool HasDifferences(IRelationalModel? source, IRelationalModel? target)
    {
        // EF's two entry points share only the protected Diff method: HasDifferences is Diff(...).Any()
        // and GetDifferences sorts what Diff produced. Overriding GetDifferences alone therefore left
        // `dotnet ef migrations has-pending-model-changes` answering the wrong question — it reported a
        // model as up to date while the next `migrations add` would have written a migration, and that
        // command is what teams gate CI on. The same blindness silenced the PendingModelChangesWarning
        // that Migrate() raises, and let `migrations remove` treat a procedure-only migration as one it
        // could revert.
        //
        // base.HasDifferences, never this.GetDifferences: the base deliberately stops at the first
        // difference and skips Sort, whose two topological sorts over the CreateTable and DropTable
        // graphs are the expensive half of producing a migration that nobody has asked for here.
        //
        // OR, never replace. A table-only change still has to report true, and an OR can only add a
        // difference, never mask one.
        //
        // PROCEDURES FIRST, and the order is load-bearing rather than a preference. DiffProcedures is
        // partial - Compute wraps each script for deployment and refuses one it cannot wrap - so a
        // short-circuited base-first OR would skip that throw exactly when the base already found a
        // table change, while GetDifferences takes it every time. The two would then agree on every
        // boolean and disagree on whether they throw, which is the same defect as disagreeing on the
        // answer: the command succeeds, reports a difference for the wrong reason, and the scaffold
        // that follows fails instead. Procedures are an annotation-dictionary comparison and the base
        // is a full relational diff, so this is also the cheaper half to run first.
        return DiffProcedures(source, target).Count > 0 || base.HasDifferences(source, target);
    }

    // One expression, two callers. Two copies of this rule is how HasDifferences and GetDifferences would
    // drift apart again, and a HasDifferences that disagrees with the differ is worse than the defect it
    // replaces: the command answers "no changes" and the migration EF then scaffolds contradicts it.
    // The null-conditional is load-bearing rather than defensive — Migrator.HasPendingModelChanges passes
    // the snapshot as the source, and a project has no snapshot until its first migration.
    private static IReadOnlyList<ProcOp> DiffProcedures(IRelationalModel? source, IRelationalModel? target)
        => StoredProcedureDiff.Compute(
            StoredProcedureModelExtensions.GetStoredProcedureScripts(source?.Model),
            StoredProcedureModelExtensions.GetStoredProcedureScripts(target?.Model));
}
