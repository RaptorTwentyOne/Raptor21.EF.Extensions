namespace Raptor21.EF.Extensions.StoredProcedures.Contracts;

/// <summary>Stored procedure parameter specification.</summary>
public readonly record struct ProcParamSpec(
    string Name,
    SqlTypeSpec SqlType,
    bool IsOutput = false);
