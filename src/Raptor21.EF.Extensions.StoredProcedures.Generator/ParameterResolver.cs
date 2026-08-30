using System;
using System.Collections.Generic;
using System.Globalization;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator;

/// <summary>
/// Merges what the C# declaration states with what the procedure's .sql declares, and renders the
/// contract entries the emitter consumes.
/// </summary>
/// <remarks>
/// <para>
/// This is the only place the two sources meet. The rule is per facet, not per parameter: whatever the
/// <c>[Sql]</c> attribute states wins, whatever it leaves unstated is filled from the matched procedure
/// parameter, and the SQL type falls back to the .NET type last - except where the procedure declared a
/// construct the parser declined, which is the one case a .NET guess is known to be wrong rather than
/// merely unverified (SPG016). A declaration that spells every facet out renders byte-for-byte what it
/// renders today, with or without a script in sight; one that does not is told what moved (SPG017).
/// </para>
/// <para>
/// It is also the only place a .sql-derived diagnostic is reported. The .sql branch of the pipeline
/// answers questions and says nothing on its own, so a script that fails to parse, declares no
/// procedure, or matches no declaration costs a consumer nothing - which is what lets a large corpus of
/// legacy scripts be promoted to <c>AdditionalFiles</c> without a wall of warnings. The advisories that
/// are about the script rather than about the code go further and wait until the script has actually
/// moved the emitted contract, because a warning proportional to the size of the corpus is a wall of its
/// own for anyone building with <c>TreatWarningsAsErrors</c>. That gate measures the rendered text and
/// not the provenance of any one facet: where a fact came from is invisible to the consumer, and two
/// spellings of the same contract must not produce different build logs.
/// </para>
/// </remarks>
internal static class ParameterResolver
{
    private const string ContractsNs = StoredProcedureGenerator.ContractsNs;

    /// <summary>Resolves one method against the compilation's procedure headers.</summary>
    /// <param name="model">The method as the syntax transform saw it.</param>
    /// <param name="index">Every parsed header, indexed by procedure identity.</param>
    /// <param name="ct">Cancellation for the pipeline step.</param>
    public static ResolvedMethod Resolve(MethodModel model, SqlHeaderIndex index, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        // The model's own diagnostics (SPG002, SPG004) are carried forward, never re-reported: they live
        // in the merged list only, so nothing fires twice.
        var diagnostics = new List<DiagnosticInfo>(model.Diagnostics.AsArray());
        var procDisplay = model.Schema + "." + model.ProcName;
        var key = ProcKey.From(model.Schema, model.ProcName);

        var header = default(SqlProcHeader);
        var haveHeader = false;

        // Two files declaring one identity is ambiguous by any reading, so no facts are taken. Saying so
        // waits until the loop has shown that a parameter actually wanted those facts - see the SPG015
        // report below, next to SPG014, which answers the same question.
        var ambiguous = index.TryGetDuplicate(key, out var duplicate);
        if (!ambiguous)
        {
            haveHeader = index.TryGetHeader(key, out header);
        }

        var drafts = model.ParamDrafts.AsArray();
        var spParams = new List<SpParam>(drafts.Length);

        // File parameters some C# parameter claimed. What is left over is SPG011.
        var boundSqlNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var outputCount = 0;
        var needsFacts = false;

        // Whether the script moved any parameter's emitted contract. A method every one of whose
        // parameters renders what it rendered before the script was promoted has not been touched by it,
        // and the advisories about the script's contents are gated on this rather than on the script
        // merely existing.
        var scriptFedTheMethod = false;

        // Only a tuple with elements after the RETURN value has anywhere to put an OUTPUT parameter's
        // value; every other return shape binds the parameter as Direction.Output and drops what comes
        // back. Read once here because SPG018 asks it per parameter.
        var canReturnOutputs = model.Return == ReturnCategory.ReturnWithOutputs;

        foreach (var draft in drafts)
        {
            ct.ThrowIfCancellationRequested();

            // The lookup key is the effective SQL name: what the attribute stated, or '@' + the C#
            // parameter name. Matching is by name and only by name - a positional fallback that landed
            // wrong would send a value to the wrong parameter, and the runtime validator compares by
            // position too, so nothing downstream could catch it.
            var lookupName = draft.SqlName ?? ("@" + draft.CSharpName);

            var fileParam = default(SqlHeaderParam);
            var matched = haveHeader && TryMatchParameter(header, lookupName, out fileParam);
            if (matched)
            {
                boundSqlNames.Add(fileParam.Name);
            }

            // A table-valued, cursor or user-defined parameter is matched - so it is not "missing" - but
            // supplies no facts beyond its name: ProcParamSpec has no field for it and ApplySqlType has
            // no path to bind it.
            var factsAvailable = matched && fileParam.Shape == SqlParamShape.Ordinary;
            var fileTypeAvailable = factsAvailable && fileParam.TypeName.Length > 0;
            var typeCameFromFile = draft.TypeName is null && fileTypeAvailable;

            // A negative length in a [Sql] attribute is the sentinel the shipped generator used for
            // "unstated": ParseSqlAttribute defaulted Length to -1 and RenderSqlType rendered a length
            // only when it was zero or more, so [Sql("@Blob", "varbinary", -1)] emitted a bare
            // SqlTypeSpec("varbinary"). Length is int? now, and a -1 that reaches the emitter is genuine
            // MAX - which is why it may only reach it from the script, where "varbinary(max)" says MAX
            // and can say nothing else. Reading the attribute's -1 as MAX instead would rewrite the
            // contract of a declaration that compiles today with no .sql anywhere in the compilation,
            // which is the one thing this merge may not do. It stays "unstated" here too, not only at
            // render time, so the script may still fill it and SPG010 has nothing to contradict.
            var statedLength = draft.Length.HasValue && draft.Length.Value >= 0 ? draft.Length : null;

            // On a match the emitted name is the file's exact spelling, not the derived lookup key: a
            // bare 'string sku' looks '@sku' up, matches '@Sku' case-insensitively, and emits '@Sku'.
            var sqlName = draft.SqlName ?? (matched ? fileParam.Name : null);

            // What the declaration renders on its own, with no script in the compilation, kept beside the
            // merged value so SPG017 can name both.
            var statedType = draft.TypeName ?? draft.InferredSqlType;
            var typeName = draft.TypeName
                ?? (fileTypeAvailable ? fileParam.TypeName : null)
                ?? draft.InferredSqlType;
            var length = statedLength ?? (factsAvailable ? fileParam.Length : null);
            var precision = draft.Precision ?? (factsAvailable ? fileParam.Precision : null);
            var scale = draft.Scale ?? (factsAvailable ? fileParam.Scale : null);

            // OUTPUT is the one asymmetric facet. A bool has no "unstated" spelling, and the flag changes
            // the direction of the emitted call rather than only its metadata - so any [Sql] at all makes
            // the attribute authoritative, and the file is consulted only for a parameter that carries
            // none. That is also what makes the no-new-errors guarantee provable: a consumer who compiles
            // today has a [Sql] on every parameter, so no OUTPUT count can change and SPG009 cannot newly
            // fire. A disagreement is still surfaced - as SPG010 where the attribute stated the opposite
            // direction, and as SPG020 where it stated none and the script's OUTPUT was therefore
            // declined. Neither changes what is emitted.
            var isOutput = draft.HasSqlAttribute
                ? draft.Output ?? false
                : factsAvailable && fileParam.IsOutput;

            // The declaration's contract entry as it renders with no script in the compilation at all,
            // and the merged one that will actually be emitted. Null on either side means "this does not
            // render" - a declaration that states no name is SPG003 and one that states no type is
            // SPG005 - which is never equal to a spec that does render, so a parameter the script is
            // rescuing from an error counts as moved.
            var declaredSpec = draft.SqlName is not null && statedType is not null
                ? RenderParamSpec(
                    draft.SqlName,
                    statedType,
                    statedLength,
                    draft.Precision,
                    draft.Scale,
                    draft.HasSqlAttribute && (draft.Output ?? false))
                : null;

            var mergedSpec = sqlName is not null && typeName is not null
                ? RenderParamSpec(sqlName, typeName, length, precision, scale, isOutput)
                : null;

            // Whether the script moved this parameter's emitted contract. This is the gate for every
            // advisory that is about the script rather than about the code, and it measures the rendered
            // text rather than the provenance of any one facet, because provenance answers a question
            // nobody asked: [Sql("@A", "int")] takes nothing from a file that declares "@A int" while
            // [Sql("@A", 32)] takes the type from one that declares "@A varchar(32)", and both render
            // exactly what they rendered before the file existed. Gating on provenance made the second
            // spelling louder than the first over identical output, which is a report a consumer cannot
            // act on and cannot even reproduce by reading their own contract.
            var tookFactsFromFile = matched && !string.Equals(declaredSpec, mergedSpec, StringComparison.Ordinal);
            scriptFedTheMethod |= tookFactsFromFile;

            if (factsAvailable)
            {
                ReportContradictions(diagnostics, model, draft, statedLength, fileParam, procDisplay);

                // SQL Server reads a bare 'varchar' as 'varchar(1)'. Recording that faithfully would set
                // SqlParameter.Size = 1, truncate every value to one character at the server, and still
                // pass startup validation, because sys.parameters.max_length really is 1. Nothing is
                // inferred; the developer is told the procedure is almost certainly wrong - but only when
                // the script moved this parameter's contract, because for any other the emitted contract
                // is what it always was and a corpus of legacy scripts is full of bare varchars.
                if (fileParam.HasNoLengthArgument && statedLength is null && tookFactsFromFile)
                {
                    diagnostics.Add(new DiagnosticInfo(
                        Diagnostics.ScriptParameterHasNoLength,
                        SqlLocation(header.FilePath, fileParam.Span),
                        Args(procDisplay, fileParam.Name, fileParam.TypeName)));
                }
            }

            // Only ever asserted against a type the file supplied. A hand-written [Sql(TypeName)] is the
            // developer's call and is never second-guessed.
            if (typeCameFromFile && typeName is not null && !CanBind(typeName, draft.Clr))
            {
                diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.ScriptTypeCannotBind,
                    draft.Location,
                    Args(procDisplay, fileParam.Name, typeName, draft.ClrTypeDisplay, draft.CSharpName, model.MethodName)));
            }

            if (sqlName is null)
            {
                needsFacts = true;
                diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.ParamMissingSql,
                    draft.Location,
                    Args(draft.CSharpName, model.MethodName, lookupName, procDisplay)));
                continue;
            }

            // The parser declined to type this one - sysname, sql_variant, timestamp and the spatial
            // types, or a shape that carries no type at all: a TVP, a CURSOR, a user-defined type. The
            // .NET type is not a fallback for it. Everywhere else the .NET guess is merely unverified,
            // and here it is known to be wrong, because the procedure's real type is one this generator
            // cannot bind - so emitting the guess would trade a build error for a boot failure. Refused
            // only for a parameter carrying no [Sql] at all, which is an SPG003 error today: an
            // attributed parameter keeps the guess it already emits, since making that an error would
            // wall a consumer whose code compiles - and is told so by SPG019 just below, because keeping
            // a guess that cannot bind is not something to do in silence either.
            if (matched && !fileTypeAvailable && !draft.HasSqlAttribute)
            {
                needsFacts = true;
                diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.ScriptTypeDeclined,
                    draft.Location,
                    Args(procDisplay, fileParam.Name, DescribeDeclinedType(fileParam), draft.CSharpName, model.MethodName)));
                continue;
            }

            if (typeName is null)
            {
                needsFacts = true;
                diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.UnsupportedParamType,
                    draft.Location,
                    Args(draft.CSharpName, model.MethodName, draft.ClrTypeDisplay, procDisplay)));
                continue;
            }

            // SPG016's attributed mirror, and the only channel that reaches a codebase carrying [Sql] on
            // every parameter. Reachable only for such a parameter, because the bare one continued out
            // above. The .NET guess it kept is the one guess known to be wrong rather than merely
            // unverified - the procedure's real type is one the runtime cannot bind - so [Sql("@Rows")]
            // against "@Rows dbo.IdList READONLY" emits SqlTypeSpec("varchar") and fails at startup. A
            // warning and not SPG016's error, because the emitted contract is byte-for-byte what it was
            // before the script was promoted and an error would wall a consumer who compiles today.
            // Ungated for the same reason SPG010 is: this is not a report about the script's contents,
            // it is a statement that the contract and the procedure cannot both be right.
            if (matched && !fileTypeAvailable && draft.TypeName is null)
            {
                diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.ScriptTypeDeclinedForStatedParameter,
                    draft.Location,
                    Args(procDisplay, fileParam.Name, DescribeDeclinedType(fileParam), draft.CSharpName, model.MethodName, typeName)));
            }

            // A parameter that carries [Sql] compiles today, so a facet the script fills for it changes a
            // contract nobody edited. That is the feature working as designed and it is decided in the
            // script's favour, but it is never decided silently.
            if (draft.HasSqlAttribute && tookFactsFromFile)
            {
                ReportInferredFacets(
                    diagnostics, model, draft, procDisplay,
                    statedType, typeName, statedLength, length, precision, scale);
            }

            // SPG009's mirror, and the reason it cannot be folded into it: SPG009 compares counts for a
            // method that returns its OUTPUT parameters, while this is a method that has nowhere to put
            // one. Scoped to a direction the script supplied, because [Sql(Output = true)] on a Task<int>
            // is a pattern that compiles today - a caller who genuinely does not want the value.
            if (isOutput && !canReturnOutputs && !draft.HasSqlAttribute)
            {
                diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.InferredOutputHasNowhereToGo,
                    draft.Location,
                    Args(procDisplay, fileParam.Name, draft.CSharpName, model.MethodName)));
            }

            // What the SPG010 OUTPUT arm used to say, said correctly. The attribute states no direction,
            // so it has contradicted nothing and calling it a contradiction reported a sentence the
            // developer never wrote; but OUTPUT is deliberately never taken from the script for an
            // attributed parameter - that asymmetry is what makes "SPG009 cannot newly fire for a
            // consumer who compiles today" provable - so the contract binds an input where the procedure
            // declares an OUTPUT and the validator throws before the first call. The remedy is the one
            // thing the old message could not offer: Output = true plus somewhere to put the value.
            if (factsAvailable && draft.HasSqlAttribute && draft.Output is null && fileParam.IsOutput)
            {
                diagnostics.Add(new DiagnosticInfo(
                    Diagnostics.ScriptOutputNotTakenByAttribute,
                    draft.Location,
                    Args(procDisplay, fileParam.Name, draft.CSharpName, model.MethodName)));
            }

            if (isOutput)
            {
                outputCount++;
            }

            // mergedSpec was rendered above from the very two locals the three guards have just proved
            // non-null, each of which leaves the loop rather than falling through to here.
            spParams.Add(new SpParam(draft.CSharpName, mergedSpec!, isOutput));
        }

        // A SQL parameter no C# parameter carries is a contract the validator rejects at startup on
        // dbParams.Count != contractParams.Count - even when the SQL parameter has a DEFAULT, because
        // sys.parameters lists a defaulted parameter like any other. Warning, not error: the file is
        // evidence, and a stale copy must not break a working build.
        //
        // Gated on the script having moved something in this method, which is the whole difference
        // between a true statement and an upgrade wall. Ungated, it fires once per unbound script
        // parameter of every procedure - on a legacy corpus, once per DEFAULTed parameter - about
        // contracts that did not move when the files were promoted, and TreatWarningsAsErrors turns each
        // of those into a build failure. Once the script does move a parameter, the script is what the
        // method is being built from and a parameter missing from it is the next thing to fix.
        if (haveHeader && scriptFedTheMethod)
        {
            foreach (var fileParam in header.Parameters.AsArray())
            {
                if (!boundSqlNames.Contains(fileParam.Name))
                {
                    diagnostics.Add(new DiagnosticInfo(
                        Diagnostics.ScriptParameterNotBound,
                        model.MethodLocation,
                        Args(procDisplay, fileParam.Name, model.MethodName)));
                }
            }
        }

        // Why the script supplied nothing, asked only by a method that needed it to. One question, so one
        // answer: "two files declare this procedure and neither was believed" (SPG015) or "no file
        // declares it at all" (SPG014) - the latter being the Directory.Build.targets ordering trap and
        // the ProjectReference-without-the-Import trap. Once per method however many parameters went
        // unresolved, and nothing at all for a method that resolved: a duplicate or a missing script
        // costs a fully stated declaration nothing, and a 400-procedure project would drown in it.
        if (needsFacts && !haveHeader)
        {
            diagnostics.Add(ambiguous
                ? new DiagnosticInfo(
                    Diagnostics.DuplicateProcedureScript,
                    SqlLocation(duplicate.SecondPath, duplicate.SecondSpan),
                    Args(procDisplay, duplicate.FirstPath, duplicate.SecondPath))
                : new DiagnosticInfo(
                    Diagnostics.NoScriptForProcedure,
                    model.MethodLocation,
                    Args(procDisplay, model.MethodName)));
        }

        // OUTPUT parameters must line up (count + order) with the post-RETURN tuple elements. Deferred to
        // here because IsOutput can now be file-derived.
        if (model.Return == ReturnCategory.ReturnWithOutputs && outputCount != model.OutputConversions.Count)
        {
            diagnostics.Add(new DiagnosticInfo(
                Diagnostics.OutputArityMismatch,
                model.MethodLocation,
                Args(
                    model.MethodName,
                    model.OutputConversions.Count.ToString(CultureInfo.InvariantCulture),
                    outputCount.ToString(CultureInfo.InvariantCulture))));
        }

        // SpParams is attached only to a method that resolved. An unresolvable parameter contributes
        // nothing, so were the list attached regardless, the contract would be shorter than the
        // procedure's parameter list: clean at build, fatal at startup. Building the model empty first
        // and asking it whether it is valid makes that state unrepresentable rather than merely avoided,
        // and keeps one copy of the validity formula.
        var resolved = new ResolvedMethod(
            Namespace: model.Namespace,
            ClassName: model.ClassName,
            ClassAccessibility: model.ClassAccessibility,
            ClassIsPartial: model.ClassIsPartial,
            MethodName: model.MethodName,
            MethodAccessibility: model.MethodAccessibility,
            ReturnTypeText: model.ReturnTypeText,
            Return: model.Return,
            SignatureParams: model.SignatureParams,
            CancellationTokenParamName: model.CancellationTokenParamName,
            SpParams: default,
            Schema: model.Schema,
            ProcName: model.ProcName,
            ContractFieldName: model.ContractFieldName,
            ResultRowFqn: model.ResultRowFqn,
            ReturnsTuple: model.ReturnsTuple,
            OutputConversions: model.OutputConversions,
            Diagnostics: EquatableArray<DiagnosticInfo>.From(diagnostics));

        return resolved.IsValid
            ? resolved with { SpParams = EquatableArray<SpParam>.From(spParams) }
            : resolved;
    }

    /// <summary>Renders one <c>ProcParamSpec</c> initialiser.</summary>
    private static string RenderParamSpec(string sqlName, string sqlTypeName, int? length, byte? precision, byte? scale, bool isOutput)
    {
        var typeExpr = RenderSqlType(sqlTypeName, length, precision, scale);
        return $"new {ContractsNs}.ProcParamSpec(\"{sqlName}\", {typeExpr}{(isOutput ? ", true" : "")})";
    }

    /// <summary>Renders one <c>SqlTypeSpec</c> initialiser.</summary>
    /// <remarks>
    /// The branch precedence is load-bearing and unchanged: precision beats length, and the two are never
    /// emitted together. The guard is <c>precision &gt; 0</c> rather than <c>HasValue</c> so an explicit
    /// <c>[Sql(Precision = 0)]</c> still renders the bare form it renders today. With length modelled as
    /// <c>int?</c>, <c>null</c> is "unspecified" and <c>-1</c> is genuinely <c>MAX</c> - the two used to
    /// collide on the same sentinel, which is how <c>varchar(max)</c> was silently lost. A <c>-1</c> may
    /// therefore only reach here from a script; <see cref="Resolve"/> strips the attribute's legacy
    /// <c>-1</c> before the merge, so nothing rendered from a <c>[Sql]</c> alone can have moved.
    /// </remarks>
    internal static string RenderSqlType(string sqlTypeName, int? length, byte? precision, byte? scale)
    {
        if (precision.HasValue && precision.Value > 0)
        {
            var p = precision.Value.ToString(CultureInfo.InvariantCulture);
            var s = (scale ?? 0).ToString(CultureInfo.InvariantCulture);
            return $"new {ContractsNs}.SqlTypeSpec(\"{sqlTypeName}\", null, (byte){p}, (byte){s})";
        }

        if (length.HasValue)
        {
            return $"new {ContractsNs}.SqlTypeSpec(\"{sqlTypeName}\", {length.Value.ToString(CultureInfo.InvariantCulture)})";
        }

        return $"new {ContractsNs}.SqlTypeSpec(\"{sqlTypeName}\")";
    }

    /// <summary>The header parameter whose name equals <paramref name="sqlName"/>, ignoring case.</summary>
    private static bool TryMatchParameter(SqlProcHeader header, string sqlName, out SqlHeaderParam match)
    {
        foreach (var candidate in header.Parameters.AsArray())
        {
            if (string.Equals(candidate.Name, sqlName, StringComparison.OrdinalIgnoreCase))
            {
                match = candidate;
                return true;
            }
        }

        match = default;
        return false;
    }

    /// <summary>Reports one SPG010 per facet the attribute and the procedure both state and disagree on.</summary>
    /// <param name="diagnostics">The method's merged diagnostic list, appended to in place.</param>
    /// <param name="model">The declaring method, named in every message so the report reads on its own.</param>
    /// <param name="draft">
    /// The C# parameter under test. Its location is what the report is anchored to, because the
    /// attribute is the half of the disagreement the developer is being asked to look at.
    /// </param>
    /// <param name="statedLength">
    /// The attribute's length with the shipped generator's -1 sentinel already removed. Taking it from
    /// the draft here instead would report the legacy "unstated" spelling as a contradiction with every
    /// length the procedure declares.
    /// </param>
    /// <param name="fileParam">
    /// The matched procedure parameter. The caller has already established that it is
    /// <see cref="SqlParamShape.Ordinary"/>, the only shape carrying facets there is anything to
    /// disagree about.
    /// </param>
    /// <param name="procDisplay">The <c>schema.procedure</c> named as the other side of the disagreement.</param>
    private static void ReportContradictions(
        List<DiagnosticInfo> diagnostics,
        MethodModel model,
        ParamDraft draft,
        int? statedLength,
        SqlHeaderParam fileParam,
        string procDisplay)
    {
        void Report(string facet, string attributeValue, string procedureValue) =>
            diagnostics.Add(new DiagnosticInfo(
                Diagnostics.AttributeContradictsScript,
                draft.Location,
                Args(draft.CSharpName, model.MethodName, facet, attributeValue, procDisplay, procedureValue)));

        if (draft.TypeName is not null
            && fileParam.TypeName.Length > 0
            && !string.Equals(draft.TypeName.Trim(), fileParam.TypeName.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            Report("the SQL type", draft.TypeName, fileParam.TypeName);
        }

        if (statedLength.HasValue && fileParam.Length.HasValue && statedLength.Value != fileParam.Length.Value)
        {
            Report("length", FormatLength(statedLength.Value), FormatLength(fileParam.Length.Value));
        }

        if (draft.Precision.HasValue && fileParam.Precision.HasValue && draft.Precision.Value != fileParam.Precision.Value)
        {
            Report("precision", draft.Precision.Value.ToString(CultureInfo.InvariantCulture), fileParam.Precision.Value.ToString(CultureInfo.InvariantCulture));
        }

        if (draft.Scale.HasValue && fileParam.Scale.HasValue && draft.Scale.Value != fileParam.Scale.Value)
        {
            Report("scale", draft.Scale.Value.ToString(CultureInfo.InvariantCulture), fileParam.Scale.Value.ToString(CultureInfo.InvariantCulture));
        }

        // OUTPUT contradicts on the attribute's *stated* value, never on the false it defaults to. The
        // effective value is what the emitted call uses, but a developer who wrote no direction has
        // contradicted nothing, and reporting them for it was unanswerable: writing Output = false gave
        // the identical warning, and writing Output = true changed the contract - so the only way to
        // silence a diagnostic about a facet they had not written was to write the other value and hope.
        // The unstated case is a real problem all the same, and SPG020 is where it is now reported.
        if (draft.Output.HasValue && draft.Output.Value != fileParam.IsOutput)
        {
            Report("OUTPUT", FormatBool(draft.Output.Value), FormatBool(fileParam.IsOutput));
        }
    }

    /// <summary>Reports one SPG017 per facet the script filled in on a parameter that carries a [Sql].</summary>
    /// <remarks>
    /// Only a facet whose emitted value actually moved is reported, which is why the merged values are
    /// compared against what the declaration renders alone rather than against what the attribute states.
    /// <c>[Sql("@Sku", 32)]</c> takes its type from a <c>varchar(32)</c> parameter and lands on the same
    /// <c>varchar</c> the .NET type already implied: a fill, but not a change, and a consumer whose
    /// contract did not move has nothing to read about. The caller's gate says the same thing about the
    /// parameter as a whole, so a spec that did not move is never even offered here; the per-facet
    /// comparison is what decides which of the facets of a spec that did move are named.
    /// </remarks>
    private static void ReportInferredFacets(
        List<DiagnosticInfo> diagnostics,
        MethodModel model,
        ParamDraft draft,
        string procDisplay,
        string? statedType,
        string? mergedType,
        int? statedLength,
        int? mergedLength,
        byte? mergedPrecision,
        byte? mergedScale)
    {
        void Report(string facet, string before, string after) =>
            diagnostics.Add(new DiagnosticInfo(
                Diagnostics.ScriptFillsUnstatedFacet,
                draft.Location,
                Args(draft.CSharpName, model.MethodName, facet, procDisplay, before, after)));

        if (!string.Equals(statedType ?? "", mergedType ?? "", StringComparison.OrdinalIgnoreCase))
        {
            Report("SQL type", Quote(statedType), Quote(mergedType));
        }

        if (statedLength != mergedLength)
        {
            Report("length", QuoteLength(statedLength), QuoteLength(mergedLength));
        }

        if (draft.Precision != mergedPrecision)
        {
            Report("precision", QuoteByte(draft.Precision), QuoteByte(mergedPrecision));
        }

        if (draft.Scale != mergedScale)
        {
            Report("scale", QuoteByte(draft.Scale), QuoteByte(mergedScale));
        }
    }

    /// <summary>Names the construct a declined header parameter was written with, for SPG016.</summary>
    private static string DescribeDeclinedType(SqlHeaderParam fileParam) => fileParam.Shape switch
    {
        SqlParamShape.TableValued => "a table-valued (READONLY) parameter",
        SqlParamShape.Cursor => "a CURSOR parameter",
        SqlParamShape.UserDefined => "a user-defined or schema-qualified type",
        _ => "a type this generator does not model - sysname, sql_variant, timestamp and the spatial types among them",
    };

    /// <summary>
    /// Whether a SQL type the procedure declared can bind a parameter of the given .NET type.
    /// </summary>
    /// <remarks>
    /// Deliberately narrow. It rejects only pairs where a silent bind is data corruption or an outright
    /// failure - never a width mismatch between integers, which SqlClient converts freely, and never a
    /// type this generator does not classify, about which it knows nothing. The names are the parser's
    /// normalised, lower-case set, and the rules are <c>GetAllowedDotNetTypesForSqlType</c> intersected
    /// with what <c>ApplySqlType</c> can bind.
    /// </remarks>
    private static bool CanBind(string sqlTypeName, ClrKind clr)
    {
        if (clr == ClrKind.Other)
        {
            return true;
        }

        switch (sqlTypeName)
        {
            case "char":
            case "varchar":
            case "nchar":
            case "nvarchar":
            case "text":
            case "ntext":
            case "xml":
                return clr == ClrKind.String;

            case "binary":
            case "varbinary":
            case "image":
                return clr == ClrKind.ByteArray;

            case "uniqueidentifier":
                return clr == ClrKind.Guid;

            // The offset is discarded on the way out, and SqlDataReader hands 'time' back as a TimeSpan.
            case "datetimeoffset":
            case "time":
                return clr != ClrKind.String && clr != ClrKind.ByteArray && clr != ClrKind.DateTime;

            case "bit":
            case "tinyint":
            case "smallint":
            case "int":
            case "bigint":
            case "money":
            case "smallmoney":
            case "real":
            case "float":
            case "decimal":
            case "numeric":
            case "date":
            case "datetime":
            case "datetime2":
            case "smalldatetime":
                return clr != ClrKind.String && clr != ClrKind.ByteArray;

            default:
                return true;
        }
    }

    /// <summary>Rebuilds a reportable location inside a .sql from the integers the model carries.</summary>
    /// <remarks>
    /// The .sql branch cannot carry a <c>Location</c> - it compares by reference and would defeat the
    /// incremental cache - so the span rides as four integers and becomes an external-file location here,
    /// at report time.
    /// </remarks>
    private static Location? SqlLocation(string path, SourceSpanInfo span)
    {
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        var start = span.Start < 0 ? 0 : span.Start;
        var length = span.Length < 0 ? 0 : span.Length;
        var line = span.Line < 0 ? 0 : span.Line;
        var column = span.Column < 0 ? 0 : span.Column;

        var startPosition = new LinePosition(line, column);
        var endPosition = new LinePosition(line, column + length);
        return Location.Create(path, new TextSpan(start, length), new LinePositionSpan(startPosition, endPosition));
    }

    private static string FormatLength(int length) =>
        length == -1 ? "max" : length.ToString(CultureInfo.InvariantCulture);

    private static string FormatBool(bool value) => value ? "true" : "false";

    // "none" rather than an empty pair of quotes, because SPG017 reports a facet the declaration left
    // unstated as often as one it stated, and "changes from '' to '100'" reads as an empty string.
    private static string Quote(string? value) => value is null ? "none" : "'" + value + "'";

    private static string QuoteLength(int? length) => length.HasValue ? "'" + FormatLength(length.Value) + "'" : "none";

    private static string QuoteByte(byte? value) =>
        value.HasValue ? "'" + value.Value.ToString(CultureInfo.InvariantCulture) + "'" : "none";

    private static EquatableArray<string> Args(params string[] values) => new EquatableArray<string>(values);
}
