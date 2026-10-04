using System.Reflection;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Raptor21.EF.Extensions.Migrations;

/// <summary>Registers embedded stored-procedure scripts onto the EF model so they participate in migrations.</summary>
public static class StoredProcedureModelExtensions
{
    /// <summary>
    /// Loads every embedded <c>.sql</c> resource under <paramref name="resourcePrefix"/> from
    /// <paramref name="assembly"/> and records each as a model annotation (<c>Sp:schema.name</c> = script).
    /// These annotations are serialized into the model snapshot, so changes are diffed by
    /// <see cref="StoredProcedureModelDiffer"/> at <c>Add-Migration</c> time.
    /// </summary>
    public static ModelBuilder RegisterStoredProcedures(this ModelBuilder modelBuilder, Assembly assembly, string resourcePrefix)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePrefix);

        // Which resource claimed each procedure name, so a collision can name BOTH files. Naming only the
        // loser leaves the developer hunting for the one it collided with.
        var origins = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (resourceName, scriptName, sql) in EmbeddedScripts.Read(assembly, resourcePrefix))
        {
            var qualifiedName = StoredProcedureScript.ParseQualifiedName(sql, scriptName);

            // The annotation key is the procedure's qualified name, not the file's, so two .sql files that
            // both CREATE dbo.usp_Foo land on one key. A plain SetAnnotation lets the second overwrite the
            // first, and the loser is a file that is plainly in the repository, is plainly an
            // EmbeddedResource, and is nowhere in the migration - which surfaces far away as a procedure
            // that never changes no matter how it is edited. Refusing both costs nothing; this is the only
            // loader left, so nothing else is going to catch it.
            if (!origins.TryAdd(qualifiedName, resourceName))
                throw new InvalidOperationException(
                    $"Embedded resources '{origins[qualifiedName]}' and '{resourceName}' in assembly " +
                    $"'{assembly.FullName}' both declare procedure '{qualifiedName}'.");

            modelBuilder.Model.SetAnnotation(StoredProcedureScript.AnnotationPrefix + qualifiedName, sql);
        }

        return modelBuilder;
    }

    /// <summary>Reads the registered procedure scripts (canonical <c>schema.name</c> → script) from a model.</summary>
    public static IReadOnlyDictionary<string, string> GetStoredProcedureScripts(IModel? model)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (model is null)
            return result;

        foreach (var annotation in model.GetAnnotations())
        {
            if (annotation.Name.StartsWith(StoredProcedureScript.AnnotationPrefix, StringComparison.Ordinal)
                && annotation.Value is string sql)
            {
                result[annotation.Name[StoredProcedureScript.AnnotationPrefix.Length..]] = sql;
            }
        }

        return result;
    }
}
