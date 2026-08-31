namespace Raptor21.EF.Extensions.Migrations;

/// <summary>The kind of migration step a partition diff produces.</summary>
public enum PartitionOpKind
{
    /// <summary>A partition function is new → <c>CREATE PARTITION FUNCTION</c>, guarded by <c>IF NOT EXISTS</c>.</summary>
    CreateFunction,

    /// <summary>A partition scheme is new → <c>CREATE PARTITION SCHEME</c>, guarded by <c>IF NOT EXISTS</c>.</summary>
    CreateScheme,

    /// <summary>A boundary left an existing function → <c>ALTER PARTITION FUNCTION ... MERGE RANGE</c>, guarded by <c>IF EXISTS</c>.</summary>
    MergeRange,

    /// <summary>
    /// A boundary joined an existing function → <c>ALTER PARTITION SCHEME ... NEXT USED</c> for every scheme bound
    /// to the function in either model, then <c>ALTER PARTITION FUNCTION ... SPLIT RANGE</c>, guarded by <c>IF NOT EXISTS</c>.
    /// </summary>
    SplitRange,

    /// <summary>A partition scheme was removed → <c>DROP PARTITION SCHEME</c>, guarded by <c>IF EXISTS</c>.</summary>
    DropScheme,

    /// <summary>A partition function was removed → <c>DROP PARTITION FUNCTION</c>, guarded by <c>IF EXISTS</c>.</summary>
    DropFunction,
}

/// <summary>A single partition migration step: the object it concerns, what it does and the T-SQL that does it.</summary>
/// <param name="Name">The function or scheme name.</param>
/// <param name="Kind">What the step does.</param>
/// <param name="Sql">The statement to run.</param>
public sealed record PartitionOp(string Name, PartitionOpKind Kind, string Sql)
{
    /// <summary>
    /// Whether this step removes an object. The differ emits removals after the table operations and
    /// everything else before them, so that a table is never created on a scheme that does not exist yet and
    /// a scheme is never dropped while a table still sits on it.
    /// </summary>
    public bool IsDrop => Kind is PartitionOpKind.DropScheme or PartitionOpKind.DropFunction;
}

/// <summary>
/// Pure diff between two <see cref="PartitionLayout"/>s. Symmetric like <see cref="StoredProcedureDiff"/>:
/// EF calls the differ once per direction, so a boundary added in <c>Up</c> is merged back out in <c>Down</c>
/// by the same rule.
/// </summary>
public static class PartitionDiff
{
    /// <summary>
    /// Computes the steps that turn <paramref name="source"/> into <paramref name="target"/>, in the order they
    /// must run: function creates, scheme creates, merges, splits, scheme drops, function drops.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// A function changed its type or range direction, or a scheme changed its function or filegroup. SQL
    /// Server has no <c>ALTER</c> for either, and the only route is to move every dependent table off the
    /// object, drop it and create it again — a migration to write by hand with the data volume in view.
    /// </exception>
    public static IReadOnlyList<PartitionOp> Compute(PartitionLayout source, PartitionLayout target)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(target);

        var createFunctions = new List<PartitionOp>();
        var createSchemes = new List<PartitionOp>();
        var merges = new List<PartitionOp>();
        var splits = new List<PartitionOp>();
        var dropSchemes = new List<PartitionOp>();
        var dropFunctions = new List<PartitionOp>();

        foreach (var name in Names(source.Functions.Keys, target.Functions.Keys))
        {
            var inSource = source.Functions.TryGetValue(name, out var before);
            var inTarget = target.Functions.TryGetValue(name, out var after);

            if (inTarget && !inSource)
            {
                createFunctions.Add(new PartitionOp(after!.Name, PartitionOpKind.CreateFunction, CreateFunctionSql(after)));
            }
            else if (inSource && !inTarget)
            {
                dropFunctions.Add(new PartitionOp(before!.Name, PartitionOpKind.DropFunction, DropFunctionSql(before)));
            }
            else
            {
                DiffBoundaries(before!, after!, source, target, merges, splits);
            }
        }

        foreach (var name in Names(source.Schemes.Keys, target.Schemes.Keys))
        {
            var inSource = source.Schemes.TryGetValue(name, out var before);
            var inTarget = target.Schemes.TryGetValue(name, out var after);

            if (inTarget && !inSource)
            {
                createSchemes.Add(new PartitionOp(after!.Name, PartitionOpKind.CreateScheme, CreateSchemeSql(after)));
            }
            else if (inSource && !inTarget)
            {
                dropSchemes.Add(new PartitionOp(before!.Name, PartitionOpKind.DropScheme, DropSchemeSql(before)));
            }
            else if (!SameName(before!.FunctionName, after!.FunctionName)
                || !SameName(before.Filegroup, after.Filegroup))
            {
                throw new NotSupportedException(
                    $"Partition scheme '{after.Name}' changed from function '{before.FunctionName}' on filegroup " +
                    $"'{before.Filegroup}' to function '{after.FunctionName}' on filegroup '{after.Filegroup}'. " +
                    "SQL Server cannot alter a scheme's function or filegroup in place: move the tables off the " +
                    "scheme, drop it and create it again in a migration written by hand, or declare a new scheme " +
                    "under a new name.");
            }
        }

        // Creates before everything that could depend on them, drops after everything that could still
        // depend on them. Merges run before splits so that a boundary "moved" in one edit shrinks the
        // function before it grows it again.
        return
        [
            .. createFunctions,
            .. createSchemes,
            .. merges,
            .. splits,
            .. dropSchemes,
            .. dropFunctions,
        ];
    }

    private static void DiffBoundaries(
        PartitionFunctionDefinition before,
        PartitionFunctionDefinition after,
        PartitionLayout source,
        PartitionLayout target,
        List<PartitionOp> merges,
        List<PartitionOp> splits)
    {
        if (!SameName(before.SqlType, after.SqlType) || before.Range != after.Range)
        {
            throw new NotSupportedException(
                $"Partition function '{after.Name}' changed from {before.SqlType} RANGE {Range(before)} to " +
                $"{after.SqlType} RANGE {Range(after)}. SQL Server can only split and merge a function's " +
                "boundaries; its type and range direction are fixed at creation. Move every dependent table off " +
                "the function, drop it and create it again in a migration written by hand, or declare a new " +
                "function under a new name.");
        }

        var removed = before.Boundaries.Where(b => !after.Boundaries.Contains(b, StringComparer.Ordinal));
        foreach (var boundary in removed)
            merges.Add(new PartitionOp(after.Name, PartitionOpKind.MergeRange, MergeSql(after, boundary)));

        // The schemes that will exist when the split RUNS: the target's, because a scheme this migration
        // creates is created before the split; the source's too, because a scheme this migration drops is
        // dropped after it and is still bound to the function at that moment — error 7707 if its NEXT USED
        // marker was consumed by an earlier split and nothing here renews it. The target wins a name
        // collision, though the two can only disagree in ways this method has already refused.
        var bound = new Dictionary<string, PartitionSchemeDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in source.Schemes.Values.Concat(target.Schemes.Values))
        {
            if (SameName(candidate.FunctionName, after.Name))
                bound[candidate.Name] = candidate;
        }

        var schemes = bound.Values
            .OrderBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        var added = after.Boundaries.Where(b => !before.Boundaries.Contains(b, StringComparer.Ordinal));
        foreach (var boundary in added)
            splits.Add(new PartitionOp(after.Name, PartitionOpKind.SplitRange, SplitSql(after, boundary, schemes)));
    }

    private static string CreateFunctionSql(PartitionFunctionDefinition function)
        => $"IF NOT EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = {SqlText.NLiteral(function.Name)})\n" +
           $"    CREATE PARTITION FUNCTION {SqlText.Delimit(function.Name)} ({function.SqlType}) " +
           $"AS RANGE {Range(function)} FOR VALUES ({string.Join(", ", function.Boundaries)});";

    private static string DropFunctionSql(PartitionFunctionDefinition function)
        => $"IF EXISTS (SELECT 1 FROM sys.partition_functions WHERE name = {SqlText.NLiteral(function.Name)})\n" +
           $"    DROP PARTITION FUNCTION {SqlText.Delimit(function.Name)};";

    private static string CreateSchemeSql(PartitionSchemeDefinition scheme)
        => $"IF NOT EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = {SqlText.NLiteral(scheme.Name)})\n" +
           $"    CREATE PARTITION SCHEME {SqlText.Delimit(scheme.Name)} AS PARTITION {SqlText.Delimit(scheme.FunctionName)} " +
           $"ALL TO ({SqlText.Delimit(scheme.Filegroup)});";

    private static string DropSchemeSql(PartitionSchemeDefinition scheme)
        => $"IF EXISTS (SELECT 1 FROM sys.partition_schemes WHERE name = {SqlText.NLiteral(scheme.Name)})\n" +
           $"    DROP PARTITION SCHEME {SqlText.Delimit(scheme.Name)};";

    private static string SplitSql(
        PartitionFunctionDefinition function,
        string boundary,
        IReadOnlyList<PartitionSchemeDefinition> schemes)
    {
        // Every scheme on the function must name a NEXT USED filegroup before the function can split, or
        // SQL Server refuses with error 7707. ALL TO already marks its one filegroup as next used, so
        // repeating it is harmless; omitting it on a scheme whose marker was consumed by an earlier split is
        // not.
        var nextUsed = string.Concat(schemes.Select(s =>
            $"    ALTER PARTITION SCHEME {SqlText.Delimit(s.Name)} NEXT USED {SqlText.Delimit(s.Filegroup)};\n"));

        return $"IF NOT EXISTS ({BoundaryExists(function, boundary)})\n" +
               "BEGIN\n" +
               nextUsed +
               $"    ALTER PARTITION FUNCTION {SqlText.Delimit(function.Name)}() SPLIT RANGE ({boundary});\n" +
               "END;";
    }

    private static string MergeSql(PartitionFunctionDefinition function, string boundary)
        => $"IF EXISTS ({BoundaryExists(function, boundary)})\n" +
           $"    ALTER PARTITION FUNCTION {SqlText.Delimit(function.Name)}() MERGE RANGE ({boundary});";

    // sys.partition_range_values.value is a sql_variant, and a sql_variant compared with a plain literal is
    // compared by type family first: a datetime2 boundary against the string '2026-08-01' is simply "not
    // equal", never converted. The LITERAL is converted, never the catalog value: SQL Server does not
    // promise to apply the name filter before the conversion, so a CONVERT over prv.value can reach the rows
    // of every other partition function in the database, and converting another function's int boundary to
    // datetime2 is an error (Msg 529), not a mismatch. Wrapping the converted literal back in sql_variant
    // makes the equality one between two variants of the same declared type — the comparison the guard
    // means — while rows of other types are never converted and simply compare unequal.
    private static string BoundaryExists(PartitionFunctionDefinition function, string boundary)
        => "SELECT 1 FROM sys.partition_range_values AS prv " +
           "INNER JOIN sys.partition_functions AS pf ON pf.function_id = prv.function_id " +
           $"WHERE pf.name = {SqlText.NLiteral(function.Name)} AND prv.value = CONVERT(sql_variant, CONVERT({function.SqlType}, {boundary}))";

    private static string Range(PartitionFunctionDefinition function)
        => function.Range == PartitionRange.Right ? "RIGHT" : "LEFT";

    private static bool SameName(string a, string b) => string.Equals(a.Trim(), b.Trim(), StringComparison.OrdinalIgnoreCase);

    // Deterministic, stable order, the same way StoredProcedureDiff walks its names.
    private static IEnumerable<string> Names(IEnumerable<string> source, IEnumerable<string> target)
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in source) names.Add(k);
        foreach (var k in target) names.Add(k);
        return names;
    }
}
