using System.Reflection;
using System.Text;
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

        var prefix = resourcePrefix.TrimEnd('.') + ".";

        foreach (var resourceName in assembly.GetManifestResourceNames())
        {
            if (!resourceName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                || !resourceName.EndsWith(".sql", StringComparison.OrdinalIgnoreCase))
                continue;

            using var stream = assembly.GetManifestResourceStream(resourceName)!;
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var sql = reader.ReadToEnd();

            var scriptName = resourceName[prefix.Length..];
            var qualifiedName = StoredProcedureScript.ParseQualifiedName(sql, scriptName);

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
