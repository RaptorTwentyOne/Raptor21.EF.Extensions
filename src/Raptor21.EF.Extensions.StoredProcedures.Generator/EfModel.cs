using System;
using System.Collections.Generic;
using System.Collections.Immutable;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator;

// The value-equatable models the EF ModelSnapshot branch of the pipeline carries. Everything here holds
// strings, bools, enums and other value-equatable models - never an AdditionalText, SourceText,
// Compilation, SemanticModel, ISymbol, SyntaxNode or Location.
//
// Stricter than Model.cs, which does carry Locations. A snapshot is regenerated wholesale by
// `dotnet ef migrations add`, so every span in it moves on any model change; a Location here would make
// the index compare unequal on an edit that changed nothing the reader reads, and this library writes
// every procedure script into that same file as an "Sp:" annotation. Every snapshot-derived diagnostic
// is attached to the ROW TYPE's own location during resolution instead - the discipline the .sql branch
// already keeps.
//
// This file is the snapshot branch's single home, the way SqlHeaderModel.cs is the .sql branch's: the
// pipeline-side EfModelIndex sits at the bottom. Declare anything the branch carries here.

/// <summary>Where a snapshot said an entity type's rows live.</summary>
internal enum EfMappingKind
{
    /// <summary>
    /// The block stated no mapping call at all. Never a legitimate state for a non-owned, non-derived
    /// entity - EF emits <c>ToTable</c> unconditionally for a root - so it means the reader failed to
    /// understand the block and the entity is refused.
    /// </summary>
    None,

    /// <summary>A table named by <c>ToTable("X")</c>, at any arity.</summary>
    Table,

    /// <summary>
    /// A view, SQL query or table-valued function named by <c>ToView</c>, <c>ToSqlQuery</c> or
    /// <c>ToFunction</c>. Counted for sharing exactly as a table is: two entity types on one view carry
    /// the same nullability hazard as two on one table.
    /// </summary>
    View,

    /// <summary>
    /// <c>ToTable((string)null)</c> with no view beside it - the entity said, explicitly, that it owns
    /// no table. A TPC hierarchy root and a keyless type both land here.
    /// </summary>
    NonMapped,

    /// <summary>
    /// A mapping call whose leading argument is neither a string literal nor <c>(string)null</c>. One
    /// of these poisons the whole file: an unparsed mapping must reduce confidence, never leave a
    /// DIFFERENT entity looking like a clean sole occupant, and a per-entity quarantine cannot do that
    /// because the unreadable entity might be the one sharing the table.
    /// </summary>
    Unreadable,
}

/// <summary>The table or view an entity type's columns come from.</summary>
/// <remarks>
/// On a raw block (see <see cref="EfEntityFacts"/>) <see cref="Schema"/> is what the file spelled, or
/// null when it spelled none. On a resolved entity it has been folded to
/// <c>explicit ?? HasDefaultSchema ?? "dbo"</c> and is never null for a <see cref="EfMappingKind.Table"/>
/// or <see cref="EfMappingKind.View"/>.
/// </remarks>
internal readonly record struct EfMappingTarget(EfMappingKind Kind, string? Schema, string? Name)
{
    /// <summary>The absence of a mapping call.</summary>
    public static EfMappingTarget None => default;

    /// <summary>Which of two mapping statements about one entity is the entity's answer.</summary>
    /// <remarks>
    /// An unreadable argument outranks everything, so a file cannot be quietly rescued by a second,
    /// readable call. A named table outranks a view, which is what keeps table splitting visible when one
    /// of the two entities sharing the table also declares a view of its own. A stated "no table"
    /// outranks silence, which is how <c>ToTable((string)null)</c> followed by a <c>ToView</c> becomes one
    /// View target.
    /// <para>
    /// One function, used by the reader when it folds a block's own calls together and by the index when
    /// it folds an entity's blocks together, so the two cannot drift into disagreeing about which
    /// statement won.
    /// </para>
    /// </remarks>
    internal static int Rank(EfMappingKind kind) => kind switch
    {
        EfMappingKind.Unreadable => 4,
        EfMappingKind.Table => 3,
        EfMappingKind.View => 2,
        EfMappingKind.NonMapped => 1,
        _ => 0,
    };

    /// <summary>The target as a diagnostic message spells it, <c>schema.name</c>; empty when there is none.</summary>
    public string Display => Kind switch
    {
        EfMappingKind.Table or EfMappingKind.View =>
            string.IsNullOrEmpty(Schema) ? Name ?? "" : Schema + "." + Name,
        _ => "",
    };
}

/// <summary>One <c>Property&lt;T&gt;</c> or <c>PrimitiveCollection&lt;T&gt;</c> as the snapshot declares it.</summary>
/// <remarks>
/// Facts only - the reader decides nothing. In particular nullability is recorded as the two signals the
/// snapshot actually carries (<see cref="StoreTypeIsNullable"/> for a value type,
/// <see cref="IsRequiredCalled"/> for a reference type) rather than as one merged answer, because the
/// rule that combines them is the inverse of the emitter's and belongs where the row is rendered.
/// </remarks>
/// <param name="Name">The property name, verbatim from the string literal.</param>
/// <param name="StoreClrType">
/// The generic argument with <c>Nullable&lt;T&gt;</c> unwrapped, rendered exactly as the row emitter
/// renders a member type - <c>int</c>, <c>string</c>, <c>decimal</c>, <c>global::System.DateTime</c>.
/// Null when the type argument did not resolve; the property is refused rather than guessed from syntax.
/// </param>
/// <param name="StoreTypeIsNullable">
/// The generic argument was <c>T?</c> for a value type <c>T</c>. False for every reference type: the
/// snapshot declares <c>#nullable disable</c>, so a reference annotation in it means nothing.
/// </param>
/// <param name="IsRequiredCalled">The chain called <c>IsRequired()</c>.</param>
/// <param name="IsPrimitiveCollection">The declaration was <c>PrimitiveCollection&lt;T&gt;</c>.</param>
/// <param name="ColumnName">The <c>HasColumnName</c> argument, else null (the column is the property name).</param>
/// <param name="ColumnType">
/// The <c>HasColumnType</c> argument, else null. EF synthesises <c>HasColumnType</c> for every property
/// it maps to a column, so null here means the property is mapped to JSON rather than to a column.
/// </param>
internal sealed record EfProperty(
    string Name,
    string? StoreClrType,
    bool StoreTypeIsNullable,
    bool IsRequiredCalled,
    bool IsPrimitiveCollection,
    string? ColumnName,
    string? ColumnType);

/// <summary>Whether an entity type's columns are exclusively its own, and if not, why not.</summary>
/// <remarks>
/// The rule is one sentence: <b>an entity type is bindable only if its columns are exclusively its
/// own.</b> Stated over the entity rather than over the table, which is what lets a keyless type with no
/// table at all bind - a type that owns nothing shares nothing - and what closes the TPT hole where each
/// type's own table legitimately holds exactly one entity.
/// <para>
/// EF's own <c>RelationalPropertyExtensions.IsColumnNullable</c> names exactly two causes of a divergence
/// between what the snapshot records per property and what the column really is: derived types in a TPH
/// hierarchy, and properties on optional types sharing a table. <see cref="InHierarchyDerived"/>,
/// <see cref="InHierarchyBase"/>, <see cref="Owned"/> and <see cref="SharedTarget"/> refuse both.
/// </para>
/// </remarks>
internal enum EfBindability
{
    /// <summary>Nothing else can name a column of this entity type.</summary>
    Bindable,

    /// <summary>The block called <c>HasBaseType</c> - TPH, TPT or TPC alike.</summary>
    InHierarchyDerived,

    /// <summary>Another block named this one in <c>HasBaseType</c>.</summary>
    InHierarchyBase,

    /// <summary>The block was reached inside an <c>OwnsOne</c>/<c>OwnsMany</c> lambda.</summary>
    Owned,

    /// <summary>Another entity type resolves to the same table or view.</summary>
    SharedTarget,

    /// <summary>The block called <c>SplitToTable</c> or <c>SplitToView</c>.</summary>
    SplitMapping,

    /// <summary>The block declared a <c>ComplexProperty</c> or <c>ComplexCollection</c>.</summary>
    ComplexMember,

    /// <summary>The file carries an unreadable mapping argument, or this entity states no mapping at all.</summary>
    UnreadableMapping,
}

/// <summary>What one <c>Entity(...)</c> block said, or what one entity type resolves to.</summary>
/// <remarks>
/// <para>The same record is used at both stages of the branch, and the two readings differ:</para>
/// <list type="bullet">
/// <item><description>
/// <b>As a raw block</b> - the shape <see cref="EfSnapshotReader"/> produces, one entry per
/// <c>Entity(...)</c>/<c>OwnsOne(...)</c> lambda in the file. <see cref="Bindability"/> carries only the
/// clauses a single block can decide on its own (<see cref="EfBindability.InHierarchyDerived"/>,
/// <see cref="EfBindability.Owned"/>, <see cref="EfBindability.ComplexMember"/>,
/// <see cref="EfBindability.SplitMapping"/>), <see cref="Target"/> is the block's own mapping call with
/// the schema unfolded, and <see cref="Blocker"/> is the base type name or the owner name according to
/// which of those two clauses fired. Several entries may name the same entity: an entity's properties,
/// its owned types and its relationships are emitted in separate blocks.
/// </description></item>
/// <item><description>
/// <b>As a resolved entity</b> - what <see cref="EfModelIndex.Lookup"/> answers with, one entry per
/// entity name, after the five passes. Every field then means what its own documentation says, the
/// whole-model clauses (<see cref="EfBindability.InHierarchyBase"/>,
/// <see cref="EfBindability.SharedTarget"/>, <see cref="EfBindability.UnreadableMapping"/>) are decided,
/// and <see cref="Target"/> is resolved through inheritance and ownership with the model's default
/// schema folded in.
/// </description></item>
/// </list>
/// <para>
/// <see cref="Blocker"/> and <see cref="BlockerDetail"/> are meaningful only for the
/// <see cref="Bindability"/> that filled them. That is the one place this model is denser than it looks,
/// and it is deliberate: a field per clause would widen every equality comparison in the incremental
/// cache for facts that exactly one refusal ever reads.
/// </para>
/// </remarks>
/// <param name="Name">The EF metadata name, verbatim from the string literal - <c>Ns.Outer+Nested</c>.</param>
/// <param name="Bindability">Whether the entity's columns are exclusively its own.</param>
/// <param name="Target">The table or view the columns come from.</param>
/// <param name="Blocker">
/// The other entity that explains the refusal: the base type, a derived type, the owner, or another
/// entity mapped to the same target. Null when <see cref="Bindability"/> names no other entity.
/// </param>
/// <param name="BlockerDetail">
/// The construct that explains the refusal - <c>"SplitToTable"</c>, <c>"ComplexProperty"</c>, or the
/// phrase naming which arm of the unreadable clause fired. Null otherwise.
/// </param>
/// <param name="IsKeyless">
/// No <c>HasKey</c> and no <c>HasBaseType</c>. <c>HasNoKey</c> is never written into a snapshot, so
/// keylessness is an absence rather than a call. Not itself a bindability question.
/// </param>
/// <param name="KeyPropertyNames">The <c>HasKey</c> arguments, in order.</param>
/// <param name="Properties">The properties in snapshot order - primary key first, then alphabetical.</param>
internal sealed record EfEntityFacts(
    string Name,
    EfBindability Bindability,
    EfMappingTarget Target,
    string? Blocker,
    string? BlockerDetail,
    bool IsKeyless,
    EquatableArray<string> KeyPropertyNames,
    EquatableArray<EfProperty> Properties);

/// <summary>One EF Core <c>ModelSnapshot</c> class, read as ordinary C# syntax.</summary>
/// <remarks>
/// The <c>GeneratorAttributeSyntaxContext</c> - its <c>SemanticModel</c> included - is consumed and
/// discarded inside the transform that produces this; only this model survives it. That is what makes an
/// <c>Sp:</c> annotation change a no-op: the reader skips <c>HasAnnotation</c>, so the model compares
/// equal and every step after it is served from the cache. Which matters here more than anywhere else,
/// because this library writes every one of its procedure scripts into that same file as an annotation.
/// </remarks>
/// <param name="FilePath">The snapshot's syntax tree path. Diagnostic text only.</param>
/// <param name="SnapshotTypeName">The namespace-qualified snapshot class name, for SPG029's message.</param>
/// <param name="DbContextSimpleName">
/// The <c>[DbContext(typeof(X))]</c> argument's simple name. Carried for diagnostic text and nothing
/// else - the entity join never uses it, because one CLR type can be mapped by two contexts and
/// <c>Entity = typeof(X)</c> names neither.
/// </param>
/// <param name="DefaultSchema">The <c>HasDefaultSchema</c> argument, else null.</param>
/// <param name="Entities">One entry per block, in the order the file declares them.</param>
internal sealed record EfModelFile(
    string FilePath,
    string SnapshotTypeName,
    string? DbContextSimpleName,
    string? DefaultSchema,
    EquatableArray<EfEntityFacts> Entities);

/// <summary>Why a lookup did or did not produce an entity type.</summary>
internal enum EfLookupStatus
{
    /// <summary>
    /// The compilation contains no model snapshot at all - the state of every consumer who has not run
    /// <c>dotnet ef migrations add</c>, who excludes <c>Migrations/**</c> from compilation, whose
    /// migrations live in another project, or who does not reference EF. Never reportable on its own.
    /// </summary>
    NoSnapshot,

    /// <summary>Snapshots exist; none of them names this entity.</summary>
    NotFound,

    /// <summary>Two snapshots name it and describe it differently.</summary>
    Ambiguous,

    /// <summary>Exactly one description of the entity type was found.</summary>
    Found,
}

/// <summary>The answer to one entity-name lookup.</summary>
/// <param name="Status">Why the lookup did or did not produce an entity.</param>
/// <param name="Entity">The resolved facts; non-null only when <see cref="Status"/> is <see cref="EfLookupStatus.Found"/>.</param>
/// <param name="SnapshotA">The snapshot that answered, or the first of the two that disagree.</param>
/// <param name="SnapshotB">The second of the two snapshots that disagree; null otherwise.</param>
internal readonly record struct EfLookup(
    EfLookupStatus Status,
    EfEntityFacts? Entity,
    string? SnapshotA,
    string? SnapshotB);

/// <summary>Every snapshot in the compilation, indexed by EF metadata name.</summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="SqlHeaderIndex"/>: the dictionaries are built once in the constructor because a
/// resolver that scanned the entity list per declaration would be O(n*m), they are a cache and never the
/// equality basis, and <see cref="Empty"/> is the state of every consumer today.
/// </para>
/// <para>
/// The five per-file passes run here rather than in the reader, and that is the point of the class: both
/// "is anything else mapped to this table" and "does anything derive from this type" are whole-model
/// questions that a per-declaration parse cannot answer at all, so answering them lazily is not slow, it
/// is wrong. Resolving mapping targets through <c>HasBaseType</c> BEFORE the sharing count is taken is
/// what closes the biggest fail-open: a TPH derived type emits no <c>ToTable</c>, so counting literal
/// mapping calls would read EF's default inheritance strategy as one entity per table.
/// </para>
/// <para>
/// The index answers questions and reports nothing, exactly as <see cref="SqlHeaderIndex"/> does. A
/// snapshot that does not parse, names no entities or matches no row costs a consumer precisely zero;
/// every snapshot-derived diagnostic is raised during row resolution, against the row type's own
/// location.
/// </para>
/// </remarks>
internal sealed class EfModelIndex : IEquatable<EfModelIndex>
{
    /// <summary>
    /// The schema an entity falls back to when neither it nor the model states one. A SQL-Server-only
    /// assumption that this provider-blind generator cannot verify: it is sound because this library is
    /// SQL Server only, and it is written down rather than left implicit because it stops being true the
    /// day a second provider arrives.
    /// </summary>
    private const string DefaultSchemaFallback = "dbo";

    /// <summary>Guards a cyclic <c>HasBaseType</c>/ownership chain, which no generated file contains.</summary>
    private const int WalkDepthCap = 64;

    /// <summary>Separates an owned type's name from its owner in an accumulator key. Not legal in a metadata name.</summary>
    private const string OwnerKeySeparator = "\u0001";

    private readonly EquatableArray<EfModelFile> _files;
    private readonly Dictionary<string, EfEntityFacts> _byName;
    private readonly Dictionary<string, string> _snapshotOfName;
    private readonly Dictionary<string, (string A, string B)> _ambiguous;

    /// <summary>Builds the index, running the five passes over each file and folding files together.</summary>
    /// <param name="files">The parsed snapshots, in the order the compiler supplied them.</param>
    public EfModelIndex(ImmutableArray<EfModelFile> files)
    {
        _byName = new Dictionary<string, EfEntityFacts>(StringComparer.Ordinal);
        _snapshotOfName = new Dictionary<string, string>(StringComparer.Ordinal);
        _ambiguous = new Dictionary<string, (string A, string B)>(StringComparer.Ordinal);
        var kept = new List<EfModelFile>();

        if (!files.IsDefaultOrEmpty)
        {
            // Deduplicated by path AND class name, not by path alone: the same tree can arrive twice,
            // and reading it twice would compare every entity in it against itself, but one file may
            // legitimately declare two snapshot classes and dropping the second would answer from a
            // model the developer never asked about.
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var file in files)
            {
                if (file is null || !seen.Add(file.FilePath + OwnerKeySeparator + file.SnapshotTypeName))
                    continue;

                kept.Add(file);

                foreach (var entity in ResolveFile(file))
                {
                    if (_ambiguous.ContainsKey(entity.Name))
                        continue;

                    if (_byName.TryGetValue(entity.Name, out var first))
                    {
                        // Two snapshots describing the entity identically is an ambiguity nobody can
                        // observe, so it is answered silently - the same gate the whole feature keeps:
                        // a fill that does not move the emitted contract says nothing. The comparison is
                        // free because these are records, and it runs on the RESOLVED facts, with the
                        // model's default schema already folded into the target.
                        if (!first.Equals(entity))
                            _ambiguous.Add(entity.Name, (_snapshotOfName[entity.Name], file.SnapshotTypeName));

                        continue;
                    }

                    _byName.Add(entity.Name, entity);
                    _snapshotOfName.Add(entity.Name, file.SnapshotTypeName);
                }
            }
        }

        _files = EquatableArray<EfModelFile>.From(kept);
    }

    /// <summary>An index over no snapshot at all - the state of every consumer today.</summary>
    public static EfModelIndex Empty { get; } = new EfModelIndex(ImmutableArray<EfModelFile>.Empty);

    /// <summary>Whether the compilation contains a readable model snapshot at all.</summary>
    public bool HasAnySnapshot => _files.Count > 0;

    /// <summary>Answers for one EF metadata name.</summary>
    /// <param name="entityMetadataName">
    /// The name EF uses in the snapshot: namespace, then containing types joined with <c>'+'</c>, then
    /// the type name. Not a display string - <c>ToDisplayString</c> writes <c>Outer.Nested</c> and would
    /// silently never match. Compared ordinally.
    /// </param>
    public EfLookup Lookup(string entityMetadataName)
    {
        if (!HasAnySnapshot)
            return new EfLookup(EfLookupStatus.NoSnapshot, null, null, null);

        if (string.IsNullOrEmpty(entityMetadataName))
            return new EfLookup(EfLookupStatus.NotFound, null, null, null);

        if (_ambiguous.TryGetValue(entityMetadataName, out var pair))
            return new EfLookup(EfLookupStatus.Ambiguous, null, pair.A, pair.B);

        if (_byName.TryGetValue(entityMetadataName, out var facts))
            return new EfLookup(EfLookupStatus.Found, facts, _snapshotOfName[entityMetadataName], null);

        return new EfLookup(EfLookupStatus.NotFound, null, null, null);
    }

    /// <inheritdoc />
    public bool Equals(EfModelIndex? other) => other is not null && _files.Equals(other._files);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as EfModelIndex);

    /// <inheritdoc />
    public override int GetHashCode() => _files.GetHashCode();

    /// <summary>Runs the five passes over one snapshot, producing one resolved entity per name.</summary>
    /// <remarks>
    /// Per file, never across files: two DbContexts in one assembly may legitimately map different
    /// entities to the same table name, and counting them together would refuse both for a collision
    /// that does not exist in either model.
    /// </remarks>
    private static List<EfEntityFacts> ResolveFile(EfModelFile file)
    {
        // Pass 1 - merge blocks by entity name. A snapshot emits an entity's properties in one block and
        // its owned types, relationships and navigations in later ones, so neither the first block nor
        // the last is the entity. Owned blocks are keyed by name AND owner rather than by name alone:
        // one owned type owned by two entities is two rows on two tables, and merging them would leave
        // the second owner looking like the sole occupant of its own table.
        var order = new List<string>();
        var accumulators = new Dictionary<string, EntityAccumulator>(StringComparer.Ordinal);

        foreach (var block in file.Entities.AsArray())
        {
            if (block is null || string.IsNullOrEmpty(block.Name))
                continue;

            var owner = block.Bindability == EfBindability.Owned ? block.Blocker : null;
            var key = owner is null ? block.Name : block.Name + OwnerKeySeparator + owner;

            if (!accumulators.TryGetValue(key, out var accumulator))
            {
                accumulator = new EntityAccumulator(block.Name, owner);
                accumulators.Add(key, accumulator);
                order.Add(key);
            }

            accumulator.Merge(block);
        }

        // Pass 2 - the base graph. Both ends of every HasBaseType edge are needed: the derived end comes
        // straight off the block, and the base end exists only as somebody else's argument.
        var keyOfName = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in order)
        {
            var accumulator = accumulators[key];
            if (accumulator.OwnerName is null && !keyOfName.ContainsKey(accumulator.Name))
                keyOfName.Add(accumulator.Name, key);
        }

        foreach (var key in order)
        {
            var accumulator = accumulators[key];
            if (!keyOfName.ContainsKey(accumulator.Name))
                keyOfName.Add(accumulator.Name, key);
        }

        var isBaseOf = new HashSet<string>(StringComparer.Ordinal);
        var derivedOf = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in order)
        {
            var baseName = accumulators[key].BaseName;
            if (string.IsNullOrEmpty(baseName))
                continue;

            isBaseOf.Add(baseName!);
            if (!derivedOf.ContainsKey(baseName!))
                derivedOf.Add(baseName!, accumulators[key].Name);
        }

        // Pass 3 - resolve every entity's mapping target, through inheritance and then ownership, with
        // the model's default schema folded in. This runs BEFORE the count below, which is the whole
        // reason a TPH hierarchy is caught: its derived types state no table of their own.
        var fileUnreadable = false;
        var targets = new Dictionary<string, EfMappingTarget>(StringComparer.Ordinal);

        foreach (var key in order)
        {
            if (accumulators[key].TargetKind == EfMappingKind.Unreadable)
                fileUnreadable = true;

            targets.Add(key, ResolveTarget(key, accumulators, keyOfName, file.DefaultSchema));
        }

        // Pass 4 - count entity types per resolved target. Tables and views symmetrically, so two
        // entities on one view are refused the same way two on one table are. Keyed case-insensitively
        // because T-SQL identifiers are, and by DISTINCT entity name because one entity may reach the
        // same target through more than one accumulator.
        var occupants = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var key in order)
        {
            var groupKey = TargetGroupKey(targets[key]);
            if (groupKey is null)
                continue;

            if (!occupants.TryGetValue(groupKey, out var names))
            {
                names = new List<string>();
                occupants.Add(groupKey, names);
            }

            var name = accumulators[key].Name;
            if (!names.Contains(name))
                names.Add(name);
        }

        // Pass 5 - apply the bindability clauses in order; the first match wins and supplies the message.
        var resolved = new List<EfEntityFacts>();

        foreach (var key in order)
        {
            var accumulator = accumulators[key];

            // One accumulator answers for each name. The rest exist only for the count above, which is
            // exactly where they had to be counted.
            if (!keyOfName.TryGetValue(accumulator.Name, out var preferred)
                || !string.Equals(preferred, key, StringComparison.Ordinal))
            {
                continue;
            }

            var target = targets[key];
            var bindability = EfBindability.Bindable;
            string? blocker = null;
            string? detail = null;

            if (accumulator.HasBaseTypeCall)
            {
                bindability = EfBindability.InHierarchyDerived;
                blocker = accumulator.BaseName;
            }
            else if (isBaseOf.Contains(accumulator.Name))
            {
                bindability = EfBindability.InHierarchyBase;
                derivedOf.TryGetValue(accumulator.Name, out blocker);
            }
            else if (accumulator.OwnerName is not null)
            {
                bindability = EfBindability.Owned;
                blocker = accumulator.OwnerName;
            }
            else if (accumulator.ComplexCall is not null)
            {
                bindability = EfBindability.ComplexMember;
                detail = accumulator.ComplexCall;
            }
            else if (accumulator.SplitCall is not null)
            {
                bindability = EfBindability.SplitMapping;
                detail = accumulator.SplitCall;
            }
            else if (fileUnreadable)
            {
                bindability = EfBindability.UnreadableMapping;
                detail = "a mapping argument this reader cannot read";
            }
            else if (accumulator.TargetKind == EfMappingKind.None)
            {
                // EF emits ToTable unconditionally for a root, so a non-owned, non-derived entity with no
                // mapping call is a block the reader failed to understand, not a legitimate state.
                bindability = EfBindability.UnreadableMapping;
                detail = "no mapping call this reader recognises";
            }
            else
            {
                var groupKey = TargetGroupKey(target);
                if (groupKey is not null && occupants.TryGetValue(groupKey, out var names) && names.Count >= 2)
                {
                    bindability = EfBindability.SharedTarget;
                    blocker = string.Equals(names[0], accumulator.Name, StringComparison.Ordinal) ? names[1] : names[0];
                }
            }

            resolved.Add(new EfEntityFacts(
                accumulator.Name,
                bindability,
                target,
                blocker,
                detail,
                accumulator.KeyNames.Count == 0 && !accumulator.HasBaseTypeCall,
                EquatableArray<string>.From(accumulator.KeyNames),
                EquatableArray<EfProperty>.From(accumulator.Properties)));
        }

        return resolved;
    }

    /// <summary>Walks one entity's own mapping, then its base chain, then its owner, and folds the schema.</summary>
    private static EfMappingTarget ResolveTarget(
        string startKey,
        Dictionary<string, EntityAccumulator> accumulators,
        Dictionary<string, string> keyOfName,
        string? defaultSchema)
    {
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var key = startKey;

        for (var depth = 0; depth < WalkDepthCap; depth++)
        {
            if (!visited.Add(key) || !accumulators.TryGetValue(key, out var accumulator))
                break;

            if (accumulator.TargetKind == EfMappingKind.Unreadable)
                return new EfMappingTarget(EfMappingKind.Unreadable, null, null);

            if (accumulator.TargetKind == EfMappingKind.Table || accumulator.TargetKind == EfMappingKind.View)
            {
                return new EfMappingTarget(
                    accumulator.TargetKind,
                    accumulator.TargetSchema ?? defaultSchema ?? DefaultSchemaFallback,
                    accumulator.TargetName);
            }

            // A TPH derived type states nothing; a TPC root states ToTable((string)null). Neither is the
            // last word while there is still a base type or an owner above it.
            if (accumulator.BaseName is not null && keyOfName.TryGetValue(accumulator.BaseName, out var baseKey))
            {
                key = baseKey;
                continue;
            }

            if (accumulator.OwnerName is not null && keyOfName.TryGetValue(accumulator.OwnerName, out var ownerKey))
            {
                key = ownerKey;
                continue;
            }

            return accumulator.TargetKind == EfMappingKind.NonMapped
                ? new EfMappingTarget(EfMappingKind.NonMapped, null, null)
                : EfMappingTarget.None;
        }

        return EfMappingTarget.None;
    }

    /// <summary>The sharing-count key for a target, or null when the target names nothing to share.</summary>
    private static string? TargetGroupKey(EfMappingTarget target) => target.Kind switch
    {
        // ToUpperInvariant rather than ToLowerInvariant, for the Turkish-I hazard the .sql branch's
        // ProcKey already documents.
        EfMappingKind.Table => "T|" + (target.Schema ?? "").ToUpperInvariant() + "|" + (target.Name ?? "").ToUpperInvariant(),
        EfMappingKind.View => "V|" + (target.Schema ?? "").ToUpperInvariant() + "|" + (target.Name ?? "").ToUpperInvariant(),
        _ => null,
    };

    /// <summary>Every block naming one entity type (one owner's, when the type is owned), folded together.</summary>
    /// <remarks>
    /// Mutable and private to the constructor's passes. Nothing here reaches a model: the accumulators
    /// are discarded once <see cref="ResolveFile"/> has produced its <see cref="EfEntityFacts"/>.
    /// </remarks>
    private sealed class EntityAccumulator
    {
        private readonly HashSet<string> _propertyNames = new HashSet<string>(StringComparer.Ordinal);

        public EntityAccumulator(string name, string? ownerName)
        {
            Name = name;
            OwnerName = ownerName;
        }

        public string Name { get; }

        public string? OwnerName { get; }

        public bool HasBaseTypeCall { get; private set; }

        public string? BaseName { get; private set; }

        public string? ComplexCall { get; private set; }

        public string? SplitCall { get; private set; }

        public EfMappingKind TargetKind { get; private set; } = EfMappingKind.None;

        public string? TargetSchema { get; private set; }

        public string? TargetName { get; private set; }

        public List<string> KeyNames { get; } = new List<string>();

        public List<EfProperty> Properties { get; } = new List<EfProperty>();

        /// <summary>Folds one block's reading into the entity's.</summary>
        public void Merge(EfEntityFacts block)
        {
            switch (block.Bindability)
            {
                case EfBindability.InHierarchyDerived:
                    HasBaseTypeCall = true;
                    BaseName ??= block.Blocker;
                    break;
                case EfBindability.ComplexMember:
                    ComplexCall ??= block.BlockerDetail ?? "ComplexProperty";
                    break;
                case EfBindability.SplitMapping:
                    SplitCall ??= block.BlockerDetail ?? "SplitToTable";
                    break;
            }

            if (EfMappingTarget.Rank(block.Target.Kind) > EfMappingTarget.Rank(TargetKind))
            {
                TargetKind = block.Target.Kind;
                TargetSchema = block.Target.Schema;
                TargetName = block.Target.Name;
            }

            foreach (var keyName in block.KeyPropertyNames.AsArray())
            {
                if (!KeyNames.Contains(keyName))
                    KeyNames.Add(keyName);
            }

            foreach (var property in block.Properties.AsArray())
            {
                if (property is not null && _propertyNames.Add(property.Name))
                    Properties.Add(property);
            }
        }
    }
}
