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

/// <summary>A diagnostic to be reported during the emit phase (carries its own location).</summary>
internal sealed record DiagnosticInfo(DiagnosticDescriptor Descriptor, Location? Location, EquatableArray<string> Args)
{
    public Diagnostic ToDiagnostic() => Diagnostic.Create(Descriptor, Location, Args.AsArray());
}

/// <summary>A single stored-procedure parameter (the typed call argument that maps to a SQL parameter).</summary>
internal sealed record SpParam(string CSharpName, string SqlTypeExpression, bool IsOutput);

/// <summary>A parameter as it appears in the partial method signature (reproduced verbatim).</summary>
internal sealed record SigParam(string TypeText, string Name);

/// <summary>Everything needed to emit one stored-procedure group method + its contract.</summary>
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
    EquatableArray<SpParam> SpParams,
    string Schema,
    string ProcName,
    string ContractFieldName,
    string? ResultRowFqn,
    bool ReturnsTuple,
    EquatableArray<string> OutputConversions,
    EquatableArray<DiagnosticInfo> Diagnostics)
{
    public bool IsSelfValid => Return != ReturnCategory.Unsupported
        && !Diagnostics.AsArray().Any(d => d.Descriptor.DefaultSeverity == DiagnosticSeverity.Error);
}

/// <summary>A single result-row member mapped to a SQL result-set column.</summary>
internal sealed record RowColumn(string ColumnName, string ColumnSpecExpression, string ReadExpression);

/// <summary>A [SqlRow] positional record for which FromDataRecord is generated.</summary>
internal sealed record RowModel(
    string Namespace,
    string TypeName,
    string Accessibility,
    bool IsPartialRecord,
    string Fqn,
    EquatableArray<RowColumn> Columns,
    EquatableArray<DiagnosticInfo> Diagnostics)
{
    public bool IsValid => IsPartialRecord
        && !Diagnostics.AsArray().Any(d => d.Descriptor.DefaultSeverity == DiagnosticSeverity.Error);
}
