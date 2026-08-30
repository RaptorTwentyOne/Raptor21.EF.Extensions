using System.Linq;
using Microsoft.CodeAnalysis;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator;

internal enum ReturnCategory
{
    Unsupported,
    Int32,
    Int16,
    ResultSet,
    NonQuery,
    ReturnWithOutputs,
}

/// <summary>
/// The .NET side of a parameter, classified once in the syntax transform.
/// </summary>
/// <remarks>
/// There is exactly one CLR classification in this generator. It feeds both the SQL type inferred from
/// the .NET type (<c>InferSqlType</c>) and the narrow agreement table behind SPG013, so the two can
/// never drift into disagreeing about what a parameter is. <see cref="Other"/> means "not classified",
/// never "classified as something unusual": nothing is inferred for it and nothing is rejected for it.
/// </remarks>
internal enum ClrKind
{
    /// <summary>No SQL type is inferable and no agreement is asserted.</summary>
    Other,
    String,
    Boolean,
    Byte,
    Int16,
    Int32,
    Int64,
    Decimal,
    Single,
    Double,
    DateTime,
    Guid,
    ByteArray,
}

/// <summary>A diagnostic to be reported during the emit phase (carries its own location).</summary>
internal sealed record DiagnosticInfo(DiagnosticDescriptor Descriptor, Location? Location, EquatableArray<string> Args)
{
    public Diagnostic ToDiagnostic() => Diagnostic.Create(Descriptor, Location, Args.AsArray());
}

/// <summary>
/// One parameter as the C# declaration alone describes it, before the .sql has been consulted.
/// </summary>
/// <remarks>
/// Every fact the attribute can state is nullable here, because "the developer did not state it" and
/// "the developer stated the default" are different questions to the merge: only the first may be
/// answered from the procedure. Nullability is read from <see cref="AttributeData"/> - the constructor
/// arity and the named-argument list - never from a materialised attribute object, which cannot tell
/// the two apart.
/// <para>
/// <see cref="Output"/> carries that distinction furthest. The merge never answers it from the
/// procedure for an attributed parameter, so its nullability changes no emitted byte - it decides only
/// what the developer is told: a stated direction that disagrees with the procedure is a contradiction
/// (SPG010), while an unstated one is a declined fact (SPG020). Collapsing the two reported a sentence
/// the developer had not written and left them nothing they could write to answer it.
/// </para>
/// </remarks>
internal sealed record ParamDraft(
    string CSharpName,
    string ClrTypeDisplay,
    ClrKind Clr,
    bool HasSqlAttribute,
    string? SqlName,
    string? TypeName,
    int? Length,
    byte? Precision,
    byte? Scale,
    bool? Output,
    string? InferredSqlType,
    Location? Location);

/// <summary>A single stored-procedure parameter (the typed call argument that maps to a SQL parameter).</summary>
internal sealed record SpParam(string CSharpName, string SqlTypeExpression, bool IsOutput);

/// <summary>A parameter as it appears in the partial method signature (reproduced verbatim).</summary>
internal sealed record SigParam(string TypeText, string Name);

/// <summary>Everything the syntax transform can know about one stored-procedure method.</summary>
/// <remarks>
/// This model is deliberately not emittable on its own: its parameters are drafts, not rendered
/// contract entries. <see cref="ParameterResolver.Resolve"/> turns it into a <see cref="ResolvedMethod"/>.
/// </remarks>
internal sealed record MethodModel(
    string Namespace,
    string ClassName,
    string ClassAccessibility,
    bool ClassIsPartial,
    string MethodName,
    string MethodAccessibility,
    string ReturnTypeText,
    ReturnCategory Return,
    EquatableArray<SigParam> SignatureParams,
    string? CancellationTokenParamName,
    EquatableArray<ParamDraft> ParamDrafts,
    string Schema,
    string ProcName,
    string ContractFieldName,
    string? ResultRowFqn,
    bool ReturnsTuple,
    EquatableArray<string> OutputConversions,
    Location? MethodLocation,
    EquatableArray<DiagnosticInfo> Diagnostics)
{
    /// <summary>
    /// Whether the declaration is broken by something decidable from C# alone (SPG002, SPG004).
    /// </summary>
    /// <remarks>
    /// Deliberately NOT the emit-time validity check any more. SPG003, SPG005 and SPG009 now depend on
    /// the .sql and live in <see cref="ResolvedMethod.Diagnostics"/>, so a model that satisfies this
    /// predicate may still be unresolvable. <see cref="ResolvedMethod.IsValid"/> is the only predicate
    /// the emitter may ask.
    /// </remarks>
    public bool IsSelfValid => Return != ReturnCategory.Unsupported
        && !Diagnostics.AsArray().Any(d => d.Descriptor.DefaultSeverity == DiagnosticSeverity.Error);
}

/// <summary>A <see cref="MethodModel"/> after the procedure's parameter facts have been merged in.</summary>
/// <remarks>
/// <see cref="SpParams"/> is populated only when <see cref="IsValid"/> holds. That is a structural
/// guarantee, not a convention: an unresolvable parameter contributes nothing to the contract, so
/// emitting a contract whose <c>ProcParamSpec[]</c> is shorter than the procedure's parameter list -
/// silent at build, fatal at startup on <c>dbParams.Count != contractParams.Count</c> - cannot be
/// represented. The throwing stub reads <see cref="SignatureParams"/> only.
/// </remarks>
internal sealed record ResolvedMethod(
    string Namespace,
    string ClassName,
    string ClassAccessibility,
    bool ClassIsPartial,
    string MethodName,
    string MethodAccessibility,
    string ReturnTypeText,
    ReturnCategory Return,
    EquatableArray<SigParam> SignatureParams,
    string? CancellationTokenParamName,
    EquatableArray<SpParam> SpParams,
    string Schema,
    string ProcName,
    string ContractFieldName,
    string? ResultRowFqn,
    bool ReturnsTuple,
    EquatableArray<string> OutputConversions,
    EquatableArray<DiagnosticInfo> Diagnostics)
{
    public bool IsValid => Return != ReturnCategory.Unsupported
        && !Diagnostics.AsArray().Any(d => d.Descriptor.DefaultSeverity == DiagnosticSeverity.Error);
}

/// <summary>A single result-row member mapped to a SQL result-set column.</summary>
internal sealed record RowColumn(string ColumnName, string ColumnSpecExpression, string ReadExpression);

/// <summary>One mappable member of the entity class a row binds to, classified exactly as a row member is.</summary>
/// <remarks>
/// Produced by <c>ClassifyMember</c>, which is the same classification <c>BuildReadExpression</c> performs
/// for a hand-written positional record - factored out rather than reimplemented, so the two paths cannot
/// drift into emitting different text for the same shape. <see cref="Getter"/> is null when no
/// <c>IDataRecord</c> reader exists for the type, which is a refusal (SPG030) rather than a skip.
/// <para>
/// <see cref="ElementTypeText"/> is written with the generator's non-nullable format, which is the format
/// <c>EfSnapshotReader.StoreTypeFormat</c> writes a <c>Property&lt;T&gt;</c> argument with. The two strings
/// are compared against each other to prove the class and the model agree about a member's type, so a
/// format difference between them would read as a disagreement rather than as a bug.
/// </para>
/// </remarks>
/// <param name="Name">The member's name, which is also the model property name it is matched to, ordinally.</param>
/// <param name="ElementTypeText">The type with <c>Nullable&lt;T&gt;</c> unwrapped - <c>int</c>, <c>string</c>, <c>global::System.DateTime</c>.</param>
/// <param name="IsNullableValue">The member is <c>T?</c> for a value type <c>T</c>.</param>
/// <param name="IsReferenceType">The unwrapped type is a reference type, so every read is <c>IsDBNull</c>-guarded.</param>
/// <param name="IsByteArray">The member is <c>byte[]</c>, which is read through <c>GetValue</c> rather than a getter.</param>
/// <param name="Getter">The <c>IDataRecord</c> getter name, or null when the type has none.</param>
internal sealed record EfMemberCandidate(
    string Name,
    string ElementTypeText,
    bool IsNullableValue,
    bool IsReferenceType,
    bool IsByteArray,
    string? Getter);

/// <summary>The entity type a <c>[SqlRow(Entity = typeof(X))]</c> row binds its members to.</summary>
/// <remarks>
/// The join key and the class's own members, both snapshotted in the syntax transform - the only place
/// that holds the symbol. Nothing symbol-shaped escapes it: <see cref="EntityMetadataName"/> is built by
/// hand rather than taken from a display format, because EF names a nested entity type <c>Outer+Nested</c>
/// and every <c>ToDisplayString</c> spelling writes <c>Outer.Nested</c> and would silently never match.
/// <para>
/// The class is the authority on which members exist and the model is the authority on the column each
/// one maps to. A model property with no member here is a shadow property and is skipped silently; a
/// member with no model property behind it is not emitted at all.
/// </para>
/// </remarks>
/// <param name="EntityMetadataName">Namespace, then containing types joined with <c>'+'</c>, then the name.</param>
/// <param name="EntityDisplayName">How the developer spelled the type, for diagnostic text only.</param>
/// <param name="Members">Every public or internal instance property with a getter, and every such field.</param>
internal sealed record EntityBinding(
    string EntityMetadataName,
    string EntityDisplayName,
    EquatableArray<EfMemberCandidate> Members);

/// <summary>A [SqlRow] record for which FromDataRecord is generated.</summary>
/// <remarks>
/// Two shapes, one model. A positional record states its own members and arrives here fully rendered,
/// with <see cref="ParameterListText"/> empty and <see cref="Binding"/> null - which is what makes
/// <see cref="RowResolver.Resolve"/> the identity function for it, and byte-identity a structural fact
/// rather than an inspected one. An Entity-bound row arrives with no columns and a
/// <see cref="Binding"/>, and gets both from the model during resolution.
/// </remarks>
/// <param name="Namespace">The row type's namespace, empty for the global one.</param>
/// <param name="TypeName">The row type's own name.</param>
/// <param name="Accessibility">The row type's declared accessibility, rendered as C#.</param>
/// <param name="IsPartialRecord">The declaration is usable: a partial record the emitter can write into.</param>
/// <param name="Fqn">The fully-qualified name methods name their result row by.</param>
/// <param name="Columns">One entry per emitted member, in emitted order.</param>
/// <param name="ParameterListText">
/// The primary constructor the generator writes for an Entity-bound row, rendered with the indentation
/// <c>EmitRow</c> declares the type at. Empty on the positional path, where the developer wrote it.
/// </param>
/// <param name="Binding">The entity type to take members from, or null when the row states its own.</param>
/// <param name="TypeLocation">
/// The row type's own location, carried for the diagnostics <see cref="RowResolver"/> raises against it -
/// the same discipline <c>MethodModel.MethodLocation</c> keeps for the .sql branch. Null on the positional
/// path, deliberately: a <see cref="Location"/> compares by tree and span, so carrying one unconditionally
/// would make a row's model unequal after an edit that moved its declaration and changed nothing else.
/// </param>
/// <param name="Diagnostics">Everything to report about this row, each against its own location.</param>
internal sealed record RowModel(
    string Namespace,
    string TypeName,
    string Accessibility,
    bool IsPartialRecord,
    string Fqn,
    EquatableArray<RowColumn> Columns,
    string ParameterListText,
    EntityBinding? Binding,
    Location? TypeLocation,
    EquatableArray<DiagnosticInfo> Diagnostics)
{
    public bool IsValid => IsPartialRecord
        && !Diagnostics.AsArray().Any(d => d.Descriptor.DefaultSeverity == DiagnosticSeverity.Error);
}
