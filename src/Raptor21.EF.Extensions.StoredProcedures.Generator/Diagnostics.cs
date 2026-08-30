using Microsoft.CodeAnalysis;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator;

internal static class Diagnostics
{
    private const string Category = "Raptor21.EF.Extensions.StoredProcedures";

    public static readonly DiagnosticDescriptor GroupNotPartial = new(
        id: "SPG001",
        title: "Stored-procedure group class must be partial",
        messageFormat: "Class '{0}' contains [StoredProcedure] methods and must be declared 'partial'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedReturn = new(
        id: "SPG002",
        title: "Unsupported stored-procedure return type",
        messageFormat: "Method '{0}' has an unsupported return type. Supported: Task, Task<int>, Task<short>, Task<IReadOnlyList<TRow>>, and a tuple of the RETURN value with the OUTPUT parameters or the result set.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // Reported from ParameterResolver, not from the syntax transform: a parameter without [Sql] is only
    // an error once the .sql has also failed to supply a name. It keeps the parameter's own Location.
    public static readonly DiagnosticDescriptor ParamMissingSql = new(
        id: "SPG003",
        title: "Parameter has no SQL name",
        messageFormat: "Parameter '{0}' of method '{1}' has no [Sql] attribute and no SQL parameter named '{2}' was inferred for procedure '{3}'; add [Sql(\"@name\", ...)] or supply the procedure's .sql script",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor InvalidSqlName = new(
        id: "SPG004",
        title: "Invalid SQL parameter name",
        messageFormat: "Parameter '{0}' of method '{1}' has SQL name '{2}'; it must be non-empty and start with '@'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // Also deferred to ParameterResolver: the procedure is now a third source of the type name, after
    // the attribute and before the .NET type.
    public static readonly DiagnosticDescriptor UnsupportedParamType = new(
        id: "SPG005",
        title: "Cannot infer SQL type for parameter",
        messageFormat: "Parameter '{0}' of method '{1}' has .NET type '{2}' and no SQL type could be inferred from [Sql], from procedure '{3}', or from the .NET type; specify it via [Sql(\"@name\", \"sqltype\", length)]",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor RowNotPositionalPartialRecord = new(
        id: "SPG006",
        title: "[SqlRow] type must be a partial positional record",
        messageFormat: "Type '{0}' marked [SqlRow] must be a 'partial record' with a primary constructor (positional members)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor RowTypeNotFound = new(
        id: "SPG007",
        title: "Result row type is not marked [SqlRow]",
        messageFormat: "Method '{0}' returns rows of '{1}', which must be a partial record marked [SqlRow]",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor UnsupportedColumnType = new(
        id: "SPG008",
        title: "Cannot read result column type",
        messageFormat: "Member '{0}' of [SqlRow] '{1}' has .NET type '{2}' with no supported IDataRecord reader",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // Deferred to ParameterResolver because OUTPUT can now come from the .sql - but only for a
    // parameter that carries no [Sql] at all, which is why the message no longer names the attribute.
    public static readonly DiagnosticDescriptor OutputArityMismatch = new(
        id: "SPG009",
        title: "Return tuple does not match OUTPUT parameters",
        messageFormat: "Method '{0}' returns {1} value(s) after the RETURN element but declares {2} OUTPUT parameter(s); they must match in count and order",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // SPG010-SPG015 are the library's first non-Error diagnostics, and deliberately so: the .sql file is
    // evidence, not authority. A stale or hand-edited script must never turn a compiling consumer into a
    // wall of errors, so every disagreement between the file and the code is reported and then decided
    // in favour of the code. <NoWarn> takes them one at a time; <R21SqlScriptDiscovery>false</...> takes
    // the whole feature out.
    //
    // Non-Error is not on its own enough. A consumer who sets TreatWarningsAsErrors turns every one of
    // them into a wall anyway, and an ungated advisory fires once per script parameter rather than once
    // per real problem, so its volume scales with the size of the promoted corpus. Each one is therefore
    // also gated, and the gate measures one thing only: whether the emitted contract moved. Where a fact
    // came from is a different question and the wrong one - [Sql("@A", "int")] and [Sql("@A", 32)] can
    // describe the same contract while taking different facets from the file, and a consumer whose
    // contract did not move must not be able to tell which spelling they used from their build log.
    //
    //   SPG010  ungated. It is not "a script exists", it is "the two disagree", and it can only fire on
    //           a facet the developer wrote out by hand, so its volume is bounded by real contradictions
    //           and each one means the contract or the procedure is wrong.
    //   SPG011  only for a method at least one of whose parameters the script moved, and
    //   SPG012  only for such a parameter. A declaration that renders what it rendered before the script
    //           was promoted has not been touched by it, so an advisory about that script's contents
    //           costs the consumer a warning and buys them nothing - and a legacy corpus is made of
    //           DEFAULTed parameters and bare varchars, which is precisely what those two would report.
    //   SPG013  gated already: it fires only on a type the file supplied.
    //   SPG014,
    //   SPG015  only once a parameter has actually gone unresolved. They answer one question - "why did
    //           the script not supply this?" - so they are mutually exclusive, and neither is worth
    //           saying to a method that never needed an answer.
    //   SPG017  only for a facet whose emitted value moved, which is the same gate stated per facet.
    //   SPG019,
    //   SPG020  ungated for the same reason as SPG010: neither is about the script's contents, both are
    //           statements that the emitted contract and the procedure cannot both be right. Their
    //           volume is bounded by parameters the runtime provably cannot bind.

    public static readonly DiagnosticDescriptor AttributeContradictsScript = new(
        id: "SPG010",
        title: "[Sql] attribute contradicts the procedure script",
        messageFormat: "Parameter '{0}' of method '{1}' declares {2} '{3}' in its [Sql] attribute, but procedure '{4}' declares '{5}'. The attribute wins; one of the two is wrong, and a contract that disagrees with the procedure fails startup validation.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ScriptParameterNotBound = new(
        id: "SPG011",
        title: "Procedure declares a parameter the method does not carry",
        messageFormat: "Procedure '{0}' declares parameter '{1}', which method '{2}' does not carry. StoredProcedureValidator compares parameter counts and will reject this contract at startup, even when the SQL parameter has a DEFAULT.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ScriptParameterHasNoLength = new(
        id: "SPG012",
        title: "Procedure parameter declares no length",
        messageFormat: "Procedure '{0}' declares parameter '{1}' as '{2}' with no length, which SQL Server reads as '{2}(1)'. No length was inferred, so the parameter keeps its current binding; state the intended length in the procedure or in [Sql(\"{1}\", n)].",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ScriptTypeCannotBind = new(
        id: "SPG013",
        title: "Procedure type cannot bind the .NET type",
        messageFormat: "Procedure '{0}' declares parameter '{1}' as '{2}', which cannot bind .NET type '{3}' of parameter '{4}' on method '{5}'",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor NoScriptForProcedure = new(
        id: "SPG014",
        title: "No .sql script reached the compiler for this procedure",
        messageFormat: "No .sql script declaring procedure '{0}' reached the compiler, so parameter facts for method '{1}' could not be inferred. Scripts are picked up from EmbeddedResource *.sql items; if yours are declared in Directory.Build.targets, come from another project, or are excluded by R21SqlScriptDiscovery, add them with <AdditionalFiles Include=\"...\" />.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor DuplicateProcedureScript = new(
        id: "SPG015",
        title: "Procedure is declared by more than one .sql script",
        messageFormat: "Procedure '{0}' is declared by more than one .sql file ('{1}' and '{2}'). No parameter facts are inferred for it; remove the duplicate or spell the parameters out with [Sql].",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    // SPG016 and SPG018 are Errors, and that is safe for exactly one reason: each can fire only on a
    // parameter carrying no [Sql] attribute at all, and such a parameter is already an SPG003 error
    // today, before any of this existed. The .sql is allowed to rescue a declaration the old generator
    // rejected; it is never allowed to reject one the old generator accepted.

    public static readonly DiagnosticDescriptor ScriptTypeDeclined = new(
        id: "SPG016",
        title: "Procedure declares the parameter with a construct that carries no SQL type",
        messageFormat: "Procedure '{0}' declares parameter '{1}' as {2}, which supplies no SQL type, and parameter '{3}' of method '{4}' carries no [Sql] attribute to state one. The .NET type is not a fallback here: the procedure's own type is one this generator cannot bind, so a type inferred from .NET would be wrong rather than merely unverified, and would fail at startup instead of at build. State the type with [Sql(\"{1}\", \"sqltype\")], or change the procedure.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // A warning, not informational. The fill is the feature working as designed and the emitted contract
    // now agrees with the procedure, so it is not an error - but it moves a contract that compiles today
    // without the developer writing anything, and the facet it most often moves is precisely the one that
    // turns a StoredProcedureValidator check the old contract SKIPPED into one it ENFORCES, so a stale
    // script can convert a booting application into a startup InvalidOperationException. Info was
    // measured invisible: zero occurrences at `dotnet build` and at -v n, six only at -v d, which means
    // no CI log a team actually reads would ever carry it. Only a facet whose emitted value actually
    // moved is reported; a fill that lands on the value the declaration already had says nothing at all.
    public static readonly DiagnosticDescriptor ScriptFillsUnstatedFacet = new(
        id: "SPG017",
        title: "Procedure script supplies a facet the [Sql] attribute leaves unstated",
        messageFormat: "Parameter '{0}' of method '{1}' states no {2}, so procedure '{3}' supplies one and the emitted contract's {2} changes from {4} to {5}. State the {2} in [Sql] to pin the binding this declaration had before any script was read.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    // The remedy deliberately does not mention [Sql]. Stating the direction is what an earlier draft of
    // this message suggested, and it was measured: [Sql("@Total", Output = false)] silences this error by
    // making the parameter attributed, and buys a contract the validator rejects at startup on the
    // output-flag mismatch plus an SPG010 that no spelling of [Sql] can answer - Output = true changes the
    // contract, Output = false is the contradiction being reported, and omitting it raises SPG020 instead.
    // Only two things actually fix this, and both are named.
    public static readonly DiagnosticDescriptor InferredOutputHasNowhereToGo = new(
        id: "SPG018",
        title: "Inferred OUTPUT parameter has no return element to carry it",
        messageFormat: "Procedure '{0}' declares parameter '{1}' as OUTPUT and parameter '{2}' of method '{3}' takes its direction from the script, but the method's return type has no element after the RETURN value to receive it, so the value the procedure writes back is discarded. Return a tuple whose elements after the RETURN value match the procedure's OUTPUT parameters, or change the procedure so '{1}' is not OUTPUT. Binding it as an input instead is not a third option: StoredProcedureValidator rejects that contract at startup on the output-flag mismatch.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // SPG019 and SPG020 are the attributed mirrors of SPG016 and SPG018, and they exist because those two
    // are scoped to a parameter carrying no [Sql] at all - which is the only scope in which an Error is
    // safe, and therefore a scope that reaches none of a codebase with [Sql] on every parameter. For that
    // population these are the only channel there is. Both report a contract the runtime provably cannot
    // bind, and both are Warnings: the emitted contract is byte-for-byte what it was before the script
    // was promoted, so an error here would wall a consumer whose code compiles today.

    public static readonly DiagnosticDescriptor ScriptTypeDeclinedForStatedParameter = new(
        id: "SPG019",
        title: "Procedure declares a type the generator cannot map and the parameter kept its .NET guess",
        messageFormat: "Procedure '{0}' declares parameter '{1}' as {2}, which supplies no SQL type, so parameter '{3}' of method '{4}' kept the '{5}' inferred from its .NET type. The procedure's own type is one this generator cannot bind, so that inference is wrong rather than merely unverified and the contract it produces fails startup validation. State a scalar type the procedure accepts with [Sql(\"{1}\", \"sqltype\")], or change the procedure - a table-valued or CURSOR parameter has no contract form at all.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ScriptOutputNotTakenByAttribute = new(
        id: "SPG020",
        title: "Procedure declares the parameter OUTPUT and the [Sql] attribute states no direction",
        messageFormat: "Procedure '{0}' declares parameter '{1}' as OUTPUT, but parameter '{2}' of method '{3}' carries a [Sql] attribute that states no direction, and OUTPUT is never taken from the script for an attributed parameter. The contract binds '{1}' as an input and StoredProcedureValidator rejects it at startup on the output-flag mismatch. Write Output = true in the [Sql] attribute and give the method's return type an element after the RETURN value to receive it, or change the procedure so '{1}' is not OUTPUT.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);
    // SPG021-SPG032 are the entity-binding diagnostics, and every one of them is an Error. That is the
    // SPG016/SPG018 argument above, reused with a stronger gate. There the guard was "a parameter with no
    // [Sql] at all, which is already SPG003 today"; here the guard is a token that cannot be written:
    // 'Entity' is a new named property on SqlRowAttribute, which is internal, is emitted by this generator
    // into the consumer's own compilation, and declared no members at all before this change. No source
    // that compiles today can spell 'Entity = typeof(...)', so every one of these fires only on a
    // declaration that did not exist before it, and there is no consumer who compiles cleanly today and
    // sees a new error.
    //
    // That argument has a condition, and it must be re-checked rather than remembered: it holds only while
    // Entity is unspellable. Giving SqlRowAttribute a constructor overload that takes the entity type, a
    // default, or an MSBuild-property equivalent would make some of these reachable from code that
    // compiles today, and each one would have to be re-argued at that point.
    //
    // Nothing here is a Warning, and that is deliberate rather than an oversight. The .sql branch warns
    // because a stale script is evidence that must never wall a compiling consumer. There is no analogous
    // state here: a snapshot-derived refusal produces no row at all, so silence would leave the developer
    // wondering why nothing was generated - the exact condition the established gate says warrants a
    // diagnostic. And there is no partial outcome to warn about, because the binding either produces a
    // complete member list or none.
    //
    // The absence of a snapshot is never on its own reportable at any severity: a row type that did not
    // ask is told nothing. Excluding Migrations from the compilation builds at 0 warnings and 0 errors
    // today, and so does every consumer who has not yet run 'dotnet ef migrations add' - most of them.

    public static readonly DiagnosticDescriptor EntityRowNotPartialRecord = new(
        id: "SPG021",
        title: "Entity-bound [SqlRow] type must be a partial record",
        messageFormat: "Type '{0}' binds its members to entity type '{1}', so the generator writes its primary constructor and it must be declared as a 'partial record' with no parameter list of its own. Declare it as 'public partial record {0};'.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EntityRowIsPositional = new(
        id: "SPG022",
        title: "Entity-bound [SqlRow] type also declares primary-constructor members",
        messageFormat: "Type '{0}' declares primary-constructor members and also binds to entity type '{1}'. These are the two ways of saying what the row's members are, only one can win, and merging them would produce a contract neither spelling describes, so the generator refuses both. Remove the parameter list to take the members from the model, or remove 'Entity = typeof({1})' to keep the members written here.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // The clause the whole reader exists for: an entity type is bindable only if its columns are
    // exclusively its own, and this is what says so about a table or a view two entity types both reach.
    // Its soundness rests on something that has to be said where the code is and not only in a design
    // note: "column name = HasColumnName ?? property name" is universally true ONLY because owned and
    // complex types - the sole constructs whose column names carry an undeclared navigation prefix - are
    // refused by SPG025 and SPG026. Relaxing either one silently reintroduces wrong column names, and no
    // test would fail, because the snapshot contains no evidence of the prefix at all.
    public static readonly DiagnosticDescriptor EntitySharesItsMappingTarget = new(
        id: "SPG023",
        title: "Entity type shares its table or view with another entity type",
        messageFormat: "Entity type '{0}' is mapped to '{1}', which entity type '{2}' is also mapped to, so no row members can be generated for it. A column on a shared target is nullable whenever any entity mapped there is optional, and the snapshot records nullability per property rather than per column: EF's own IsColumnNullable says so for derived types in a TPH hierarchy and for properties on optional types sharing the same table. A non-nullable member over a nullable column emits an unguarded read and StoredProcedureValidator rejects the contract at startup; a nullable member over a NOT NULL column is rejected just as hard. There is no safe guess in either direction, so nothing is guessed. Declare '{3}' as a positional record whose members you spell out yourself.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EntityInInheritanceHierarchy = new(
        id: "SPG024",
        title: "Entity type is part of an inheritance hierarchy",
        messageFormat: "Entity type '{0}' {1} in an inheritance hierarchy, so no row members can be generated for it. Under TPH a derived type's non-nullable properties map to nullable columns and the snapshot emits no marker at all; under TPT and TPC each block lists only the properties declared on that type, so a row built from one would silently be missing every inherited member. A derived block carries no mapping strategy - only the hierarchy root does - so the three cannot be told apart where it matters and all three are refused together. Declare '{2}' as a positional record whose members you spell out yourself.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EntityIsOwned = new(
        id: "SPG025",
        title: "Entity type is owned by another entity type",
        messageFormat: "Entity type '{0}' is owned by '{1}', so no row members can be generated for it. Its real column names carry the navigation prefix EF applies in the relational model and never writes into the snapshot - the snapshot says 'City' where the table says '{2}_City' - so every column name the generator produced would be wrong. That prefix is an overridable convention that nests for deeper ownership, and this generator reconstructs no conventions. Declare '{3}' as a positional record whose members you spell out yourself.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EntityMappingNotBindable = new(
        id: "SPG026",
        title: "Entity type is mapped through a construct this reader does not bind",
        messageFormat: "Entity type '{0}' is mapped through {1}, which this reader does not bind, so no row members can be generated for it. Its columns are either distributed across more than one mapping fragment or named somewhere the snapshot does not record, so a single ordered column list for it does not exist. Declare '{2}' as a positional record whose members you spell out yourself.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EntityNoModelSnapshot = new(
        id: "SPG027",
        title: "No EF Core model snapshot in this compilation",
        messageFormat: "Type '{0}' binds its members to entity type '{1}', but this compilation contains no EF Core model snapshot, so the model could not be read. The snapshot is the class deriving from Microsoft.EntityFrameworkCore.Infrastructure.ModelSnapshot that 'dotnet ef migrations add' writes. There is none if no migration has been added yet, if the Migrations folder is excluded from compilation, or if migrations live in a separate project - a source generator sees only the syntax trees of the compilation it runs on, and a referenced assembly's snapshot carries no syntax at all, only IL. Add a migration, bring the snapshot into this project, or declare '{0}' as a positional record whose members you spell out yourself.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EntityNotInModel = new(
        id: "SPG028",
        title: "Type is not an entity type in any model snapshot",
        messageFormat: "Type '{0}' binds its members to '{1}', which is not an entity type in any model snapshot in this compilation. The snapshot names entity types by their metadata name, so a nested type is written 'Outer+Nested'; and some entity types have no CLR type behind them at all - a many-to-many join table is named 'PostTag' with no namespace, and a SharedTypeEntity is named by string, so typeof() matches neither. Add the type to the model and run 'dotnet ef migrations add', or declare '{0}' as a positional record whose members you spell out yourself.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EntityDescribedByTwoSnapshots = new(
        id: "SPG029",
        title: "Entity type is described differently by two model snapshots",
        messageFormat: "Entity type '{1}' appears in model snapshots '{2}' and '{3}', which describe it differently, so no row members can be generated for '{0}'. One CLR type mapped by two DbContexts can carry two table names and two column types in one assembly, and 'Entity = typeof({1})' names neither context. Picking one by declaration order would make the emitted contract depend on something you cannot see. Declare '{0}' as a positional record whose members you spell out yourself.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    // SPG030 and SPG031 are two halves of an intersection, and neither table alone is sufficient: the
    // reader's getter table admits a Property<int> on a varchar column that the validator rejects, and the
    // validator's allowed-types table admits time -> TimeSpan and datetimeoffset -> DateTimeOffset, for
    // which this generator emits no reader at all.
    public static readonly DiagnosticDescriptor EntityPropertyHasNoReader = new(
        id: "SPG030",
        title: "Model property has no IDataRecord reader",
        messageFormat: "Property '{0}' of entity type '{1}' is stored as '{2}', which has no IDataRecord reader in this generator, so no row members can be generated for '{3}'. {4} Emitting nothing for the member would leave the contract's column list shorter than the row's constructor, which is clean at build and fatal at startup, so the whole row is refused instead. Declare '{3}' as a positional record whose members you spell out yourself.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EntityColumnTypeNotAccepted = new(
        id: "SPG031",
        title: "Column type and .NET type are not a pair StoredProcedureValidator accepts",
        messageFormat: "Property '{0}' of entity type '{1}' has column type '{2}', which StoredProcedureValidator does not accept for .NET type '{3}', so no row members can be generated for '{4}'. A contract built from this pair would compile and then throw on the first startup validation pass, which is the failure this binding exists to move to build time. {5} Declare '{4}' as a positional record whose members you spell out yourself.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EntityStoreTypeDiffersFromMember = new(
        id: "SPG032",
        title: "Model store type and class member type disagree",
        messageFormat: "Entity type '{1}' stores property '{0}' as '{2}', but the member '{0}' on the class is '{3}', so no row members can be generated for '{4}'. A result-set reader hands back the stored value, and this generator cannot run the value converter that would turn it into '{3}' - the converter is an EF object and this generator references no EF. Emitting the stored type under the class's member name would compile and quietly mean something else. Decide which of the two the procedure actually returns, then declare '{4}' as a positional record whose members you spell out yourself.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor EntityBindsNoColumns = new(
        id: "SPG033",
        title: "Entity binding produced no row members",
        messageFormat: "Entity type '{1}' contributed no members to '{0}': every mapped property is a shadow property, so there is nothing for a class member to match. An empty row would emit a zero-length ColumnSpec[], and StoredProcedureValidator skips a contract whose ResultColumns is empty, so neither this build nor the first startup would notice that '{0}' reads nothing. Declare '{0}' as a positional record whose members you spell out yourself.",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
