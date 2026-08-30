namespace Raptor21.EF.Extensions.StoredProcedures.Contracts;

/// <summary>Type-safe contract for a stored procedure (no reflection; all data provided explicitly).</summary>
public interface IStoredProcedureContract
{
    string Schema { get; }
    string Name { get; }
    IReadOnlyList<ProcParamSpec> Parameters { get; }
    ProcReturnSpec Return { get; }
    IReadOnlyList<ColumnSpec>? ResultColumns { get; }
}
