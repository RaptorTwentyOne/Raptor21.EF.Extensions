namespace Raptor21.EF.Extensions.StoredProcedures.Contracts;

/// <summary>
/// Single shared <see cref="IStoredProcedureContract"/> implementation used by all
/// source-generated contracts. Replaces the per-procedure nested <c>Impl</c> classes:
/// the generator emits one <see cref="GeneratedContract"/> instance per stored procedure
/// instead of a bespoke type each.
/// </summary>
public sealed class GeneratedContract : IStoredProcedureContract
{
    public GeneratedContract(
        string schema,
        string name,
        IReadOnlyList<ProcParamSpec> parameters,
        ProcReturnSpec @return,
        IReadOnlyList<ColumnSpec>? resultColumns = null)
    {
        Schema = schema;
        Name = name;
        Parameters = parameters;
        Return = @return;
        ResultColumns = resultColumns;
    }

    public string Schema { get; }
    public string Name { get; }
    public IReadOnlyList<ProcParamSpec> Parameters { get; }
    public ProcReturnSpec Return { get; }
    public IReadOnlyList<ColumnSpec>? ResultColumns { get; }
}
