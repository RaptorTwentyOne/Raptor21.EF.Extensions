namespace Raptor21.EF.Extensions.StoredProcedures.Contracts;

/// <summary>Stored procedure return specification (RETURN value or result set).</summary>
public readonly record struct ProcReturnSpec(ReturnKind Kind, SqlTypeSpec SqlType);
