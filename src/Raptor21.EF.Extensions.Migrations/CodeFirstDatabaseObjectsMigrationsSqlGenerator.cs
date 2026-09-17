using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Migrations.Operations;
using Microsoft.EntityFrameworkCore.Update;

namespace Raptor21.EF.Extensions.Migrations;

/// <summary>
/// The SQL Server migrations SQL generator, extended in two places. A <see cref="CreateTableOperation"/> carrying
/// <see cref="CodeFirstAnnotations.PartitionScheme"/> and <see cref="CodeFirstAnnotations.PartitionColumn"/> — put
/// there by <see cref="CodeFirstDatabaseObjectsModelDiffer"/> — is written as
/// <c>CREATE TABLE ... ON [scheme]([column])</c>. A <see cref="SqlOperation"/> carrying
/// <see cref="CodeFirstAnnotations.SuppressTransaction"/> runs outside the migration's transaction, which is how
/// a full-text statement survives the scaffold: EF's C# generator writes <c>migrationBuilder.Sql("...")</c>
/// without the <c>suppressTransaction</c> argument but with the operation's annotations. Every other operation
/// is the provider's own.
/// </summary>
/// <remarks>
/// Installed by <see cref="CodeFirstDbContextOptionsBuilderExtensions.UseCodeFirstDatabaseObjects(Microsoft.EntityFrameworkCore.DbContextOptionsBuilder)"/>.
/// The <c>ON</c> clause is appended between the provider's unterminated <c>CREATE TABLE ... (...)</c> and the
/// terminator the provider would have written, so the statement is the provider's byte for byte plus the
/// clause, in the plain script and inside the <c>IF NOT EXISTS ... BEGIN ... END</c> of an idempotent one.
/// </remarks>
public class CodeFirstDatabaseObjectsMigrationsSqlGenerator(
    MigrationsSqlGeneratorDependencies dependencies,
    ICommandBatchPreparer commandBatchPreparer)
    : SqlServerMigrationsSqlGenerator(dependencies, commandBatchPreparer)
{
    // The provider's own annotation names, spelled the way SqlServerAnnotationNames spells them.
    private const string IsTemporal = "SqlServer:IsTemporal";
    private const string MemoryOptimized = "SqlServer:MemoryOptimized";

    /// <summary>
    /// Writes the provider's statement, outside the migration's transaction when the operation carries
    /// <see cref="CodeFirstAnnotations.SuppressTransaction"/> — the flag the scaffold dropped, restored.
    /// </summary>
    protected override void Generate(SqlOperation operation, IModel? model, MigrationCommandListBuilder builder)
    {
        if (operation[CodeFirstAnnotations.SuppressTransaction] is true)
            operation.SuppressTransaction = true;

        base.Generate(operation, model, builder);
    }

    /// <summary>
    /// Writes the provider's <c>CREATE TABLE</c>, followed by <c>ON [scheme]([column])</c> when the operation
    /// carries a partition placement.
    /// </summary>
    /// <exception cref="NotSupportedException">The table is also temporal or memory-optimized.</exception>
    /// <exception cref="ArgumentException">
    /// <paramref name="terminate"/> is <see langword="false"/> and the table or a column has a comment — the
    /// provider's own rule, restated because the comment statements can only follow a terminated command.
    /// </exception>
    protected override void Generate(
        CreateTableOperation operation,
        IModel? model,
        MigrationCommandListBuilder builder,
        bool terminate = true)
    {
        var scheme = operation[CodeFirstAnnotations.PartitionScheme] as string;
        var column = operation[CodeFirstAnnotations.PartitionColumn] as string;

        if (scheme is null && column is null)
        {
            base.Generate(operation, model, builder, terminate);
            return;
        }

        if (string.IsNullOrWhiteSpace(scheme) || string.IsNullOrWhiteSpace(column))
            throw new InvalidOperationException(
                $"CreateTableOperation for '{operation.Name}' carries only one half of a partition placement " +
                $"(scheme '{scheme}', column '{column}'). Both annotations are written together by " +
                "CodeFirstDatabaseObjectsModelDiffer; a migration edited by hand must keep both.");

        // Both of these make the provider write a WITH (...) clause after the column list — SYSTEM_VERSIONING
        // for a temporal table, MEMORY_OPTIMIZED for an in-memory one — and a temporal table's history table
        // would need placing as well. Neither combination has been driven against a server, and a
        // partitioned memory-optimized table does not exist in SQL Server at all, so both are refused rather
        // than emitted in an order that has not been proven.
        if (operation[IsTemporal] as bool? == true)
            throw new NotSupportedException(
                $"Table '{operation.Name}' is both temporal and placed on partition scheme '{scheme}'. Partitioning a " +
                "temporal table is not supported by this package; partition it by hand or drop one of the two.");

        if (operation[MemoryOptimized] as bool? == true)
            throw new NotSupportedException(
                $"Table '{operation.Name}' is both memory-optimized and placed on partition scheme '{scheme}'. SQL Server " +
                "does not partition memory-optimized tables.");

        // The provider refuses to leave a command unterminated when a comment exists, because the
        // sp_addextendedproperty calls that carry comments have to follow a terminated CREATE TABLE. The rule
        // is restated here so that a subclass calling this with terminate:false gets the provider's answer.
        var tableComment = operation.Comment;
        var columnComments = operation.Columns.Select(c => c.Comment).ToArray();
        var hasComments = tableComment is not null || columnComments.Any(c => c is not null);
        if (hasComments && !terminate)
            throw new ArgumentException(
                "Cannot produce unterminated SQL for a CreateTableOperation that carries comments.", nameof(terminate));

        // The provider's CREATE TABLE, without its terminator, so the ON clause can follow the closing
        // parenthesis. The comments are hidden for that one call — the provider would otherwise throw the
        // exception restated above — and written below, exactly where the provider writes them.
        if (hasComments)
        {
            operation.Comment = null;
            foreach (var c in operation.Columns)
                c.Comment = null;
        }

        try
        {
            base.Generate(operation, model, builder, terminate: false);
        }
        finally
        {
            if (hasComments)
            {
                operation.Comment = tableComment;
                for (var i = 0; i < columnComments.Length; i++)
                    operation.Columns[i].Comment = columnComments[i];
            }
        }

        builder
            .Append(" ON ")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(scheme))
            .Append("(")
            .Append(Dependencies.SqlGenerationHelper.DelimitIdentifier(column))
            .Append(")");

        // From here on this is the provider's own ending, as SqlServerMigrationsSqlGenerator writes it for a
        // table that is neither temporal nor memory-optimized: with comments, the terminator, one
        // sp_addextendedproperty per comment (the first declares the variables, the rest reuse them), and
        // EndCommand; without, the terminator and EndCommand when asked to terminate. EndCommand rather than
        // EndStatement, and with the transaction left on, because that is the call the provider makes.
        if (hasComments)
        {
            builder.AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator);

            var firstDescription = true;
            if (tableComment is not null)
            {
                AddDescription(builder, tableComment, operation.Schema, operation.Name);
                firstDescription = false;
            }

            foreach (var c in operation.Columns)
            {
                if (c.Comment is null)
                    continue;

                AddDescription(builder, c.Comment, operation.Schema, operation.Name, c.Name, omitVariableDeclarations: !firstDescription);
                firstDescription = false;
            }

            builder.EndCommand();
        }
        else if (terminate)
        {
            builder
                .AppendLine(Dependencies.SqlGenerationHelper.StatementTerminator)
                .EndCommand();
        }
    }
}
