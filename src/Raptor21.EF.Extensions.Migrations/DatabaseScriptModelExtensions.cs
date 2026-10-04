using System.Reflection;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;

namespace Raptor21.EF.Extensions.Migrations;

/// <summary>
/// Registers embedded procedure AND function scripts onto the EF model from one folder, so both travel through
/// migrations the way <see cref="StoredProcedureModelExtensions.RegisterStoredProcedures"/> carries procedures.
/// </summary>
public static partial class DatabaseScriptModelExtensions
{
    [GeneratedRegex(@"\bCREATE\s+OR\s+ALTER\s+(PROC(?:EDURE)?|FUNCTION)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex HeaderKindRegex();

    /// <summary>
    /// Loads every embedded <c>.sql</c> resource under <paramref name="resourcePrefix"/> from
    /// <paramref name="assembly"/>, classifies each by its header and records it as a model annotation:
    /// a <c>CREATE OR ALTER PROCEDURE</c> script under <see cref="StoredProcedureScript.AnnotationPrefix"/>
    /// (<c>Sp:schema.name</c>), exactly as <see cref="StoredProcedureModelExtensions.RegisterStoredProcedures"/>
    /// would, and a <c>CREATE OR ALTER FUNCTION</c> script under <see cref="FunctionScript.AnnotationPrefix"/>
    /// (<c>Fn:schema.name</c>). Either may open with a <see cref="ModuleScriptPreamble"/>.
    /// </summary>
    /// <remarks>
    /// The annotations round-trip through the model snapshot and are diffed by
    /// <see cref="CodeFirstDatabaseObjectsModelDiffer"/> (or <see cref="StoredProcedureModelDiffer"/>), which
    /// creates functions after the tables and before the procedures, and drops them after the procedures.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A script is neither a single <c>CREATE OR ALTER PROCEDURE</c> nor a single <c>CREATE OR ALTER FUNCTION</c>
    /// batch, its preamble sets an option twice, a function has no <c>RETURNS</c> clause, or two scripts declare
    /// the same object — a procedure and a function included, because they share one namespace in SQL Server.
    /// </exception>
    public static ModelBuilder RegisterDatabaseScripts(this ModelBuilder modelBuilder, Assembly assembly, string resourcePrefix)
    {
        ArgumentNullException.ThrowIfNull(modelBuilder);
        ArgumentNullException.ThrowIfNull(assembly);
        ArgumentException.ThrowIfNullOrWhiteSpace(resourcePrefix);

        // One map for both kinds: SQL Server keeps procedures and functions in one namespace per schema
        // (sys.objects), so a procedure and a function of the same name are a collision the server would
        // otherwise only report at deployment, as a CREATE OR ALTER refused for an incompatible object type.
        var origins = new Dictionary<string, (string Resource, string Kind)>(StringComparer.OrdinalIgnoreCase);

        foreach (var (resourceName, scriptName, sql) in EmbeddedScripts.Read(assembly, resourcePrefix))
        {
            var header = HeaderKindRegex().Match(sql);
            if (!header.Success)
                throw new InvalidOperationException(
                    $"Script '{scriptName}' must start with 'CREATE OR ALTER PROCEDURE' or 'CREATE OR ALTER FUNCTION' " +
                    "(idempotency requires CREATE OR ALTER, not plain CREATE; other object kinds are not carried).");

            var isFunction = header.Groups[1].Value.Equals("FUNCTION", StringComparison.OrdinalIgnoreCase);
            var kind = isFunction ? "function" : "procedure";

            string qualifiedName;
            if (isFunction)
            {
                qualifiedName = FunctionScript.ParseQualifiedName(sql, scriptName);

                // Read now so a function with no RETURNS clause is refused here, at registration, rather than at
                // the next migration whose diff has to compare its kind.
                FunctionScript.DetectKind(sql, scriptName);
            }
            else
            {
                qualifiedName = StoredProcedureScript.ParseQualifiedName(sql, scriptName);
            }

            try
            {
                ModuleScriptPreamble.Parse(sql);
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidOperationException($"Script '{scriptName}': {ex.Message}", ex);
            }

            if (!origins.TryAdd(qualifiedName, (resourceName, kind)))
            {
                var (incumbent, incumbentKind) = origins[qualifiedName];
                var what = incumbentKind == kind
                    ? $"{kind} '{qualifiedName}'"
                    : $"'{qualifiedName}' (one as a {incumbentKind}, one as a {kind} - SQL Server keeps both kinds in one namespace)";
                throw new InvalidOperationException(
                    $"Embedded resources '{incumbent}' and '{resourceName}' in assembly '{assembly.FullName}' both declare {what}.");
            }

            var prefix = isFunction ? FunctionScript.AnnotationPrefix : StoredProcedureScript.AnnotationPrefix;
            modelBuilder.Model.SetAnnotation(prefix + qualifiedName, sql);
        }

        return modelBuilder;
    }

    /// <summary>Reads the registered function scripts (canonical <c>schema.name</c> → script) from a model.</summary>
    public static IReadOnlyDictionary<string, string> GetFunctionScripts(IModel? model)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (model is null)
            return result;

        foreach (var annotation in model.GetAnnotations())
        {
            if (annotation.Name.StartsWith(FunctionScript.AnnotationPrefix, StringComparison.Ordinal)
                && annotation.Value is string sql)
            {
                result[annotation.Name[FunctionScript.AnnotationPrefix.Length..]] = sql;
            }
        }

        return result;
    }
}
