namespace Raptor21.EF.Extensions.Migrations;

/// <summary>The kind of migration step a procedure diff produces.</summary>
public enum ProcOpKind
{
    /// <summary>Procedure was added or its body changed → run the (CREATE OR ALTER) script.</summary>
    CreateOrAlter,

    /// <summary>Procedure was removed → drop it.</summary>
    Drop,
}

/// <summary>A single procedure migration step (a piece of T-SQL plus what it does).</summary>
public sealed record ProcOp(string QualifiedName, ProcOpKind Kind, string Sql);

/// <summary>
/// Pure diff between two sets of procedure scripts (snapshot vs current). Symmetric: EF calls the
/// model differ once per direction (Up = snapshot→current, Down = current→snapshot), so this single
/// rule yields correct rollback for free — no separate Down logic needed.
/// </summary>
public static class StoredProcedureDiff
{
    /// <summary>
    /// Computes the procedure operations to turn <paramref name="source"/> into <paramref name="target"/>.
    /// Keys are canonical <c>schema.name</c>; values are the full CREATE OR ALTER scripts. A
    /// <see cref="ProcOpKind.CreateOrAlter"/> step carries the script already wrapped by
    /// <see cref="StoredProcedureScript.WrapInExec"/>, so <see cref="ProcOp.Sql"/> is the statement to run
    /// rather than the script as written.
    /// </summary>
    public static IReadOnlyList<ProcOp> Compute(
        IReadOnlyDictionary<string, string> source,
        IReadOnlyDictionary<string, string> target)
    {
        var ops = new List<ProcOp>();

        // Deterministic, stable order.
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in source.Keys) names.Add(k);
        foreach (var k in target.Keys) names.Add(k);

        foreach (var name in names)
        {
            var inTarget = target.TryGetValue(name, out var targetSql);
            var inSource = source.TryGetValue(name, out var sourceSql);

            // Only this branch is wrapped. CREATE OR ALTER PROCEDURE must be the first statement in its
            // batch and never is in a generated script; the DROP below carries no such rule, is already
            // valid inside EF's IF NOT EXISTS ... BEGIN ... END, and wrapping it would only hide what the
            // migration does behind a string literal. The cost of wrapping here rather than in a custom
            // IMigrationsSqlGenerator is that the scaffolded migration reads EXEC(N'...') instead of the
            // bare script; the gain is that the consumer keeps a single ReplaceService, and forgetting a
            // second one would silently reproduce exactly this defect.
            if (inTarget && (!inSource || !ScriptEquals(sourceSql!, targetSql!)))
                ops.Add(new ProcOp(name, ProcOpKind.CreateOrAlter, StoredProcedureScript.WrapInExec(targetSql!)));
            else if (!inTarget && inSource)
                ops.Add(new ProcOp(name, ProcOpKind.Drop, $"DROP PROCEDURE IF EXISTS {StoredProcedureScript.Bracket(name)};"));
        }

        return ops;
    }

    // Normalize line endings so CRLF/LF differences don't look like a real body change.
    private static bool ScriptEquals(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.Ordinal);

    private static string Normalize(string s) => s.Replace("\r\n", "\n").Replace("\r", "\n").Trim();
}
