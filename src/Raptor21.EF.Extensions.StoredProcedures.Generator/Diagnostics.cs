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
        messageFormat: "Method '{0}' return type is not supported (Faz 1 supports Task<int> and Task<short>)",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public static readonly DiagnosticDescriptor ParamMissingSql = new(
        id: "SPG003",
        title: "Parameter is missing the [Sql] attribute",
        messageFormat: "Parameter '{0}' of method '{1}' must have a [Sql(\"@name\", ...)] attribute",
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

    public static readonly DiagnosticDescriptor UnsupportedParamType = new(
        id: "SPG005",
        title: "Cannot infer SQL type for parameter",
        messageFormat: "Parameter '{0}' of method '{1}' has .NET type '{2}' with no inferable SQL type; specify it via [Sql(\"@name\", \"sqltype\", length)]",
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

    public static readonly DiagnosticDescriptor OutputArityMismatch = new(
        id: "SPG009",
        title: "Return tuple does not match OUTPUT parameters",
        messageFormat: "Method '{0}' returns {1} value(s) after the RETURN element but declares {2} [Sql(Output = true)] parameter(s); they must match in count and order",
        category: Category,
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);
}
