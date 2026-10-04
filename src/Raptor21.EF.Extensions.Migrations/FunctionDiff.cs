namespace Raptor21.EF.Extensions.Migrations;

/// <summary>The kind of migration step a function diff produces.</summary>
public enum FunctionOpKind
{
    /// <summary>Function was added or its body changed within its kind → run the (CREATE OR ALTER) script.</summary>
    CreateOrAlter,

    /// <summary>
    /// Function changed kind — scalar, inline or multi-statement table-valued — which <c>CREATE OR ALTER</c>
    /// refuses, so it is dropped and created again in one statement.
    /// </summary>
    Recreate,

    /// <summary>Function was removed → drop it.</summary>
    Drop,
}

/// <summary>A single function migration step (a piece of T-SQL plus what it does).</summary>
public sealed record FunctionOp(string QualifiedName, FunctionOpKind Kind, string Sql);

/// <summary>
/// Pure diff between two sets of function scripts (snapshot vs current), the function counterpart of
/// <see cref="StoredProcedureDiff"/>. Symmetric for the same reason: EF calls the differ once per direction,
/// so the one rule yields the rollback too.
/// </summary>
public static class FunctionDiff
{
    /// <summary>
    /// Computes the function operations to turn <paramref name="source"/> into <paramref name="target"/>.
    /// Keys are canonical <c>schema.name</c>; values are the full CREATE OR ALTER scripts. A
    /// <see cref="FunctionOpKind.CreateOrAlter"/> step carries the script wrapped by
    /// <see cref="StoredProcedureScript.WrapInExec"/>; a <see cref="FunctionOpKind.Recreate"/> step carries
    /// <c>DROP FUNCTION IF EXISTS</c> followed by that same wrapped script.
    /// </summary>
    /// <exception cref="InvalidOperationException">A target script is not a single CREATE OR ALTER FUNCTION batch.</exception>
    public static IReadOnlyList<FunctionOp> Compute(
        IReadOnlyDictionary<string, string> source,
        IReadOnlyDictionary<string, string> target)
    {
        var ops = new List<FunctionOp>();

        // Deterministic, stable order: by name. Inline table-valued functions are bound when they are created,
        // so one that reads another is created in this order too - a known gap, not a dependency sort.
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in source.Keys) names.Add(k);
        foreach (var k in target.Keys) names.Add(k);

        foreach (var name in names)
        {
            var inTarget = target.TryGetValue(name, out var targetSql);
            var inSource = source.TryGetValue(name, out var sourceSql);

            if (inTarget && !inSource)
            {
                ops.Add(new FunctionOp(name, FunctionOpKind.CreateOrAlter, StoredProcedureScript.WrapInExec(targetSql!)));
            }
            else if (inTarget && inSource && !ScriptEquals(sourceSql!, targetSql!))
            {
                // CREATE OR ALTER FUNCTION cannot turn a scalar function into a table-valued one or back
                // (SQL Server refuses it as an incompatible object type), so a kind change is a drop and a create.
                // Both statements travel in ONE operation: split across two, a failure between them would leave
                // the function gone, and the drop has no first-in-batch rule to keep it out of the same batch.
                if (FunctionScript.DetectKind(sourceSql!, name) != FunctionScript.DetectKind(targetSql!, name))
                    ops.Add(new FunctionOp(name, FunctionOpKind.Recreate,
                        $"{Drop(name)}\n{StoredProcedureScript.WrapInExec(targetSql!)}"));
                else
                    ops.Add(new FunctionOp(name, FunctionOpKind.CreateOrAlter, StoredProcedureScript.WrapInExec(targetSql!)));
            }
            else if (!inTarget && inSource)
            {
                ops.Add(new FunctionOp(name, FunctionOpKind.Drop, Drop(name)));
            }
        }

        return ops;
    }

    private static string Drop(string qualifiedName) => $"DROP FUNCTION IF EXISTS {StoredProcedureScript.Bracket(qualifiedName)};";

    // Line endings and the ends of the script, nothing else - the procedure rule, so the two kinds agree on
    // what counts as an edit.
    private static bool ScriptEquals(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
}
