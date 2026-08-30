using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Raptor21.EF.Extensions.StoredProcedures.Generator;

[Generator(LanguageNames.CSharp)]
public sealed class StoredProcedureGenerator : IIncrementalGenerator
{
    // internal because ParameterResolver renders the ProcParamSpec/SqlTypeSpec initialisers, which moved
    // there with the merge. One spelling of the namespace, in one place.
    internal const string ContractsNs = "global::Raptor21.EF.Extensions.StoredProcedures.Contracts";
    private const string ExecutionNs = "global::Raptor21.EF.Extensions.StoredProcedures.Execution";

    private static readonly SymbolDisplayFormat TypeFmt = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes
            | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

    // Same as TypeFmt but without the nullable reference '?' modifier (e.g. for 'x as string', where 'string?' is illegal).
    private static readonly SymbolDisplayFormat TypeFmtNonNullable = new(
        globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
        typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
        genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
        miscellaneousOptions: SymbolDisplayMiscellaneousOptions.UseSpecialTypes);

    /// <summary>Names of the pipeline steps that assert their own caching behaviour.</summary>
    /// <remarks>
    /// Public so the incrementality tests name the same steps this file does. The header-only design's
    /// central claim - that editing a procedure body re-runs nothing - is only checkable through
    /// <c>GeneratorRunResult.TrackedSteps</c>, and only when the names match.
    /// </remarks>
    public static class TrackingNames
    {
        /// <summary>One parsed .sql file.</summary>
        public const string SqlHeaders = "SqlHeaders";

        /// <summary>Every parsed header, indexed by procedure identity.</summary>
        public const string SqlHeaderIndex = "SqlHeaderIndex";

        /// <summary>One method after its parameter facts have been merged.</summary>
        public const string ResolvedMethods = "ResolvedMethods";

        /// <summary>One parsed EF Core ModelSnapshot class.</summary>
        public const string EfSnapshots = "EfSnapshots";

        /// <summary>Every parsed snapshot, indexed by entity metadata name.</summary>
        public const string EfModelIndex = "EfModelIndex";

        /// <summary>One row after its members have been bound to the model.</summary>
        public const string ResolvedRows = "ResolvedRows";
    }

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        context.RegisterPostInitializationOutput(ctx =>
            ctx.AddSource("StoredProcedureAttributes.g.cs", EmittedAttributes.Source));

        var methods = context.SyntaxProvider.ForAttributeWithMetadataName(
                EmittedAttributes.ProcedureAttributeMetadataName,
                predicate: static (node, _) => node is MethodDeclarationSyntax,
                transform: static (ctx, ct) => GetMethodModel(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!);

        var rows = context.SyntaxProvider.ForAttributeWithMetadataName(
                EmittedAttributes.RowAttributeMetadataName,
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, ct) => GetRowModel(ctx, ct))
            .Where(static r => r is not null)
            .Select(static (r, _) => r!);

        var rootNamespace = context.AnalyzerConfigOptionsProvider.Select(static (p, _) =>
            p.GlobalOptions.TryGetValue("build_property.rootnamespace", out var ns) ? ns : null);

        var assemblyName = context.CompilationProvider.Select(static (c, _) => c.AssemblyName);

        // The .sql files reach the compiler as AdditionalFiles (see the package's buildTransitive
        // targets). Everything Roslyn owns - the AdditionalText, its SourceText - is consumed inside this
        // Select and discarded; only the value-equatable SqlFileModel leaves it. That is what makes an
        // edit below a procedure's AS a no-op: the parser stops at the body, the model compares equal,
        // and the Collect below is served from the cache.
        //
        // This branch is never combined with CompilationProvider or anything else carrying a Compilation,
        // ISymbol or Location. It joins the pipeline once, at the resolve step, and only against methods.
        var sqlHeaders = context.AdditionalTextsProvider
            .Where(static t => t.Path.EndsWith(".sql", System.StringComparison.OrdinalIgnoreCase))
            .Select(static (t, ct) => ParseSqlFile(t, ct))
            .WithTrackingName(TrackingNames.SqlHeaders)
            .Collect()
            .Select(static (files, _) => new SqlHeaderIndex(files))
            .WithTrackingName(TrackingNames.SqlHeaderIndex);

        // The EF Core model reaches the generator as ordinary C# syntax: the ModelSnapshot class that
        // `dotnet ef migrations add` writes is in the same compilation, and it is the reconciled output of
        // every configuration route - data annotations, fluent OnModelCreating, IEntityTypeConfiguration
        // classes and conventions all collapse into it. The attribute name is a plain string, so nothing
        // here references EF Core and the csproj keeps its single PackageReference; when EF is not
        // referenced the attribute resolves to nothing, the provider is empty, and EfModelIndex.Empty is
        // the answer - which is the state of every consumer today.
        //
        // Shaped like the .sql branch above and for the same reasons: the GeneratorAttributeSyntaxContext,
        // its SemanticModel included, is consumed inside the transform and only the value-equatable
        // EfModelFile leaves it. The reader skips HasAnnotation, which is what makes this library's own
        // procedure scripts - written into that same file as "Sp:" annotations - a no-op for the model.
        var snapshots = context.SyntaxProvider.ForAttributeWithMetadataName(
                EfSnapshotReader.DbContextAttributeMetadataName,
                predicate: static (node, _) => node is ClassDeclarationSyntax,
                transform: static (ctx, ct) => EfSnapshotReader.Read(ctx, ct))
            .Where(static m => m is not null)
            .Select(static (m, _) => m!)
            .WithTrackingName(TrackingNames.EfSnapshots)
            .Collect()
            .Select(static (files, _) => new EfModelIndex(files))
            .WithTrackingName(TrackingNames.EfModelIndex);

        // The merge. Kept as an intermediate transform rather than a fifth root of the Combine chain
        // below: as a root, any .sql keystroke would re-run Emit and rewrite every group file and the
        // registry. Here a body-only edit stops at SqlHeaders, and even a header edit only re-resolves -
        // a healthy method's ResolvedMethod carries no Location and an empty diagnostic list, so it
        // compares equal and Emit is skipped.
        var resolved = methods
            .Combine(sqlHeaders)
            .Select(static (pair, ct) => ParameterResolver.Resolve(pair.Left, pair.Right, ct))
            .WithTrackingName(TrackingNames.ResolvedMethods);

        // The same shape, one branch over: an intermediate transform merged into the ROWS provider rather
        // than a root of the Combine chain, so a snapshot edit cannot rewrite every group file in the
        // assembly. RowResolver.Resolve returns its input reference for a row that states no entity, so a
        // hand-written positional record stays cached here however the model moves - which is the same
        // fact as its emitted text being byte-identical.
        var resolvedRows = rows
            .Combine(snapshots)
            .Select(static (p, ct) => RowResolver.Resolve(p.Left, p.Right, ct))
            .WithTrackingName(TrackingNames.ResolvedRows);

        var combined = resolved.Collect()
            .Combine(resolvedRows.Collect())
            .Combine(rootNamespace)
            .Combine(assemblyName);

        context.RegisterSourceOutput(combined, static (spc, data) =>
            Emit(spc, data.Left.Left.Left, data.Left.Left.Right, data.Left.Right ?? data.Right));
    }

    /// <summary>Parses one .sql file into the value-equatable model the rest of the pipeline sees.</summary>
    private static SqlFileModel ParseSqlFile(AdditionalText text, System.Threading.CancellationToken ct)
    {
        // A file Roslyn cannot read yields no headers rather than an exception or a diagnostic. The .sql
        // branch reports nothing, ever: every .sql-derived diagnostic is attached to a method during
        // resolution, so a script that does not parse, declares no procedure, or matches no declaration
        // costs a consumer nothing.
        var content = text.GetText(ct)?.ToString() ?? "";
        var headers = ProcedureHeaderParser.Parse(text.Path, content, ct);
        return new SqlFileModel(text.Path, EquatableArray<SqlProcHeader>.From(headers));
    }

    private static MethodModel? GetMethodModel(GeneratorAttributeSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        if (ctx.TargetSymbol is not IMethodSymbol method)
            return null;

        var type = method.ContainingType;
        var ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
        var classIsPartial = type.DeclaringSyntaxReferences.Any(r =>
            r.GetSyntax(ct) is ClassDeclarationSyntax c && c.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)));

        var diags = new List<DiagnosticInfo>();

        // Proc name
        var fullName = ctx.Attributes[0].ConstructorArguments.Length > 0
            ? ctx.Attributes[0].ConstructorArguments[0].Value as string ?? ""
            : "";
        var (schema, procName) = SplitProcName(fullName);

        // Return type
        var returnInfo = ClassifyReturn(method.ReturnType);
        if (returnInfo.Category == ReturnCategory.Unsupported)
            diags.Add(new DiagnosticInfo(Diagnostics.UnsupportedReturn, method.Locations.FirstOrDefault(),
                new EquatableArray<string>(new[] { method.Name })));

        // Parameters. Nothing is rendered and nothing is decided here: a parameter that states no name or
        // no type is not yet wrong, because the procedure may still supply either. It becomes a draft,
        // and ParameterResolver decides. SPG003, SPG005 and SPG009 are reported there for that reason.
        //
        // A parameter is never skipped, however broken. Dropping one used to be harmless only because the
        // method was simultaneously invalid; with the decision deferred, a dropped parameter would be a
        // contract shorter than the procedure - clean at build, fatal at startup.
        var sig = new List<SigParam>();
        var drafts = new List<ParamDraft>();
        string? ctParam = null;

        foreach (var p in method.Parameters)
        {
            sig.Add(new SigParam(p.Type.ToDisplayString(TypeFmt), p.Name));

            if (IsCancellationToken(p.Type))
            {
                ctParam = p.Name;
                continue;
            }

            var sqlAttr = p.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == EmittedAttributes.SqlAttributeMetadataName);

            string? sqlName = null, typeName = null;
            int? length = null;
            byte? precision = null, scale = null;
            bool? output = null;

            if (sqlAttr is not null)
            {
                (sqlName, typeName, length, precision, scale, output) = ParseSqlAttribute(sqlAttr);

                // Only a *stated* name can be an invalid one. [Sql(Length = 32)] states none, which is a
                // question for the resolver, not a malformed name.
                if (sqlName is not null && (string.IsNullOrWhiteSpace(sqlName) || !sqlName.StartsWith("@")))
                {
                    diags.Add(new DiagnosticInfo(Diagnostics.InvalidSqlName, p.Locations.FirstOrDefault(),
                        new EquatableArray<string>(new[] { p.Name, method.Name, sqlName })));
                }
            }

            var clr = ClassifyClr(p.Type);
            drafts.Add(new ParamDraft(
                CSharpName: p.Name,
                ClrTypeDisplay: p.Type.ToDisplayString(),
                Clr: clr,
                HasSqlAttribute: sqlAttr is not null,
                SqlName: sqlName,
                TypeName: typeName,
                Length: length,
                Precision: precision,
                Scale: scale,
                Output: output,
                InferredSqlType: InferSqlType(clr),
                Location: p.Locations.FirstOrDefault()));
        }

        return new MethodModel(
            Namespace: ns,
            ClassName: type.Name,
            ClassAccessibility: Accessibility(type.DeclaredAccessibility),
            ClassIsPartial: classIsPartial,
            MethodName: method.Name,
            MethodAccessibility: Accessibility(method.DeclaredAccessibility),
            ReturnTypeText: returnInfo.Text,
            Return: returnInfo.Category,
            SignatureParams: EquatableArray<SigParam>.From(sig),
            CancellationTokenParamName: ctParam,
            ParamDrafts: EquatableArray<ParamDraft>.From(drafts),
            Schema: schema,
            ProcName: procName,
            ContractFieldName: "__Contract_" + method.Name,
            ResultRowFqn: returnInfo.RowFqn,
            ReturnsTuple: returnInfo.ReturnsTuple,
            OutputConversions: EquatableArray<string>.From(returnInfo.OutputConversions),
            MethodLocation: method.Locations.FirstOrDefault(),
            Diagnostics: EquatableArray<DiagnosticInfo>.From(diags));
    }

    private static RowModel? GetRowModel(GeneratorAttributeSyntaxContext ctx, System.Threading.CancellationToken ct)
    {
        if (ctx.TargetSymbol is not INamedTypeSymbol type)
            return null;

        var ns = type.ContainingNamespace.IsGlobalNamespace ? "" : type.ContainingNamespace.ToDisplayString();
        var fqn = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        var accessibility = Accessibility(type.DeclaredAccessibility);
        var diags = new List<DiagnosticInfo>();

        // The one fork in this method, and the gate the whole feature stands on: 'Entity' is a named
        // property on an attribute this generator emits into the consumer's own compilation, and
        // SqlRowAttribute declared no members at all before this change - so no source that compiles today
        // can spell it, and the arm below is unreachable from every declaration that exists. Everything
        // after the fork is the code that ran before it, unmodified, including SPG006's condition.
        if (TryReadEntityArgument(ctx.Attributes[0], out var entity))
            return GetEntityBoundRowModel(type, ns, fqn, accessibility, entity, ct);

        // SPG006's single condition, and it stays single: a [SqlRow] type that is not a partial record
        // with a non-empty primary constructor is an error, whatever it is instead. Not a record at all,
        // a non-partial one, `partial record R();` with an empty parameter list, a record struct - the
        // emitter unconditionally writes `partial record {TypeName}{ParameterListText}`, so every one of
        // them would produce CS0261/CS8863/CS8865 inside generated code, and this diagnostic exists to
        // explain that instead.
        var recordDecl = type.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(ct))
            .OfType<RecordDeclarationSyntax>()
            .FirstOrDefault(r => r.ParameterList is { Parameters.Count: > 0 });
        var isPartial = type.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(ct))
            .OfType<RecordDeclarationSyntax>()
            .Any(r => r.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)));

        // IsValueType is the record-struct arm the comment above promises. A `partial record struct
        // R(int Id);` is a RecordDeclarationSyntax with a primary constructor and the partial modifier, so
        // the other two clauses both pass it, and the emitter's `partial record` then lands as CS0261 in
        // generated code - a failure about a file the developer did not write.
        if (type.IsValueType || recordDecl is null || !isPartial)
        {
            diags.Add(new DiagnosticInfo(Diagnostics.RowNotPositionalPartialRecord, type.Locations.FirstOrDefault(),
                new EquatableArray<string>(new[] { type.Name })));
            return new RowModel(ns, type.Name, accessibility, false, fqn,
                EquatableArray<RowColumn>.From(new List<RowColumn>()), "", null, null,
                EquatableArray<DiagnosticInfo>.From(diags));
        }

        var cols = new List<RowColumn>();
        foreach (var p in recordDecl.ParameterList!.Parameters)
        {
            // A parameter whose symbol does not resolve used to be dropped, which left the contract's
            // column list shorter than the row's constructor: clean at build and fatal at startup on the
            // validator's count check. It is reported instead, on the same descriptor an unreadable
            // parameter type already used - and it can only happen in a compilation that is already
            // broken, so no consumer who compiles cleanly sees it.
            if (ctx.SemanticModel.GetDeclaredSymbol(p, ct) is not IParameterSymbol sym)
            {
                diags.Add(new DiagnosticInfo(Diagnostics.UnsupportedColumnType, p.GetLocation(),
                    new EquatableArray<string>(new[] { p.Identifier.Text, type.Name, p.Type?.ToString() ?? "?" })));
                continue;
            }

            var colName = sym.Name;
            var colAttr = sym.GetAttributes().FirstOrDefault(a =>
                a.AttributeClass?.ToDisplayString() == EmittedAttributes.ColumnAttributeMetadataName);
            if (colAttr is { ConstructorArguments.Length: > 0 } && colAttr.ConstructorArguments[0].Value is string cn && !string.IsNullOrWhiteSpace(cn))
                colName = cn;

            var read = BuildReadExpression(sym.Type, colName, out var specTypeText);
            if (read is null)
            {
                diags.Add(new DiagnosticInfo(Diagnostics.UnsupportedColumnType, sym.Locations.FirstOrDefault(),
                    new EquatableArray<string>(new[] { sym.Name, type.Name, sym.Type.ToDisplayString() })));
                continue;
            }

            var colSpec = $"new {ContractsNs}.ColumnSpec(\"{colName}\", typeof({specTypeText}))";
            cols.Add(new RowColumn(colName, colSpec, read));
        }

        return new RowModel(ns, type.Name, accessibility, true, fqn,
            EquatableArray<RowColumn>.From(cols), "", null, null, EquatableArray<DiagnosticInfo>.From(diags));
    }

    /// <summary>Reads <c>Entity = typeof(X)</c> from the row attribute.</summary>
    /// <remarks>
    /// The return value answers "was it stated", which is the fork; the out parameter answers "did it
    /// resolve", which is a different question. <c>Entity = typeof(Undefined)</c> states it and resolves
    /// to an error type: the compilation already carries CS0246 for that, so it is stated-but-null here
    /// and the row is refused in silence rather than with an SPG piled on top of a compiler error.
    /// </remarks>
    private static bool TryReadEntityArgument(AttributeData attribute, out INamedTypeSymbol? entity)
    {
        entity = null;
        var stated = false;

        foreach (var na in attribute.NamedArguments)
        {
            if (na.Key != "Entity")
                continue;

            stated = true;
            if (na.Value.Kind == TypedConstantKind.Type
                && na.Value.Value is INamedTypeSymbol named
                && named.TypeKind != TypeKind.Error)
            {
                entity = named;
            }
        }

        return stated;
    }

    /// <summary>Snapshots an Entity-bound row: the join key, the class's members, and the shape checks.</summary>
    /// <remarks>
    /// The only place that holds the entity symbol, so all symbol work happens here and nothing
    /// symbol-shaped escapes. No lookup is attempted: the model index is a different branch of the
    /// pipeline and the two meet in <see cref="RowResolver"/>.
    /// </remarks>
    private static RowModel GetEntityBoundRowModel(
        INamedTypeSymbol type,
        string ns,
        string fqn,
        string accessibility,
        INamedTypeSymbol? entity,
        System.Threading.CancellationToken ct)
    {
        var diags = new List<DiagnosticInfo>();
        var location = type.Locations.FirstOrDefault();
        var noColumns = EquatableArray<RowColumn>.From(new List<RowColumn>());

        // typeof(Undefined), or a literal 'Entity = null'. Nothing to bind and nothing to say: the first
        // is already CS0246 on screen and the second states no type at all. The row is invalid, so no
        // partial is emitted for it and the method returning it reports SPG007.
        if (entity is null)
        {
            return new RowModel(ns, type.Name, accessibility, false, fqn, noColumns, "", null, location,
                EquatableArray<DiagnosticInfo>.From(diags));
        }

        var entityDisplay = entity.ToDisplayString();
        var declarations = type.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(ct))
            .OfType<RecordDeclarationSyntax>()
            .ToList();
        var isPartialRecord = declarations.Any(r => r.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)));
        // `is not null` and not `Parameters.Count: > 0`: an empty parameter list, `partial record R();`,
        // is still a parameter list, and the emitter is about to write a second one. Testing the count
        // let that case through and the consumer met CS8863 inside generated code instead of SPG022.
        var isPositional = declarations.Any(r => r.ParameterList is not null);

        if (!isPartialRecord)
        {
            diags.Add(new DiagnosticInfo(Diagnostics.EntityRowNotPartialRecord, location,
                new EquatableArray<string>(new[] { type.Name, entityDisplay })));
        }

        if (isPositional)
        {
            diags.Add(new DiagnosticInfo(Diagnostics.EntityRowIsPositional, location,
                new EquatableArray<string>(new[] { type.Name, entityDisplay })));
        }

        // Refused here rather than in the resolver, and with no binding attached, so that a row whose
        // shape is already wrong is never also told what its entity is missing. It also keeps the
        // resolver's identity return the only path a bindingless row can take.
        if (diags.Count > 0)
        {
            return new RowModel(ns, type.Name, accessibility, isPartialRecord, fqn, noColumns, "", null, location,
                EquatableArray<DiagnosticInfo>.From(diags));
        }

        // EF maps fields as well as properties, and it maps members a non-entity base class declares -
        // a base type EF does not know about contributes its properties to this entity type and the
        // snapshot lists them in this entity's block. Missing either would make a real property look like
        // a shadow property, and a shadow property is skipped in silence, so the row would quietly come
        // out short.
        var members = new List<EfMemberCandidate>();
        var seen = new HashSet<string>(System.StringComparer.Ordinal);
        for (var current = entity;
             current is not null && current.SpecialType != SpecialType.System_Object;
             current = current.BaseType)
        {
            foreach (var member in current.GetMembers())
            {
                if (member.IsStatic)
                    continue;
                if (member.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Public
                    && member.DeclaredAccessibility != Microsoft.CodeAnalysis.Accessibility.Internal)
                {
                    continue;
                }

                switch (member)
                {
                    case IPropertySymbol p when !p.IsIndexer && p.GetMethod is not null && seen.Add(p.Name):
                        members.Add(ClassifyMember(p.Name, p.Type));
                        break;
                    case IFieldSymbol f when !f.IsConst && !f.IsImplicitlyDeclared && seen.Add(f.Name):
                        members.Add(ClassifyMember(f.Name, f.Type));
                        break;
                }
            }
        }

        var binding = new EntityBinding(
            EfMetadataName(entity), entityDisplay, EquatableArray<EfMemberCandidate>.From(members));

        return new RowModel(ns, type.Name, accessibility, true, fqn, noColumns, "", binding, location,
            EquatableArray<DiagnosticInfo>.From(diags));
    }

    /// <summary>The name EF writes an entity type under in a snapshot.</summary>
    /// <remarks>
    /// Namespace, then the containing types outermost-first joined with <c>'+'</c>, then the name -
    /// <c>Recon.Outer+Nested</c>. Built by hand rather than taken from a display format on purpose:
    /// <c>FullyQualifiedFormat</c> and <c>ToDisplayString()</c> both write <c>Outer.Nested</c>, which
    /// would silently never match and would surface as SPG028 pointing at the developer's spelling
    /// rather than at ours.
    /// </remarks>
    private static string EfMetadataName(INamedTypeSymbol type)
    {
        var containing = new List<string>();
        for (var outer = type.ContainingType; outer is not null; outer = outer.ContainingType)
            containing.Add(outer.Name);
        containing.Reverse();

        var sb = new StringBuilder();
        if (!type.ContainingNamespace.IsGlobalNamespace)
        {
            sb.Append(type.ContainingNamespace.ToDisplayString());
            sb.Append('.');
        }

        foreach (var name in containing)
        {
            sb.Append(name);
            sb.Append('+');
        }

        sb.Append(type.Name);
        return sb.ToString();
    }

    private static void Emit(SourceProductionContext spc, ImmutableArray<ResolvedMethod> models, ImmutableArray<RowModel> rows, string? baseNamespace)
    {
        foreach (var d in models.SelectMany(m => m.Diagnostics.AsArray()))
            spc.ReportDiagnostic(d.ToDiagnostic());
        foreach (var d in rows.SelectMany(r => r.Diagnostics.AsArray()))
            spc.ReportDiagnostic(d.ToDiagnostic());

        // Emit row partials and index them by fully-qualified name for method result-set wiring.
        var rowMap = new Dictionary<string, RowModel>();
        foreach (var row in rows)
        {
            rowMap[row.Fqn] = row;
            if (row.IsValid)
                spc.AddSource((string.IsNullOrEmpty(row.Namespace) ? "" : row.Namespace + ".") + row.TypeName + ".Row.g.cs", EmitRow(row));
        }

        if (models.IsDefaultOrEmpty)
            return;

        var groups = models.GroupBy(m => (m.Namespace, m.ClassName, m.ClassAccessibility, m.ClassIsPartial));
        var registryEntries = new List<string>();

        foreach (var group in groups)
        {
            var (ns, className, accessibility, isPartial) = group.Key;

            if (!isPartial)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.GroupNotPartial, null, className));
                continue;
            }

            var methodsInGroup = group.ToList();
            var source = EmitClass(ns, className, accessibility, methodsInGroup, rowMap, spc);
            var hint = (string.IsNullOrEmpty(ns) ? "" : ns + ".") + className + ".g.cs";
            spc.AddSource(hint, source);

            foreach (var m in methodsInGroup.Where(m => IsMethodValid(m, rowMap)))
                registryEntries.Add($"{Fqn(ns, className)}.{m.ContractFieldName}");
        }

        if (registryEntries.Count > 0)
            spc.AddSource("GeneratedProcedureRegistry.g.cs", EmitRegistry(baseNamespace, registryEntries));
    }

    private static bool IsMethodValid(ResolvedMethod m, Dictionary<string, RowModel> rowMap)
    {
        if (!m.IsValid)
            return false;
        if (m.Return != ReturnCategory.ResultSet)
            return true;
        return m.ResultRowFqn is not null && rowMap.TryGetValue(m.ResultRowFqn, out var row) && row.IsValid;
    }

    private static string EmitClass(string ns, string className, string accessibility, List<ResolvedMethod> methods, Dictionary<string, RowModel> rowMap, SourceProductionContext spc)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        var indent = "";
        if (!string.IsNullOrEmpty(ns))
        {
            sb.AppendLine($"namespace {ns}");
            sb.AppendLine("{");
            indent = "    ";
        }

        sb.AppendLine($"{indent}{accessibility} partial class {className}");
        sb.AppendLine($"{indent}{{");

        var i = indent + "    ";
        // Constructor + injected fields.
        sb.AppendLine($"{i}private readonly {ExecutionNs}.ISqlConnectionProvider __connectionProvider;");
        sb.AppendLine($"{i}private readonly {ExecutionNs}.IStoredProcedureExecutor __executor;");
        sb.AppendLine();
        sb.AppendLine($"{i}public {className}({ExecutionNs}.ISqlConnectionProvider connectionProvider, {ExecutionNs}.IStoredProcedureExecutor executor)");
        sb.AppendLine($"{i}{{");
        sb.AppendLine($"{i}    __connectionProvider = connectionProvider;");
        sb.AppendLine($"{i}    __executor = executor;");
        sb.AppendLine($"{i}}}");

        foreach (var m in methods)
        {
            sb.AppendLine();
            EmitMethod(sb, i, m, rowMap, spc);
        }

        sb.AppendLine($"{indent}}}");
        if (!string.IsNullOrEmpty(ns))
            sb.AppendLine("}");
        return sb.ToString();
    }

    private static void EmitMethod(StringBuilder sb, string i, ResolvedMethod m, Dictionary<string, RowModel> rowMap, SourceProductionContext spc)
    {
        var sigParams = string.Join(", ", m.SignatureParams.AsArray().Select(p => $"{p.TypeText} {p.Name}"));

        RowModel? row = null;
        if (m.Return == ReturnCategory.ResultSet)
        {
            var found = m.ResultRowFqn is not null && rowMap.TryGetValue(m.ResultRowFqn, out row);

            // SPG007 says the row type is not a partial record marked [SqlRow]. When the type WAS found
            // and was refused, that sentence is false - it is marked, and the refusal already reported
            // why, on the row itself. Repeating SPG007 there put one misleading error beside every
            // accurate one, so it is reported only when the type genuinely could not be found.
            if (!found)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.RowTypeNotFound, null, m.MethodName, m.ResultRowFqn ?? "?"));
            }
            else if (row is not null && !row.IsValid)
            {
                row = null;
            }
        }

        if (!IsMethodValid(m, rowMap))
        {
            // Satisfy the partial declaration so the rest of the file compiles; diagnostics explain the problem.
            sb.AppendLine($"{i}{m.MethodAccessibility} partial {m.ReturnTypeText} {m.MethodName}({sigParams})");
            sb.AppendLine($"{i}    => throw new global::System.NotSupportedException(\"Source generation skipped due to a SPGxxx diagnostic on '{m.MethodName}'.\");");
            return;
        }

        var spArray = m.SpParams.AsArray();

        // Contract field.
        sb.AppendLine($"{i}internal static readonly {ContractsNs}.IStoredProcedureContract {m.ContractFieldName} =");
        sb.AppendLine($"{i}    new {ContractsNs}.GeneratedContract(");
        sb.AppendLine($"{i}        \"{m.Schema}\", \"{m.ProcName}\",");
        if (spArray.Length == 0)
        {
            sb.AppendLine($"{i}        global::System.Array.Empty<{ContractsNs}.ProcParamSpec>(),");
        }
        else
        {
            sb.AppendLine($"{i}        new {ContractsNs}.ProcParamSpec[]");
            sb.AppendLine($"{i}        {{");
            foreach (var p in spArray)
                sb.AppendLine($"{i}            {p.SqlTypeExpression},");
            sb.AppendLine($"{i}        }},");
        }
        sb.AppendLine($"{i}        new {ContractsNs}.ProcReturnSpec({ContractsNs}.ReturnKind.ReturnValue, new {ContractsNs}.SqlTypeSpec(\"int\")),");
        if (row is null)
        {
            sb.AppendLine($"{i}        null);");
        }
        else
        {
            sb.AppendLine($"{i}        new {ContractsNs}.ColumnSpec[]");
            sb.AppendLine($"{i}        {{");
            foreach (var c in row.Columns.AsArray())
                sb.AppendLine($"{i}            {c.ColumnSpecExpression},");
            sb.AppendLine($"{i}        }});");
        }
        sb.AppendLine();

        // Method body.
        var ctArg = m.CancellationTokenParamName ?? "default";
        var argList = string.Join(", ", spArray.Select(p => p.CSharpName));

        sb.AppendLine($"{i}{m.MethodAccessibility} partial async {m.ReturnTypeText} {m.MethodName}({sigParams})");
        sb.AppendLine($"{i}{{");
        sb.AppendLine($"{i}    await using var __conn = __connectionProvider.Create();");
        sb.AppendLine($"{i}    await __conn.OpenAsync({ctArg}).ConfigureAwait(false);");

        if (m.Return == ReturnCategory.ResultSet)
        {
            var call = $"await __executor.ExecuteReturnResultSetAsync<{m.ResultRowFqn}>(__conn, {m.ContractFieldName}, new object?[] {{ {argList} }}, {ctArg}).ConfigureAwait(false)";
            if (m.ReturnsTuple)
                sb.AppendLine($"{i}    return {call};");
            else
                sb.AppendLine($"{i}    return ({call}).Rows;");
        }
        else if (m.Return == ReturnCategory.NonQuery)
        {
            sb.AppendLine($"{i}    await __executor.ExecuteNonQueryAsync(__conn, {m.ContractFieldName}, new object?[] {{ {argList} }}, {ctArg}).ConfigureAwait(false);");
        }
        else if (m.Return == ReturnCategory.ReturnWithOutputs)
        {
            sb.AppendLine($"{i}    var (__ret, __outs) = await __executor.ExecuteWithOutputsAsync(__conn, {m.ContractFieldName}, new object?[] {{ {argList} }}, {ctArg}).ConfigureAwait(false);");
            var tupleElements = new List<string> { "__ret" };
            tupleElements.AddRange(m.OutputConversions.AsArray());
            sb.AppendLine($"{i}    return ({string.Join(", ", tupleElements)});");
        }
        else
        {
            var execMethod = m.Return == ReturnCategory.Int16 ? "ExecuteReturnInt16Async" : "ExecuteReturnInt32Async";
            sb.AppendLine($"{i}    return await __executor.{execMethod}(__conn, {m.ContractFieldName}, new object?[] {{ {argList} }}, {ctArg}).ConfigureAwait(false);");
        }

        sb.AppendLine($"{i}}}");
    }

    private static string EmitRegistry(string? baseNamespace, List<string> entries)
    {
        var ns = string.IsNullOrEmpty(baseNamespace) ? "Generated" : baseNamespace + ".Generated";
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine();
        sb.AppendLine($"namespace {ns}");
        sb.AppendLine("{");
        sb.AppendLine("    /// <summary>All source-generated stored-procedure contracts in this assembly.</summary>");
        sb.AppendLine("    public static class GeneratedProcedureRegistry");
        sb.AppendLine("    {");
        sb.AppendLine($"        public static global::System.Collections.Generic.IReadOnlyList<{ContractsNs}.IStoredProcedureContract> All {{ get; }} =");
        sb.AppendLine($"            new {ContractsNs}.IStoredProcedureContract[]");
        sb.AppendLine("            {");
        foreach (var e in entries)
            sb.AppendLine($"                {e},");
        sb.AppendLine("            };");
        sb.AppendLine("    }");
        sb.AppendLine("}");
        return sb.ToString();
    }

    private static string EmitRow(RowModel row)
    {
        var sb = new StringBuilder();
        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable disable");
        sb.AppendLine();

        var indent = "";
        if (!string.IsNullOrEmpty(row.Namespace))
        {
            sb.AppendLine($"namespace {row.Namespace}");
            sb.AppendLine("{");
            indent = "    ";
        }

        // ParameterListText is empty on the positional path, where the developer wrote the primary
        // constructor themselves, so this line produces the identical token sequence it produced before
        // the Entity binding existed. It is the emitter's only change.
        sb.AppendLine($"{indent}partial record {row.TypeName}{row.ParameterListText} : {ExecutionNs}.IFromDataRecord<{row.TypeName}>");
        sb.AppendLine($"{indent}{{");
        var i = indent + "    ";
        sb.AppendLine($"{i}public static {row.TypeName} FromDataRecord(global::System.Data.IDataRecord record)");
        sb.AppendLine($"{i}{{");
        var cols = row.Columns.AsArray();
        if (cols.Length == 0)
        {
            // Unreachable on the positional path, where a record with no parameters is not selected at
            // all, and reachable on the Entity path only when every model property was a shadow property.
            sb.AppendLine($"{i}    return new {row.TypeName}();");
        }
        else
        {
            sb.AppendLine($"{i}    return new {row.TypeName}(");
            for (var idx = 0; idx < cols.Length; idx++)
            {
                var sep = idx == cols.Length - 1 ? ");" : ",";
                sb.AppendLine($"{i}        {cols[idx].ReadExpression}{sep}");
            }
        }
        sb.AppendLine($"{i}}}");
        sb.AppendLine($"{indent}}}");
        if (!string.IsNullOrEmpty(row.Namespace))
            sb.AppendLine("}");
        return sb.ToString();
    }

    // ---- helpers ----

    private readonly record struct ReturnInfo(ReturnCategory Category, string Text, string? RowFqn, bool ReturnsTuple, string[] OutputConversions);

    private static readonly string[] NoConversions = System.Array.Empty<string>();

    private static ReturnInfo ClassifyReturn(ITypeSymbol returnType)
    {
        var text = returnType.ToDisplayString(TypeFmt);
        if (returnType is not INamedTypeSymbol named
            || named.Name != "Task"
            || named.ContainingNamespace.ToDisplayString() != "System.Threading.Tasks")
        {
            return new ReturnInfo(ReturnCategory.Unsupported, text, null, false, NoConversions);
        }

        // Non-generic Task -> non-query procedure (no RETURN / result set consumed).
        if (named.TypeArguments.Length == 0)
            return new ReturnInfo(ReturnCategory.NonQuery, text, null, false, NoConversions);
        if (named.TypeArguments.Length != 1)
            return new ReturnInfo(ReturnCategory.Unsupported, text, null, false, NoConversions);

        var arg = named.TypeArguments[0];

        switch (arg.SpecialType)
        {
            case SpecialType.System_Int32: return new ReturnInfo(ReturnCategory.Int32, text, null, false, NoConversions);
            case SpecialType.System_Int16: return new ReturnInfo(ReturnCategory.Int16, text, null, false, NoConversions);
        }

        // Task<IReadOnlyList<TRow>>
        if (TryGetRowType(arg, out var rowFqn))
            return new ReturnInfo(ReturnCategory.ResultSet, text, rowFqn, false, NoConversions);

        if (arg is INamedTypeSymbol tuple && tuple.IsTupleType
            && tuple.TupleElements.Length >= 2
            && tuple.TupleElements[0].Type.SpecialType == SpecialType.System_Int32)
        {
            var elems = tuple.TupleElements;

            // Task<(int ReturnValue, IReadOnlyList<TRow> Rows)>
            if (elems.Length == 2 && TryGetRowType(elems[1].Type, out var tupleRowFqn))
                return new ReturnInfo(ReturnCategory.ResultSet, text, tupleRowFqn, true, NoConversions);

            // Task<(int ReturnValue, <out0>, <out1>, ...)>
            var conversions = new List<string>();
            var ok = true;
            for (var k = 1; k < elems.Length; k++)
            {
                var conv = BuildOutputConversion(elems[k].Type, k - 1);
                if (conv is null) { ok = false; break; }
                conversions.Add(conv);
            }
            if (ok)
                return new ReturnInfo(ReturnCategory.ReturnWithOutputs, text, null, true, conversions.ToArray());
        }

        return new ReturnInfo(ReturnCategory.Unsupported, text, null, false, NoConversions);
    }

    /// <summary>Builds the cast/convert expression that reads OUTPUT value at __outs[index] as the given tuple-element type; null if unsupported.</summary>
    private static string? BuildOutputConversion(ITypeSymbol type, int index)
    {
        var src = $"__outs[{index}]";

        var t = type;
        var nullableValue = false;
        if (t is INamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            nullableValue = true;
            t = n.TypeArguments[0];
        }

        if (t is IArrayTypeSymbol arr && arr.ElementType.SpecialType == SpecialType.System_Byte)
            return $"{src} as byte[]";

        var supported = t.SpecialType is SpecialType.System_Int32 or SpecialType.System_Int16
            or SpecialType.System_Byte or SpecialType.System_Int64 or SpecialType.System_Boolean
            or SpecialType.System_Decimal or SpecialType.System_Single or SpecialType.System_Double
            or SpecialType.System_DateTime or SpecialType.System_String;
        if (!supported && t.ToDisplayString() != "System.Guid")
            return null;

        if (t.IsReferenceType)
            return $"{src} as {t.ToDisplayString(TypeFmtNonNullable)}";

        var elemText = t.ToDisplayString(TypeFmt);
        if (nullableValue)
            return $"{src} is null ? ({elemText}?)null : ({elemText}){src}!";
        return $"({elemText}){src}!";
    }

    private static bool TryGetRowType(ITypeSymbol type, out string rowFqn)
    {
        if (type is INamedTypeSymbol list
            && list.Name == "IReadOnlyList"
            && list.ContainingNamespace.ToDisplayString() == "System.Collections.Generic"
            && list.TypeArguments.Length == 1)
        {
            rowFqn = list.TypeArguments[0].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
            return true;
        }
        rowFqn = "";
        return false;
    }

    /// <summary>Reads what the developer actually wrote in a <c>[Sql]</c> attribute.</summary>
    /// <remarks>
    /// Every facet is nullable, and null means "not stated" - the one question the merge has to be able
    /// to ask. It is answered from the <see cref="AttributeData"/>, never from a materialised attribute
    /// object: the constructor arity says which overload was used and <c>NamedArguments</c> holds only
    /// what was written, whereas the object cannot tell an unstated <c>Length</c> from a written 0.
    /// </remarks>
    private static (string? SqlName, string? TypeName, int? Length, byte? Precision, byte? Scale, bool? Output) ParseSqlAttribute(AttributeData attr)
    {
        var ctor = attr.ConstructorArguments;
        var name = ctor.Length > 0 ? ctor[0].Value as string ?? "" : null;
        string? typeName = null;
        int? length = null;

        if (ctor.Length == 2)
        {
            if (ctor[1].Type?.SpecialType == SpecialType.System_Int32)
                length = ctor[1].Value is int l ? l : null;
            else
                typeName = ctor[1].Value as string;
        }
        else if (ctor.Length == 3)
        {
            typeName = ctor[1].Value as string;
            length = ctor[2].Value is int l ? l : null;
        }

        byte? precision = null, scale = null;
        bool? output = null;
        foreach (var na in attr.NamedArguments)
        {
            switch (na.Key)
            {
                case "Name": name = na.Value.Value as string ?? ""; break;
                case "Precision": precision = ToByte(na.Value.Value); break;
                case "Scale": scale = ToByte(na.Value.Value); break;
                case "Output": output = na.Value.Value is bool b && b; break;
                case "TypeName": typeName = na.Value.Value as string; break;
                case "Length": length = na.Value.Value is int l ? l : length; break;
            }
        }

        return (name, typeName, length, precision, scale, output);
    }

    private static byte ToByte(object? value) => value switch
    {
        byte b => b,
        int i => (byte)i,
        _ => 0,
    };

    /// <summary>Classifies a parameter's .NET type once, for both SQL-type inference and SPG013.</summary>
    /// <remarks>
    /// The single CLR classification in the generator. A nullable value type is classified as the type it
    /// wraps, because nullability is a property of the value, not of the SQL type.
    /// </remarks>
    private static ClrKind ClassifyClr(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            type = n.TypeArguments[0];

        switch (type.SpecialType)
        {
            case SpecialType.System_String: return ClrKind.String;
            case SpecialType.System_Int32: return ClrKind.Int32;
            case SpecialType.System_Int16: return ClrKind.Int16;
            case SpecialType.System_Byte: return ClrKind.Byte;
            case SpecialType.System_Int64: return ClrKind.Int64;
            case SpecialType.System_Boolean: return ClrKind.Boolean;
            case SpecialType.System_Decimal: return ClrKind.Decimal;
            case SpecialType.System_Single: return ClrKind.Single;
            case SpecialType.System_Double: return ClrKind.Double;
            case SpecialType.System_DateTime: return ClrKind.DateTime;
        }

        if (type is IArrayTypeSymbol arr && arr.ElementType.SpecialType == SpecialType.System_Byte)
            return ClrKind.ByteArray;
        if (type.ToDisplayString() == "System.Guid")
            return ClrKind.Guid;
        return ClrKind.Other;
    }

    /// <summary>The SQL type a .NET type implies - the last of the three sources, after [Sql] and the .sql.</summary>
    private static string? InferSqlType(ClrKind clr) => clr switch
    {
        ClrKind.String => "varchar",
        ClrKind.Int32 => "int",
        ClrKind.Int16 => "smallint",
        ClrKind.Byte => "tinyint",
        ClrKind.Int64 => "bigint",
        ClrKind.Boolean => "bit",
        ClrKind.Decimal => "decimal",
        ClrKind.Single => "real",
        ClrKind.Double => "float",
        ClrKind.DateTime => "datetime",
        ClrKind.ByteArray => "varbinary",
        ClrKind.Guid => "uniqueidentifier",
        _ => null,
    };

    /// <summary>Classifies one member the way a result-set column has to be read.</summary>
    /// <remarks>
    /// The single member classification in this generator, shared by the positional path and the Entity
    /// path so the two cannot drift into emitting different text for the same shape. It answers four
    /// questions at once - the getter, the byte[] case, the unwrapped element type and whether the member
    /// is a nullable value type - because those four are what both callers need and computing them apart
    /// is what would let them disagree.
    /// <para>
    /// <see cref="EfMemberCandidate.ElementTypeText"/> is written with <see cref="TypeFmtNonNullable"/>,
    /// which is also the format <c>EfSnapshotReader.StoreTypeFormat</c> writes a <c>Property&lt;T&gt;</c>
    /// argument with. The two strings are compared to each other during binding, so a format difference
    /// between them would read as a disagreement rather than as a bug.
    /// </para>
    /// </remarks>
    internal static EfMemberCandidate ClassifyMember(string name, ITypeSymbol type)
    {
        var isNullableValue = false;
        var t = type;
        if (t is INamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            isNullableValue = true;
            t = n.TypeArguments[0];
        }

        // byte[] (binary/varbinary/image), which has no getter and is read through GetValue.
        if (t is IArrayTypeSymbol arr && arr.ElementType.SpecialType == SpecialType.System_Byte)
            return new EfMemberCandidate(name, "byte[]", false, true, true, null);

        var getter = t.SpecialType switch
        {
            SpecialType.System_Int32 => "GetInt32",
            SpecialType.System_Int16 => "GetInt16",
            SpecialType.System_Byte => "GetByte",
            SpecialType.System_Int64 => "GetInt64",
            SpecialType.System_Boolean => "GetBoolean",
            SpecialType.System_Decimal => "GetDecimal",
            SpecialType.System_Single => "GetFloat",
            SpecialType.System_Double => "GetDouble",
            SpecialType.System_DateTime => "GetDateTime",
            SpecialType.System_String => "GetString",
            _ => null,
        };

        if (getter is null && t.ToDisplayString() == "System.Guid")
            getter = "GetGuid";

        return new EfMemberCandidate(
            name, t.ToDisplayString(TypeFmtNonNullable), isNullableValue, t.IsReferenceType, false, getter);
    }

    /// <summary>Builds an IDataRecord read expression for a row member; returns null when the type has no supported reader. Out param is the typeof() text for the ColumnSpec.</summary>
    private static string? BuildReadExpression(ITypeSymbol type, string columnName, out string specTypeText) =>
        BuildReadExpression(ClassifyMember("", type), columnName, out specTypeText);

    /// <summary>The four read shapes, rendered from a classified member.</summary>
    /// <remarks>
    /// <para>
    /// The Entity path calls this with the same candidates the positional path uses, so both emit the
    /// same text for the same shape by construction rather than by review.
    /// </para>
    /// <para>
    /// <paramref name="specTypeText"/> comes from the NON-nullable format, and that is a fix rather than
    /// a detail: the ColumnSpec renders it inside typeof(), where an annotated reference type is CS8639 -
    /// "the typeof operator cannot be used on a nullable reference type" - so <c>[SqlRow] record R(string?
    /// Name)</c> emitted code that did not compile. The two formats are identical for every value type and
    /// for an unannotated reference type, so the only text that moves is <c>string?</c> to <c>string</c>,
    /// and every declaration that produced it was already an error. The nullable-value cast below keeps
    /// its own spelling, where the '?' is required.
    /// </para>
    /// </remarks>
    internal static string? BuildReadExpression(EfMemberCandidate member, string columnName, out string specTypeText)
    {
        specTypeText = "";
        var ordinal = $"record.GetOrdinal(\"{columnName}\")";

        if (member.IsByteArray)
        {
            specTypeText = member.ElementTypeText;
            return $"record.IsDBNull({ordinal}) ? null : (byte[])record.GetValue({ordinal})";
        }

        if (member.Getter is null)
            return null;

        var elementTypeText = member.ElementTypeText;
        specTypeText = member.IsNullableValue ? elementTypeText + "?" : elementTypeText;

        if (member.IsNullableValue)
            return $"record.IsDBNull({ordinal}) ? ({elementTypeText}?)null : record.{member.Getter}({ordinal})";
        if (member.IsReferenceType)
            return $"record.IsDBNull({ordinal}) ? null : record.{member.Getter}({ordinal})";
        return $"record.{member.Getter}({ordinal})";
    }

    private static bool IsCancellationToken(ITypeSymbol type) =>
        type.Name == "CancellationToken" && type.ContainingNamespace?.ToDisplayString() == "System.Threading";

    private static (string Schema, string Name) SplitProcName(string fullName)
    {
        var cleaned = fullName.Replace("[", "").Replace("]", "").Trim();
        var dot = cleaned.IndexOf('.');
        return dot < 0 ? ("dbo", cleaned) : (cleaned.Substring(0, dot), cleaned.Substring(dot + 1));
    }

    private static string Fqn(string ns, string className) =>
        string.IsNullOrEmpty(ns) ? $"global::{className}" : $"global::{ns}.{className}";

    private static string Accessibility(Accessibility a) => a switch
    {
        Microsoft.CodeAnalysis.Accessibility.Public => "public",
        Microsoft.CodeAnalysis.Accessibility.Internal => "internal",
        Microsoft.CodeAnalysis.Accessibility.Protected => "protected",
        Microsoft.CodeAnalysis.Accessibility.ProtectedOrInternal => "protected internal",
        Microsoft.CodeAnalysis.Accessibility.ProtectedAndInternal => "private protected",
        Microsoft.CodeAnalysis.Accessibility.Private => "private",
        _ => "internal",
    };
}
