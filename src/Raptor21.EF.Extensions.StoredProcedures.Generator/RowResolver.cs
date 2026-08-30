using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator;

/// <summary>
/// Binds a <c>[SqlRow(Entity = typeof(X))]</c> row's members to the EF Core model and renders them.
/// </summary>
/// <remarks>
/// <para>
/// The counterpart of <see cref="ParameterResolver"/>, and the only place the row branch and the model
/// snapshot meet. It opens with an identity return for a row that states no entity, which is what makes
/// byte-identity structural rather than inspected: a hand-written positional record travels down exactly
/// the path it travels today, the same reference comes back out, and the model index cannot reach its
/// emitted text even in principle. That is one fact, not two - the same return keeps the row's
/// <c>Combine</c> cached whatever the snapshot does.
/// </para>
/// <para>
/// It is the only place a snapshot-derived diagnostic is reported. The reader and the index answer
/// questions and say nothing on their own, exactly as the .sql branch does, so a snapshot that does not
/// parse, names no entities or matches no row costs a consumer precisely zero. Every refusal here is
/// attached to the row type's own location, never to a span inside the snapshot: that file is rewritten
/// wholesale by <c>dotnet ef migrations add</c>, so a location in it would move on every model change.
/// </para>
/// <para>
/// The emitted member type is the model's <b>store</b> type rather than the class's, because a
/// result-set reader hands back the stored value and this generator cannot run a value converter - the
/// converter is an EF object and nothing here references EF. The class's own member is used for exactly
/// two things: to prove that the class and the model agree about the member's type (SPG032), and to
/// identify shadow properties, which are skipped silently. The two are then the same string by the time
/// anything is rendered, which is why one call to <c>BuildReadExpression</c> serves both paths.
/// </para>
/// </remarks>
internal static class RowResolver
{
    private const string ContractsNs = StoredProcedureGenerator.ContractsNs;

    /// <summary>Resolves one row against the compilation's model snapshots.</summary>
    /// <param name="row">The row as the syntax transform saw it.</param>
    /// <param name="index">Every parsed snapshot, indexed by EF metadata name.</param>
    /// <param name="ct">Cancellation for the pipeline step.</param>
    public static RowModel Resolve(RowModel row, EfModelIndex index, CancellationToken ct)
    {
        // The identity return. Reference-identical on purpose: see the class remarks.
        var binding = row.Binding;
        if (binding is null)
            return row;

        ct.ThrowIfCancellationRequested();

        // The row's own diagnostics are carried forward, never re-reported: they live in the merged list
        // only, so nothing fires twice. In practice this list is empty here, because a row that failed
        // SPG021 or SPG022 carries no binding at all and took the identity return above.
        var diagnostics = new List<DiagnosticInfo>(row.Diagnostics.AsArray());

        // SPG027 names the type the way the developer spelled it, because nothing was looked up and the
        // join key is not the subject; SPG028 and SPG029 name the metadata name, because that IS the
        // subject - it is the string that failed to match, and the one a developer would grep the
        // snapshot for. A nested type is 'Outer+Nested' there and 'Outer.Nested' in the source.
        var lookup = index.Lookup(binding.EntityMetadataName);
        switch (lookup.Status)
        {
            case EfLookupStatus.NoSnapshot:
                return Refuse(row, diagnostics, Diagnostics.EntityNoModelSnapshot,
                    row.TypeName, binding.EntityDisplayName);

            case EfLookupStatus.NotFound:
                return Refuse(row, diagnostics, Diagnostics.EntityNotInModel,
                    row.TypeName, binding.EntityMetadataName);

            case EfLookupStatus.Ambiguous:
                return Refuse(row, diagnostics, Diagnostics.EntityDescribedByTwoSnapshots,
                    row.TypeName, binding.EntityMetadataName, lookup.SnapshotA ?? "?", lookup.SnapshotB ?? "?");
        }

        var facts = lookup.Entity;
        if (facts is null)
        {
            // Unreachable: Found always carries the entity. Refused rather than assumed, because the
            // alternative is a row with no members and no explanation.
            return Refuse(row, diagnostics, Diagnostics.EntityNotInModel,
                row.TypeName, binding.EntityMetadataName);
        }

        // Item 1, in one predicate: an entity type is bindable only if its columns are exclusively its
        // own. The index decided this over the whole model - both "does anything derive from this" and
        // "is anything else mapped here" are questions a per-declaration parse cannot answer at all - so
        // there is nothing to recompute and no order to get wrong.
        switch (facts.Bindability)
        {
            case EfBindability.Bindable:
                break;

            case EfBindability.SharedTarget:
                return Refuse(row, diagnostics, Diagnostics.EntitySharesItsMappingTarget,
                    facts.Name, facts.Target.Display, facts.Blocker ?? "another entity type", row.TypeName);

            case EfBindability.InHierarchyDerived:
                return Refuse(row, diagnostics, Diagnostics.EntityInInheritanceHierarchy,
                    facts.Name,
                    facts.Blocker is null ? "is a derived type" : "is a derived type of '" + facts.Blocker + "'",
                    row.TypeName);

            case EfBindability.InHierarchyBase:
                return Refuse(row, diagnostics, Diagnostics.EntityInInheritanceHierarchy,
                    facts.Name,
                    facts.Blocker is null ? "is a base type" : "is the base type of '" + facts.Blocker + "'",
                    row.TypeName);

            case EfBindability.Owned:
                return Refuse(row, diagnostics, Diagnostics.EntityIsOwned,
                    facts.Name, facts.Blocker ?? "another entity type", SimpleName(facts.Name), row.TypeName);

            default:
                return Refuse(row, diagnostics, Diagnostics.EntityMappingNotBindable,
                    facts.Name, facts.BlockerDetail ?? "a construct this reader does not bind", row.TypeName);
        }

        var candidates = new Dictionary<string, EfMemberCandidate>(StringComparer.Ordinal);
        foreach (var member in binding.Members.AsArray())
        {
            if (member is not null && !candidates.ContainsKey(member.Name))
                candidates.Add(member.Name, member);
        }

        // One enumeration produces the ColumnSpec[] and the constructor parameter list together, so a
        // column list shorter than the row's arity - CS7036 in generated code at best, and "clean at
        // build, fatal at startup" on the validator's count check at worst - cannot be represented. That
        // is also why every failure below is a refusal and never a skip: the only thing allowed to
        // shorten the list is a shadow property, which shortens both halves of it at once.
        var columns = new List<RowColumn>();
        var parameters = new List<string>();
        var refused = false;

        // Snapshot order, which is EF's: primary key first, then alphabetical. It is the developer's
        // contract with their own SELECT, because StoredProcedureValidator compares result columns
        // positionally while the generated reader looks them up by name - so a SELECT in another order
        // fails with a column-name mismatch rather than with a wrong value. Documented at the attribute;
        // the class's declaration order was the alternative and was rejected, because reordering members
        // is a no-op refactor everywhere else in C# and must not silently move a validated contract.
        foreach (var property in facts.Properties.AsArray())
        {
            ct.ThrowIfCancellationRequested();

            if (property is null)
                continue;

            // No member of that name: a shadow property, a shadow foreign key, or a TPH discriminator.
            // The class is the authority on which members exist and the model is the authority on the
            // column each one maps to, so this is skipped in silence. (Under the hierarchy clauses the
            // discriminator case is unreachable anyway; what is left is a shadow property the developer
            // declared deliberately.)
            if (!candidates.TryGetValue(property.Name, out var candidate))
                continue;

            var storeType = property.StoreClrType;

            // The two arms of SPG030 that have to be answered before the model and the class can be
            // compared at all: an unresolved Property<T> argument leaves nothing to compare it with, and
            // a primitive collection is refused whatever the class says, because no getter materialises a
            // whole collection out of one column.
            if (storeType is null || property.IsPrimitiveCollection)
            {
                diagnostics.Add(Diag(Diagnostics.EntityPropertyHasNoReader, row.TypeLocation,
                    property.Name, facts.Name, storeType ?? "an unresolved type", row.TypeName,
                    NoReaderDetail(property)));
                refused = true;
                continue;
            }

            var memberType = candidate.ElementTypeText + (candidate.IsNullableValue ? "?" : "");
            var modelType = storeType + (property.StoreTypeIsNullable ? "?" : "");

            // The model against the class, and it comes FIRST for a reason worth stating: every check
            // after it is about the STORE type - SPG030 says the stored type has no reader, SPG031 says
            // the column type does not accept it - and the only handle this method has on a type is the
            // class member's classification. Proving the two equal here is what makes that handle the
            // store type as well, so no check below can be ambiguous about which of the two it tested.
            //
            // Ordering it after them would also make this diagnostic unreachable for the case it exists
            // to name: a value-converted or unconverted enum property, where the model says 'string' and
            // the class says 'OrderStatus', would be reported as "no IDataRecord reader for string" -
            // which is not true of string - or as a column-type mismatch, instead of as the converter it
            // actually is.
            //
            // Reference-type nullable annotations are NOT compared: the row file is #nullable disable,
            // every reference read is IsDBNull-guarded, and the validator exempts reference types from its
            // nullability check, so the annotation reaches no emitted byte. A value type's nullability is
            // compared, because it reaches every one.
            if (!string.Equals(storeType, candidate.ElementTypeText, StringComparison.Ordinal)
                || (!candidate.IsReferenceType && property.StoreTypeIsNullable != candidate.IsNullableValue))
            {
                diagnostics.Add(Diag(Diagnostics.EntityStoreTypeDiffersFromMember, row.TypeLocation,
                    property.Name, facts.Name, modelType, memberType, row.TypeName));
                refused = true;
                continue;
            }

            // From here the model's type and the class's are one type, so this reads as "the stored type
            // has no IDataRecord reader", which is what SPG030 says: 'time' binds TimeSpan and
            // 'datetimeoffset' binds DateTimeOffset, both of which the validator accepts and neither of
            // which has a getter here.
            if (candidate.Getter is null && !candidate.IsByteArray)
            {
                diagnostics.Add(Diag(Diagnostics.EntityPropertyHasNoReader, row.TypeLocation,
                    property.Name, facts.Name, storeType, row.TypeName, NoReaderDetail(property)));
                refused = true;
                continue;
            }

            // EF synthesises HasColumnType for every property it maps to a column of its own, so its
            // absence means the property is mapped into a JSON document instead.
            if (property.ColumnType is null)
            {
                diagnostics.Add(Diag(Diagnostics.EntityColumnTypeNotAccepted, row.TypeLocation,
                    property.Name, facts.Name, "(none)", modelType, row.TypeName,
                    "The snapshot records no column type for it at all, which is how EF writes a property mapped into a JSON document rather than to a column of its own."));
                refused = true;
                continue;
            }

            // The stored type against the column type - the pair the validator will compare at startup,
            // asked here instead. Nullability plays no part: the validator's table admits T and T? alike,
            // so the entries below are element types and 'int' answers for 'int?'. Whether the column
            // really is nullable is the separate question the value-type arm above settles.
            var sqlBaseType = SqlBaseTypeName(property.ColumnType);
            var allowed = AllowedElementTypesForSqlType(sqlBaseType);
            if (allowed.Length == 0 || Array.IndexOf(allowed, storeType) < 0)
            {
                diagnostics.Add(Diag(Diagnostics.EntityColumnTypeNotAccepted, row.TypeLocation,
                    property.Name, facts.Name, property.ColumnType, modelType, row.TypeName,
                    allowed.Length == 0
                        ? "'" + sqlBaseType + "' is not a SQL type the validator maps to any .NET type at all."
                        : "Allowed for '" + sqlBaseType + "': " + string.Join(", ", allowed) + "."));
                refused = true;
                continue;
            }

            var columnName = property.ColumnName ?? property.Name;
            var read = StoredProcedureGenerator.BuildReadExpression(candidate, columnName, out var specTypeText);
            if (read is null)
            {
                // Unreachable while the getter check above is the same predicate this one is. Kept as a
                // refusal rather than a skip so that a change to either one cannot silently shorten the
                // column list without shortening the constructor with it.
                diagnostics.Add(Diag(Diagnostics.EntityPropertyHasNoReader, row.TypeLocation,
                    property.Name, facts.Name, storeType, row.TypeName, NoReaderDetail(property)));
                refused = true;
                continue;
            }

            columns.Add(new RowColumn(
                columnName,
                $"new {ContractsNs}.ColumnSpec(\"{columnName}\", typeof({specTypeText}))",
                read));
            parameters.Add(specTypeText + " " + property.Name);
        }

        if (refused)
            return Refused(row, diagnostics);

        // A binding that matched an entity and still produced nothing is a refusal, not an empty row:
        // see SPG033 for why an empty ColumnSpec[] is invisible to both the build and the validator.
        if (parameters.Count == 0)
            return Refuse(row, diagnostics, Diagnostics.EntityBindsNoColumns, row.TypeName, facts.Name);

        return new RowModel(
            row.Namespace,
            row.TypeName,
            row.Accessibility,
            true,
            row.Fqn,
            EquatableArray<RowColumn>.From(columns),
            RenderParameterList(row.Namespace, parameters),
            row.Binding,
            row.TypeLocation,
            EquatableArray<DiagnosticInfo>.From(diagnostics));
    }

    // The nullability rule, written down once because a later feature will need it and would otherwise
    // invent one. It is the exact inverse of what CSharpSnapshotGenerator emits, which calls IsRequired()
    // when `property.IsNullable != (clrType.IsNullableType() && !property.IsPrimaryKey())`:
    //
    //   * VALUE TYPE - the column is nullable if and only if the Property<T> argument carries '?'.
    //     IsRequired() is irrelevant here and is not consulted. This is the half that reaches the emitted
    //     bytes, through EfMemberCandidate.IsNullableValue, which SPG032 has already proved equal to it.
    //   * REFERENCE TYPE - the column is NOT NULL if and only if IsRequired() is present OR the property
    //     is named in this entity's HasKey(...) list. The HasKey clause is not decoration: a string
    //     primary key is emitted with NO IsRequired() and its column is NOT NULL, because the emitter's
    //     condition is `false != (true && false)` and the call is skipped. A reader that omits it
    //     mis-types every string-keyed entity.
    //
    // The reference-type half changes no emitted byte today - every reference read is IsDBNull-guarded
    // and the validator exempts reference types - so it is recorded here rather than computed, and
    // EfEntityFacts carries both signals (IsRequiredCalled and KeyPropertyNames) for whoever needs it.
    //
    // What no model can answer, and what is therefore the developer's to keep along with the column
    // order: sys.dm_exec_describe_first_result_set reports the nullability of the EXPRESSION, not of the
    // column. SELECT ISNULL(Price, 0), an outer join or a UNION can make a NOT NULL column nullable in
    // the result set.

    /// <summary>The refusal shape: no columns, no parameter list, and the reason attached to the row.</summary>
    private static RowModel Refuse(
        RowModel row, List<DiagnosticInfo> diagnostics, DiagnosticDescriptor descriptor, params string[] args)
    {
        diagnostics.Add(new DiagnosticInfo(descriptor, row.TypeLocation, new EquatableArray<string>(args)));
        return Refused(row, diagnostics);
    }

    private static RowModel Refused(RowModel row, List<DiagnosticInfo> diagnostics) =>
        new RowModel(
            row.Namespace,
            row.TypeName,
            row.Accessibility,
            row.IsPartialRecord,
            row.Fqn,
            EquatableArray<RowColumn>.From(new List<RowColumn>()),
            "",
            row.Binding,
            row.TypeLocation,
            EquatableArray<DiagnosticInfo>.From(diagnostics));

    private static DiagnosticInfo Diag(DiagnosticDescriptor descriptor, Location? location, params string[] args) =>
        new DiagnosticInfo(descriptor, location, new EquatableArray<string>(args));

    /// <summary>Which arm of SPG030 fired, as the sentence the message drops in.</summary>
    private static string NoReaderDetail(EfProperty property)
    {
        if (property.StoreClrType is null)
        {
            return "The snapshot's Property<T> type argument did not resolve to a type in this compilation, and nothing is guessed from the text it was spelled with.";
        }

        if (property.IsPrimitiveCollection)
        {
            return "PrimitiveCollection<T> maps a whole collection into one column, which no IDataRecord getter materialises.";
        }

        return "StoredProcedureValidator accepts that .NET type for its SQL type, but this generator emits no reader for it - 'time' binds TimeSpan and 'datetimeoffset' binds DateTimeOffset, and neither has an IDataRecord getter here.";
    }

    /// <summary>The primary constructor <c>EmitRow</c> writes after the row type's name.</summary>
    /// <remarks>
    /// Rendered here rather than in the emitter because the emitter's one change is to interpolate this
    /// string, which is what keeps the positional path byte-identical: an empty string there produces the
    /// identical token sequence. The indentation is <c>EmitRow</c>'s own - one level in when the row has a
    /// namespace - and the coupling is named here because there is no third place to put it.
    /// </remarks>
    private static string RenderParameterList(string ns, List<string> parameters)
    {
        if (parameters.Count == 0)
        {
            // Every model property was a shadow property. A record with an empty parameter list is legal
            // C# and the emitted reader constructs it, so this stays representable rather than becoming a
            // special case that emits a declaration with no closing paren.
            return "()";
        }

        var indent = string.IsNullOrEmpty(ns) ? "    " : "        ";
        var sb = new StringBuilder();
        sb.Append('(');
        for (var i = 0; i < parameters.Count; i++)
        {
            // AppendLine rather than a literal, so the fragment carries the same line ending every other
            // line the emitter writes carries.
            sb.AppendLine();
            sb.Append(indent).Append(parameters[i]);
            if (i < parameters.Count - 1)
                sb.Append(',');
        }

        sb.Append(')');
        return sb.ToString();
    }

    /// <summary>The base name of a column type, exactly as the validator reads one.</summary>
    /// <remarks>
    /// <c>ValidateSqlToDotNetType</c> does <c>systemTypeName.Split('(')[0].Trim().ToLowerInvariant()</c>
    /// and this must do the same thing to the same string, including the invariant lowering: agreeing
    /// with that method is the entire point of the check it feeds.
    /// </remarks>
    private static string SqlBaseTypeName(string columnType)
    {
        var paren = columnType.IndexOf('(');
        var name = paren < 0 ? columnType : columnType.Substring(0, paren);
        return name.Trim().ToLowerInvariant();
    }

    /// <summary>The last segment of an EF metadata name, for a message that wants the type's own name.</summary>
    private static string SimpleName(string metadataName)
    {
        var cut = metadataName.LastIndexOfAny(new[] { '.', '+' });
        return cut < 0 ? metadataName : metadataName.Substring(cut + 1);
    }

    /// <summary>
    /// The generator's copy of <c>StoredProcedureValidator.GetAllowedDotNetTypesForSqlType</c>, keyed by
    /// the same SQL base names and rendered in the generator's own element-type spelling.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A real duplication, and it exists because this project is netstandard2.0 - that is where Roslyn
    /// loads analyzers - and cannot reference the net8.0/net10.0 runtime library that owns the original.
    /// The two are pinned to each other by
    /// <c>EntityBoundRowTests.GeneratorsCopyOfTheValidatorTypeTableAgreesWithTheRuntimes</c>, which
    /// reflects over the runtime's table and compares it set for set. Change one of the two and change
    /// the other, or that test fails; miss both and the failure moves to a startup
    /// <c>InvalidOperationException</c>, which is the failure this whole binding exists to eliminate.
    /// </para>
    /// <para>
    /// The runtime's table lists <c>T</c> and <c>T?</c> separately because it holds <c>Type</c> objects;
    /// here the element type is enough, because a member's nullability is carried beside it on
    /// <see cref="EfMemberCandidate.IsNullableValue"/> and is checked against the model rather than
    /// against this table. So each entry below collapses the runtime's pair into its element - the
    /// mapping the pinning test applies before it compares.
    /// </para>
    /// <para>
    /// This table alone is not sufficient and neither is the reader's getter table: <c>time</c> and
    /// <c>datetimeoffset</c> are here and have no getter (SPG030), while a <c>Property&lt;int&gt;</c> on a
    /// <c>varchar(32)</c> column has a getter and is not here (SPG031). Both have to be intersected.
    /// </para>
    /// </remarks>
    internal static string[] AllowedElementTypesForSqlType(string sqlBaseType)
    {
        switch (sqlBaseType)
        {
            case "tinyint": return new[] { "byte" };
            case "smallint": return new[] { "short" };
            case "int": return new[] { "int" };
            case "bigint": return new[] { "long" };
            case "bit": return new[] { "bool" };
            case "varchar":
            case "nvarchar":
            case "char":
            case "nchar":
            case "text":
            case "ntext": return new[] { "string" };
            case "binary":
            case "varbinary":
            case "image": return new[] { "byte[]" };
            case "datetime":
            case "datetime2":
            case "smalldatetime":
            case "date": return new[] { "global::System.DateTime" };
            case "time": return new[] { "global::System.TimeSpan" };
            case "datetimeoffset": return new[] { "global::System.DateTimeOffset" };
            case "uniqueidentifier": return new[] { "global::System.Guid" };
            case "decimal":
            case "numeric": return new[] { "decimal" };
            case "real": return new[] { "float" };
            case "float": return new[] { "double" };
            case "money":
            case "smallmoney": return new[] { "decimal" };
            default: return Array.Empty<string>();
        }
    }
}
