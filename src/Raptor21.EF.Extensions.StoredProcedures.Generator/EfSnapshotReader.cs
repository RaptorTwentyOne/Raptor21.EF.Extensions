using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator;

/// <summary>
/// Reads an EF Core <c>ModelSnapshot</c> out of the compilation as ordinary C# syntax.
/// </summary>
/// <remarks>
/// <para>
/// The snapshot is the reconciled output of every configuration route - data annotations, fluent
/// <c>OnModelCreating</c>, <c>IEntityTypeConfiguration</c> classes and conventions all collapse into it -
/// and <c>dotnet ef migrations add</c> checks it in as a source file. So the model is knowable at compile
/// time with no tool, no database connection and no execution of anything.
/// </para>
/// <para>
/// This generator is netstandard2.0 and references nothing but <c>Microsoft.CodeAnalysis.CSharp</c>. It
/// cannot reference EF Core and does not: the attribute and base class are matched by fully-qualified
/// NAME, and every construct in <c>BuildModel</c> is read as syntax. When EF is not referenced the
/// attribute resolves to nothing, the provider is empty, and <see cref="EfModelIndex.Empty"/> is the
/// answer.
/// </para>
/// <para>
/// Unrecognised calls are ignored rather than refused. The file is written by whatever <c>dotnet ef</c>
/// tooling last ran, which need not match the referenced EF package, so <c>ProductVersion</c> is never
/// asserted on and no branch exists for a call that has not been seen. What the reader refuses is the
/// narrow set of constructs where a wrong answer is possible: those become an
/// <see cref="EfMappingKind.Unreadable"/> mapping or a bindability clause, and the caller turns them into
/// a diagnostic against the row type.
/// </para>
/// <para>
/// The reader itself reports nothing, ever, exactly as the .sql branch's parser does. A snapshot that
/// does not parse, names no entities or matches no row costs a consumer precisely zero.
/// </para>
/// </remarks>
internal static class EfSnapshotReader
{
    /// <summary>The attribute every snapshot and every migration Designer partial carries.</summary>
    /// <remarks>A plain string. Nothing here references EF Core.</remarks>
    public const string DbContextAttributeMetadataName =
        "Microsoft.EntityFrameworkCore.Infrastructure.DbContextAttribute";

    private const string ModelSnapshotFullyQualifiedName =
        "global::Microsoft.EntityFrameworkCore.Infrastructure.ModelSnapshot";

    private const string BuildModelMethodName = "BuildModel";

    /// <summary>Guards pathological ownership nesting. Real models are two or three deep.</summary>
    private const int OwnershipDepthCap = 32;

    /// <summary>
    /// How a <c>Property&lt;T&gt;</c> type argument is written down. Must stay identical to the row
    /// emitter's own non-nullable format: the two strings are compared against each other to prove that
    /// the class and the model agree about a member's type, and a format difference would read as a
    /// disagreement.
    /// </summary>
    internal static readonly SymbolDisplayFormat StoreTypeFormat = new SymbolDisplayFormat(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>Reads one <c>[DbContext]</c>-attributed class, or returns null when it is not a snapshot.</summary>
    /// <param name="ctx">The attribute match. Consumed entirely inside this method.</param>
    /// <param name="ct">Cancellation for the pipeline step.</param>
    /// <returns>The file's value-equatable reading, or null when there is nothing to read.</returns>
    public static EfModelFile? Read(GeneratorAttributeSyntaxContext ctx, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        if (ctx.TargetSymbol is not INamedTypeSymbol type)
            return null;

        // Semantic, never a syntactic ': ModelSnapshot' match, which an alias or a same-named local type
        // defeats. This alone excludes every per-migration .Designer.cs partial: they carry the same
        // [DbContext(typeof(T))], but their base is Migrations.Migration.
        if (!DerivesFromModelSnapshot(type))
            return null;

        if (ctx.TargetNode is not TypeDeclarationSyntax declaration)
            return null;

        // The redundant second discriminator, kept deliberately. A Designer partial's BuildTargetModel
        // body is a byte-identical copy of the model TODAY, which is precisely why reading the wrong one
        // fails a year later rather than now - the copy goes stale the day a second migration is added.
        var buildModel = declaration.Members
            .OfType<MethodDeclarationSyntax>()
            .FirstOrDefault(m => m.Identifier.ValueText == BuildModelMethodName
                                 && m.ParameterList.Parameters.Count == 1
                                 && m.Body is not null);

        if (buildModel is null)
            return null;

        var builderName = buildModel.ParameterList.Parameters[0].Identifier.ValueText;

        string? dbContextSimpleName = null;
        var attribute = ctx.Attributes.Length > 0 ? ctx.Attributes[0] : null;
        if (attribute is { ConstructorArguments.Length: > 0 }
            && attribute.ConstructorArguments[0].Value is INamedTypeSymbol dbContext)
        {
            dbContextSimpleName = dbContext.Name;
        }

        string? defaultSchema = null;
        var entities = new List<EfEntityFacts>();

        foreach (var expression in BodyExpressions(buildModel.Body!))
        {
            ct.ThrowIfCancellationRequested();

            var calls = Unwind(expression, out var root);
            if (root is null || !string.Equals(root, builderName, StringComparison.Ordinal))
                continue;

            foreach (var call in calls)
            {
                switch (call.Name)
                {
                    // SharedTypeEntity is not in the recognised set for its own sake - typeof() can never
                    // name one, so no row will ever bind to it. It is read so that it counts against the
                    // table it occupies, which is the only way an entity sharing that table is refused.
                    case "Entity":
                    case "SharedTypeEntity":
                        ReadEntityBlock(ctx, call.Invocation, null, entities, 0, ct);
                        break;

                    case "HasDefaultSchema":
                        defaultSchema ??= FirstStringLiteral(call.Invocation);
                        break;

                    // Everything else at model level is skipped, HasAnnotation above all: this library
                    // writes every procedure script into the snapshot as an "Sp:" annotation, so reading
                    // them would invalidate the model index on every procedure edit.
                }
            }
        }

        return new EfModelFile(
            ctx.TargetNode.SyntaxTree.FilePath,
            type.ToDisplayString(),
            dbContextSimpleName,
            defaultSchema,
            EquatableArray<EfEntityFacts>.From(entities));
    }

    /// <summary>Whether some type in the base chain is EF's <c>ModelSnapshot</c>.</summary>
    private static bool DerivesFromModelSnapshot(INamedTypeSymbol type)
    {
        for (var current = type.BaseType; current is not null; current = current.BaseType)
        {
            if (current.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) == ModelSnapshotFullyQualifiedName)
                return true;
        }

        return false;
    }

    /// <summary>Reads one <c>Entity</c>/<c>SharedTypeEntity</c>/<c>OwnsOne</c>/<c>OwnsMany</c> block.</summary>
    /// <remarks>
    /// The block's own reading is appended before anything it owns, so the file's entity list reads in
    /// declaration order.
    /// </remarks>
    private static void ReadEntityBlock(
        GeneratorAttributeSyntaxContext ctx,
        InvocationExpressionSyntax invocation,
        string? ownerName,
        List<EfEntityFacts> results,
        int depth,
        CancellationToken ct)
    {
        var name = FirstStringLiteral(invocation);
        if (string.IsNullOrEmpty(name))
            return;

        var draft = new BlockDraft(name!, ownerName);
        var owned = new List<EfEntityFacts>();
        var lambda = LastLambda(invocation);

        if (lambda is not null && LambdaParameterName(lambda) is string parameterName)
        {
            foreach (var expression in LambdaExpressions(lambda))
            {
                ct.ThrowIfCancellationRequested();

                // The single rule that keeps the walk honest: a statement counts only when it roots at
                // the enclosing lambda's OWN parameter. That is what makes
                // SqlServerPropertyBuilderExtensions.UseIdentityColumn(b.Property<int>("Id")) a reference
                // to the Id property rather than a second declaration of it - the statement roots at the
                // extension class, and the b.Property occurrence is nested in an argument list. It also
                // ignores every future provider extension without knowing its name.
                var calls = Unwind(expression, out var root);
                if (root is null || !string.Equals(root, parameterName, StringComparison.Ordinal))
                    continue;

                InterpretEntityChain(ctx, calls, draft, name!, owned, depth, ct);
            }
        }

        results.Add(draft.ToFacts());
        results.AddRange(owned);
    }

    /// <summary>Interprets one statement inside an entity block as a single unit.</summary>
    /// <remarks>
    /// Facets chain, so <c>b.Property&lt;string&gt;("Name").IsRequired().HasColumnType("varchar(128)")</c>
    /// is one statement carrying three facts and has to be read as one. Everything else is read call by
    /// call, because a chain whose head is unrecognised may still carry a call that is not.
    /// </remarks>
    private static void InterpretEntityChain(
        GeneratorAttributeSyntaxContext ctx,
        List<ChainCall> calls,
        BlockDraft draft,
        string entityName,
        List<EfEntityFacts> owned,
        int depth,
        CancellationToken ct)
    {
        if (calls.Count == 0)
            return;

        if (calls[0].Name == "Property" || calls[0].Name == "PrimitiveCollection")
        {
            ReadProperty(ctx, calls, draft, ct);
            return;
        }

        foreach (var call in calls)
        {
            switch (call.Name)
            {
                case "HasKey":
                    foreach (var keyName in StringLiterals(call.Invocation))
                        draft.AddKey(keyName);
                    break;

                case "HasBaseType":
                    // The PRESENCE of the call is the refusal, not its argument: a HasBaseType whose
                    // argument does not parse still means the entity is derived.
                    draft.SetBaseType(FirstStringLiteral(call.Invocation));
                    break;

                case "ToTable":
                    draft.ApplyMapping(EfMappingKind.Table, call.Invocation);
                    break;

                case "ToView":
                case "ToSqlQuery":
                case "ToFunction":
                    draft.ApplyMapping(EfMappingKind.View, call.Invocation);
                    break;

                case "SplitToTable":
                case "SplitToView":
                    draft.SetSplit(call.Name);
                    break;

                // Complex types are not entity types and are never recursed into: their columns carry a
                // navigation prefix EF applies in the relational model and never writes into the
                // snapshot, so the owner's table holds columns no block here can name.
                case "ComplexProperty":
                case "ComplexCollection":
                    draft.SetComplex(call.Name);
                    break;

                case "OwnsOne":
                case "OwnsMany":
                    if (depth < OwnershipDepthCap)
                        ReadEntityBlock(ctx, call.Invocation, entityName, owned, depth + 1, ct);
                    break;
            }
        }
    }

    /// <summary>Reads a <c>Property&lt;T&gt;</c> or <c>PrimitiveCollection&lt;T&gt;</c> chain.</summary>
    private static void ReadProperty(
        GeneratorAttributeSyntaxContext ctx,
        List<ChainCall> calls,
        BlockDraft draft,
        CancellationToken ct)
    {
        var head = calls[0];
        var name = FirstStringLiteral(head.Invocation);
        if (string.IsNullOrEmpty(name))
            return;

        // Resolved through the semantic model inside the transform, so 'DateTime' becomes
        // System.DateTime and no using-directive or aliasing question survives it. Nothing symbol-shaped
        // escapes: only the display string and a bool leave this method.
        string? storeClrType = null;
        var storeTypeIsNullable = false;

        if (head.TypeArgument is not null)
        {
            var resolved = ctx.SemanticModel.GetTypeInfo(head.TypeArgument, ct).Type;

            if (resolved is INamedTypeSymbol named
                && named.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T
                && named.TypeArguments.Length == 1)
            {
                storeTypeIsNullable = true;
                resolved = named.TypeArguments[0];
            }

            // A type that does not resolve leaves StoreClrType null and is refused by the caller, rather
            // than guessed from the syntax text.
            if (resolved is not null and not IErrorTypeSymbol && resolved.TypeKind != TypeKind.Error)
                storeClrType = resolved.ToDisplayString(StoreTypeFormat);
        }

        var isRequired = false;
        string? columnName = null;
        string? columnType = null;

        for (var i = 1; i < calls.Count; i++)
        {
            switch (calls[i].Name)
            {
                case "IsRequired":
                    isRequired = !HasFalseArgument(calls[i].Invocation);
                    break;
                case "HasColumnName":
                    columnName ??= FirstStringLiteral(calls[i].Invocation);
                    break;
                case "HasColumnType":
                    columnType ??= FirstStringLiteral(calls[i].Invocation);
                    break;
            }
        }

        draft.AddProperty(new EfProperty(
            name!,
            storeClrType,
            storeTypeIsNullable,
            isRequired,
            head.Name == "PrimitiveCollection",
            columnName,
            columnType));
    }

    /// <summary>The expression statements of <c>BuildModel</c>'s body, in order.</summary>
    private static IEnumerable<ExpressionSyntax> BodyExpressions(BlockSyntax body)
    {
        foreach (var statement in body.Statements)
        {
            if (statement is ExpressionStatementSyntax expressionStatement)
                yield return expressionStatement.Expression;
        }
    }

    /// <summary>The expression statements of a configuration lambda's body, in order.</summary>
    private static IEnumerable<ExpressionSyntax> LambdaExpressions(AnonymousFunctionExpressionSyntax lambda)
    {
        if (lambda.Block is BlockSyntax block)
        {
            foreach (var expression in BodyExpressions(block))
                yield return expression;
        }
        else if (lambda.ExpressionBody is ExpressionSyntax expressionBody)
        {
            yield return expressionBody;
        }
    }

    /// <summary>The single parameter a configuration lambda names its builder, or null.</summary>
    private static string? LambdaParameterName(AnonymousFunctionExpressionSyntax lambda) => lambda switch
    {
        SimpleLambdaExpressionSyntax simple => simple.Parameter.Identifier.ValueText,
        ParenthesizedLambdaExpressionSyntax parenthesized when parenthesized.ParameterList.Parameters.Count == 1 =>
            parenthesized.ParameterList.Parameters[0].Identifier.ValueText,
        _ => null,
    };

    /// <summary>The last lambda argument of a call - the configuration block, at any arity.</summary>
    private static AnonymousFunctionExpressionSyntax? LastLambda(InvocationExpressionSyntax invocation)
    {
        AnonymousFunctionExpressionSyntax? found = null;

        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (argument.Expression is AnonymousFunctionExpressionSyntax lambda)
                found = lambda;
        }

        return found;
    }

    /// <summary>Unwinds an invocation chain to its root expression, innermost call first.</summary>
    /// <remarks>
    /// Unwound outermost-first and reversed, because a chain is one statement: the head says what is
    /// being declared and the tail says what is true about it.
    /// </remarks>
    /// <param name="expression">The statement's expression.</param>
    /// <param name="rootIdentifier">
    /// The identifier the chain roots at, or null when it roots at anything else. Only a chain rooted at
    /// the enclosing builder parameter is a statement about the entity being configured.
    /// </param>
    private static List<ChainCall> Unwind(ExpressionSyntax expression, out string? rootIdentifier)
    {
        var calls = new List<ChainCall>();
        var current = expression;
        rootIdentifier = null;

        while (true)
        {
            switch (current)
            {
                case InvocationExpressionSyntax invocation
                    when invocation.Expression is MemberAccessExpressionSyntax access
                         && access.IsKind(SyntaxKind.SimpleMemberAccessExpression):
                    calls.Add(new ChainCall(
                        access.Name.Identifier.ValueText,
                        invocation,
                        access.Name is GenericNameSyntax generic && generic.TypeArgumentList.Arguments.Count == 1
                            ? generic.TypeArgumentList.Arguments[0]
                            : null));
                    current = access.Expression;
                    continue;

                case ParenthesizedExpressionSyntax parenthesized:
                    current = parenthesized.Expression;
                    continue;

                case IdentifierNameSyntax identifier:
                    rootIdentifier = identifier.Identifier.ValueText;
                    calls.Reverse();
                    return calls;

                default:
                    calls.Reverse();
                    return calls;
            }
        }
    }

    /// <summary>The first string-literal argument of a call, or null when it has none.</summary>
    private static string? FirstStringLiteral(InvocationExpressionSyntax invocation)
    {
        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (ClassifyArgument(argument.Expression, out var value) == ArgumentKind.StringLiteral)
                return value;
        }

        return null;
    }

    /// <summary>Every string-literal argument of a call, in order - <c>HasKey("a", "b")</c>.</summary>
    private static IEnumerable<string> StringLiterals(InvocationExpressionSyntax invocation)
    {
        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (ClassifyArgument(argument.Expression, out var value) == ArgumentKind.StringLiteral && value is not null)
                yield return value;
        }
    }

    /// <summary>Whether a call's first argument is the literal <c>false</c> - <c>IsRequired(false)</c>.</summary>
    private static bool HasFalseArgument(InvocationExpressionSyntax invocation)
    {
        var arguments = invocation.ArgumentList.Arguments;
        return arguments.Count > 0
            && Unparenthesize(arguments[0].Expression) is LiteralExpressionSyntax literal
            && literal.IsKind(SyntaxKind.FalseLiteralExpression);
    }

    /// <summary>Reads the leading arguments of a mapping call, tolerating arity, casts and a trailing lambda.</summary>
    /// <remarks>
    /// Matched on the invocation NAME and the leading string-literal arguments, never on argument count:
    /// <c>ToTable("X")</c>, <c>ToTable("X", (string)null)</c>, <c>ToTable("X", "s")</c> and both
    /// <c>t =&gt; { ... }</c> overloads all say the same thing about the table.
    /// <para>
    /// The cast is not syntax noise to be stripped blindly. <c>(string)null</c> in the FIRST position
    /// changes the meaning entirely - it is how a TPC root and a keyless type say "no table" - while in
    /// the second it only means "no schema". The classifier reports what the argument is and the position
    /// decides what it means.
    /// </para>
    /// </remarks>
    private static MappingArguments ReadMappingArguments(
        InvocationExpressionSyntax invocation,
        out string? name,
        out string? schema)
    {
        name = null;
        schema = null;

        // Leading, non-lambda arguments only: a trailing configuration block says nothing about which
        // table this is.
        var leading = new List<ExpressionSyntax>();
        foreach (var argument in invocation.ArgumentList.Arguments)
        {
            if (argument.Expression is AnonymousFunctionExpressionSyntax)
                break;

            leading.Add(argument.Expression);
        }

        if (leading.Count == 0)
            return MappingArguments.Unreadable;

        switch (ClassifyArgument(leading[0], out name))
        {
            case ArgumentKind.NullLiteral:
                name = null;
                return MappingArguments.ExplicitNull;
            case ArgumentKind.Other:
                name = null;
                return MappingArguments.Unreadable;
        }

        if (leading.Count > 1 && ClassifyArgument(leading[1], out schema) == ArgumentKind.Other)
        {
            // A schema this reader cannot read would silently become the default one, which can merge or
            // split a sharing group. Closed the same way an unreadable table name is.
            name = null;
            schema = null;
            return MappingArguments.Unreadable;
        }

        return MappingArguments.Named;
    }

    /// <summary>Classifies one argument as a string literal, a null of any spelling, or neither.</summary>
    private static ArgumentKind ClassifyArgument(ExpressionSyntax expression, out string? value)
    {
        value = null;
        var current = Unparenthesize(expression);

        if (current is CastExpressionSyntax cast)
            current = Unparenthesize(cast.Expression);

        if (current is DefaultExpressionSyntax)
            return ArgumentKind.NullLiteral;

        if (current is LiteralExpressionSyntax literal)
        {
            if (literal.IsKind(SyntaxKind.StringLiteralExpression))
            {
                value = literal.Token.ValueText;
                return ArgumentKind.StringLiteral;
            }

            if (literal.IsKind(SyntaxKind.NullLiteralExpression) || literal.IsKind(SyntaxKind.DefaultLiteralExpression))
                return ArgumentKind.NullLiteral;
        }

        return ArgumentKind.Other;
    }

    /// <summary>Strips redundant parentheses.</summary>
    private static ExpressionSyntax Unparenthesize(ExpressionSyntax expression)
    {
        var current = expression;
        while (current is ParenthesizedExpressionSyntax parenthesized)
            current = parenthesized.Expression;

        return current;
    }

    /// <summary>What one argument of a mapping call turned out to be.</summary>
    private enum ArgumentKind
    {
        StringLiteral,
        NullLiteral,
        Other,
    }

    /// <summary>What a mapping call said, once its leading arguments have been read.</summary>
    private enum MappingArguments
    {
        /// <summary>A named table or view.</summary>
        Named,

        /// <summary>An explicit <c>(string)null</c> in the first position - "no table".</summary>
        ExplicitNull,

        /// <summary>An argument this reader cannot read. Poisons the whole file.</summary>
        Unreadable,
    }

    /// <summary>One call in an unwound chain.</summary>
    /// <remarks>Syntax, and local to the walk. Nothing of this shape ever reaches a model.</remarks>
    private readonly struct ChainCall
    {
        public ChainCall(string name, InvocationExpressionSyntax invocation, TypeSyntax? typeArgument)
        {
            Name = name;
            Invocation = invocation;
            TypeArgument = typeArgument;
        }

        /// <summary>The method's simple name, without its type arguments.</summary>
        public string Name { get; }

        /// <summary>The call itself, for its argument list.</summary>
        public InvocationExpressionSyntax Invocation { get; }

        /// <summary>The single type argument of a generic call - <c>Property&lt;T&gt;</c> - else null.</summary>
        public TypeSyntax? TypeArgument { get; }
    }

    /// <summary>One entity block being read, before it becomes an <see cref="EfEntityFacts"/>.</summary>
    /// <remarks>
    /// Mutable and local to the walk. What leaves it is the block's own reading only: whether anything
    /// derives from this entity, and whether anything else is mapped where it is, are whole-model
    /// questions no single block can answer, and <see cref="EfModelIndex"/> answers them.
    /// </remarks>
    private sealed class BlockDraft
    {
        private readonly string _name;
        private readonly string? _ownerName;
        private readonly List<string> _keyNames = new List<string>();
        private readonly List<EfProperty> _properties = new List<EfProperty>();

        private bool _hasBaseTypeCall;
        private string? _baseName;
        private string? _complexCall;
        private string? _splitCall;
        private EfMappingKind _kind = EfMappingKind.None;
        private string? _schema;
        private string? _targetName;

        public BlockDraft(string name, string? ownerName)
        {
            _name = name;
            _ownerName = ownerName;
        }

        public void AddKey(string keyName)
        {
            if (!_keyNames.Contains(keyName))
                _keyNames.Add(keyName);
        }

        public void AddProperty(EfProperty property) => _properties.Add(property);

        public void SetBaseType(string? baseName)
        {
            _hasBaseTypeCall = true;
            _baseName ??= baseName;
        }

        public void SetSplit(string callName) => _splitCall ??= callName;

        public void SetComplex(string callName) => _complexCall ??= callName;

        /// <summary>Folds one <c>ToTable</c>/<c>ToView</c>/<c>ToSqlQuery</c>/<c>ToFunction</c> call in.</summary>
        public void ApplyMapping(EfMappingKind kind, InvocationExpressionSyntax invocation)
        {
            switch (ReadMappingArguments(invocation, out var name, out var schema))
            {
                case MappingArguments.Unreadable:
                    Promote(EfMappingKind.Unreadable, null, null);
                    break;
                case MappingArguments.ExplicitNull:
                    Promote(EfMappingKind.NonMapped, null, null);
                    break;
                default:
                    Promote(kind, schema, name);
                    break;
            }
        }

        /// <summary>The block's reading, with only the clauses a single block can decide.</summary>
        /// <remarks>
        /// The direct clauses are applied in the order the rule states, so a block that trips two of them
        /// reports the one the rule names first. <c>Blocker</c> is the base type name or the owner name
        /// according to which fired; no generated block is both derived and owned, because EF supports
        /// neither owned hierarchies nor an owned base type.
        /// </remarks>
        public EfEntityFacts ToFacts()
        {
            var bindability = EfBindability.Bindable;
            string? blocker = null;
            string? detail = null;

            if (_hasBaseTypeCall)
            {
                bindability = EfBindability.InHierarchyDerived;
                blocker = _baseName;
            }
            else if (_ownerName is not null)
            {
                bindability = EfBindability.Owned;
                blocker = _ownerName;
            }
            else if (_complexCall is not null)
            {
                bindability = EfBindability.ComplexMember;
                detail = _complexCall;
            }
            else if (_splitCall is not null)
            {
                bindability = EfBindability.SplitMapping;
                detail = _splitCall;
            }

            return new EfEntityFacts(
                _name,
                bindability,
                new EfMappingTarget(_kind, _schema, _targetName),
                blocker,
                detail,
                _keyNames.Count == 0 && !_hasBaseTypeCall,
                EquatableArray<string>.From(_keyNames),
                EquatableArray<EfProperty>.From(_properties));
        }

        private void Promote(EfMappingKind kind, string? schema, string? name)
        {
            if (EfMappingTarget.Rank(kind) <= EfMappingTarget.Rank(_kind))
                return;

            _kind = kind;
            _schema = schema;
            _targetName = name;
        }
    }
}
