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
    private const string ContractsNs = "global::Raptor21.EF.Extensions.StoredProcedures.Contracts";
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

        var combined = methods.Collect()
            .Combine(rows.Collect())
            .Combine(rootNamespace)
            .Combine(assemblyName);

        context.RegisterSourceOutput(combined, static (spc, data) =>
            Emit(spc, data.Left.Left.Left, data.Left.Left.Right, data.Left.Right ?? data.Right));
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

        // Parameters
        var sig = new List<SigParam>();
        var spParams = new List<SpParam>();
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
            if (sqlAttr is null)
            {
                diags.Add(new DiagnosticInfo(Diagnostics.ParamMissingSql, p.Locations.FirstOrDefault(),
                    new EquatableArray<string>(new[] { p.Name, method.Name })));
                continue;
            }

            var (sqlName, typeName, length, precision, scale, output) = ParseSqlAttribute(sqlAttr);
            if (string.IsNullOrWhiteSpace(sqlName) || !sqlName.StartsWith("@"))
            {
                diags.Add(new DiagnosticInfo(Diagnostics.InvalidSqlName, p.Locations.FirstOrDefault(),
                    new EquatableArray<string>(new[] { p.Name, method.Name, sqlName ?? "" })));
                continue;
            }

            var sqlTypeName = typeName ?? InferSqlType(p.Type);
            if (sqlTypeName is null)
            {
                diags.Add(new DiagnosticInfo(Diagnostics.UnsupportedParamType, p.Locations.FirstOrDefault(),
                    new EquatableArray<string>(new[] { p.Name, method.Name, p.Type.ToDisplayString() })));
                continue;
            }

            var typeExpr = RenderSqlType(sqlTypeName, length, precision, scale);
            var paramExpr = $"new {ContractsNs}.ProcParamSpec(\"{sqlName}\", {typeExpr}{(output ? ", true" : "")})";
            spParams.Add(new SpParam(p.Name, paramExpr, output));
        }

        // OUTPUT parameters must line up (count + order) with the post-RETURN tuple elements.
        if (returnInfo.Category == ReturnCategory.ReturnWithOutputs)
        {
            var outputCount = spParams.Count(s => s.IsOutput);
            if (outputCount != returnInfo.OutputConversions.Length)
                diags.Add(new DiagnosticInfo(Diagnostics.OutputArityMismatch, method.Locations.FirstOrDefault(),
                    new EquatableArray<string>(new[] { method.Name, returnInfo.OutputConversions.Length.ToString(), outputCount.ToString() })));
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
            SpParams: EquatableArray<SpParam>.From(spParams),
            Schema: schema,
            ProcName: procName,
            ContractFieldName: "__Contract_" + method.Name,
            ResultRowFqn: returnInfo.RowFqn,
            ReturnsTuple: returnInfo.ReturnsTuple,
            OutputConversions: EquatableArray<string>.From(returnInfo.OutputConversions),
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

        var recordDecl = type.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(ct))
            .OfType<RecordDeclarationSyntax>()
            .FirstOrDefault(r => r.ParameterList is { Parameters.Count: > 0 });
        var isPartial = type.DeclaringSyntaxReferences
            .Select(r => r.GetSyntax(ct))
            .OfType<RecordDeclarationSyntax>()
            .Any(r => r.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword)));

        if (recordDecl is null || !isPartial)
        {
            diags.Add(new DiagnosticInfo(Diagnostics.RowNotPositionalPartialRecord, type.Locations.FirstOrDefault(),
                new EquatableArray<string>(new[] { type.Name })));
            return new RowModel(ns, type.Name, accessibility, false, fqn,
                EquatableArray<RowColumn>.From(new List<RowColumn>()), EquatableArray<DiagnosticInfo>.From(diags));
        }

        var cols = new List<RowColumn>();
        foreach (var p in recordDecl.ParameterList!.Parameters)
        {
            if (ctx.SemanticModel.GetDeclaredSymbol(p, ct) is not IParameterSymbol sym)
                continue;

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
            EquatableArray<RowColumn>.From(cols), EquatableArray<DiagnosticInfo>.From(diags));
    }

    private static void Emit(SourceProductionContext spc, ImmutableArray<MethodModel> models, ImmutableArray<RowModel> rows, string? baseNamespace)
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

    private static bool IsMethodValid(MethodModel m, Dictionary<string, RowModel> rowMap)
    {
        if (!m.IsSelfValid)
            return false;
        if (m.Return != ReturnCategory.ResultSet)
            return true;
        return m.ResultRowFqn is not null && rowMap.TryGetValue(m.ResultRowFqn, out var row) && row.IsValid;
    }

    private static string EmitClass(string ns, string className, string accessibility, List<MethodModel> methods, Dictionary<string, RowModel> rowMap, SourceProductionContext spc)
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

    private static void EmitMethod(StringBuilder sb, string i, MethodModel m, Dictionary<string, RowModel> rowMap, SourceProductionContext spc)
    {
        var sigParams = string.Join(", ", m.SignatureParams.AsArray().Select(p => $"{p.TypeText} {p.Name}"));

        RowModel? row = null;
        if (m.Return == ReturnCategory.ResultSet)
        {
            if (m.ResultRowFqn is null || !rowMap.TryGetValue(m.ResultRowFqn, out row) || !row.IsValid)
            {
                spc.ReportDiagnostic(Diagnostic.Create(Diagnostics.RowTypeNotFound, null, m.MethodName, m.ResultRowFqn ?? "?"));
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

        sb.AppendLine($"{indent}partial record {row.TypeName} : {ExecutionNs}.IFromDataRecord<{row.TypeName}>");
        sb.AppendLine($"{indent}{{");
        var i = indent + "    ";
        sb.AppendLine($"{i}public static {row.TypeName} FromDataRecord(global::System.Data.IDataRecord record)");
        sb.AppendLine($"{i}{{");
        sb.AppendLine($"{i}    return new {row.TypeName}(");
        var cols = row.Columns.AsArray();
        for (var idx = 0; idx < cols.Length; idx++)
        {
            var sep = idx == cols.Length - 1 ? ");" : ",";
            sb.AppendLine($"{i}        {cols[idx].ReadExpression}{sep}");
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

    private static (string SqlName, string? TypeName, int Length, byte Precision, byte Scale, bool Output) ParseSqlAttribute(AttributeData attr)
    {
        var ctor = attr.ConstructorArguments;
        var name = ctor.Length > 0 ? ctor[0].Value as string ?? "" : "";
        string? typeName = null;
        var length = -1;

        if (ctor.Length == 2)
        {
            if (ctor[1].Type?.SpecialType == SpecialType.System_Int32)
                length = ctor[1].Value is int l ? l : -1;
            else
                typeName = ctor[1].Value as string;
        }
        else if (ctor.Length == 3)
        {
            typeName = ctor[1].Value as string;
            length = ctor[2].Value is int l ? l : -1;
        }

        byte precision = 0, scale = 0;
        var output = false;
        foreach (var na in attr.NamedArguments)
        {
            switch (na.Key)
            {
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

    private static string? InferSqlType(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
            type = n.TypeArguments[0];

        switch (type.SpecialType)
        {
            case SpecialType.System_String: return "varchar";
            case SpecialType.System_Int32: return "int";
            case SpecialType.System_Int16: return "smallint";
            case SpecialType.System_Byte: return "tinyint";
            case SpecialType.System_Int64: return "bigint";
            case SpecialType.System_Boolean: return "bit";
            case SpecialType.System_Decimal: return "decimal";
            case SpecialType.System_Single: return "real";
            case SpecialType.System_Double: return "float";
            case SpecialType.System_DateTime: return "datetime";
        }

        if (type is IArrayTypeSymbol arr && arr.ElementType.SpecialType == SpecialType.System_Byte)
            return "varbinary";
        if (type.ToDisplayString() == "System.Guid")
            return "uniqueidentifier";
        return null;
    }

    /// <summary>Builds an IDataRecord read expression for a row member; returns null when the type has no supported reader. Out param is the typeof() text for the ColumnSpec.</summary>
    private static string? BuildReadExpression(ITypeSymbol type, string columnName, out string specTypeText)
    {
        specTypeText = "";
        var ordinal = $"record.GetOrdinal(\"{columnName}\")";

        var isNullableValue = false;
        var t = type;
        if (t is INamedTypeSymbol n && n.OriginalDefinition.SpecialType == SpecialType.System_Nullable_T)
        {
            isNullableValue = true;
            t = n.TypeArguments[0];
        }

        // byte[] (binary/varbinary/image)
        if (t is IArrayTypeSymbol arr && arr.ElementType.SpecialType == SpecialType.System_Byte)
        {
            specTypeText = "byte[]";
            return $"record.IsDBNull({ordinal}) ? null : (byte[])record.GetValue({ordinal})";
        }

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
        if (getter is null)
            return null;

        var elementTypeText = t.ToDisplayString(TypeFmt);
        specTypeText = isNullableValue ? elementTypeText + "?" : elementTypeText;

        if (isNullableValue)
            return $"record.IsDBNull({ordinal}) ? ({elementTypeText}?)null : record.{getter}({ordinal})";
        if (t.IsReferenceType)
            return $"record.IsDBNull({ordinal}) ? null : record.{getter}({ordinal})";
        return $"record.{getter}({ordinal})";
    }

    private static string RenderSqlType(string sqlTypeName, int length, byte precision, byte scale)
    {
        if (precision > 0)
            return $"new {ContractsNs}.SqlTypeSpec(\"{sqlTypeName}\", null, (byte){precision}, (byte){scale})";
        if (length >= 0)
            return $"new {ContractsNs}.SqlTypeSpec(\"{sqlTypeName}\", {length})";
        return $"new {ContractsNs}.SqlTypeSpec(\"{sqlTypeName}\")";
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
