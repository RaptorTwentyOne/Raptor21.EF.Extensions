namespace Raptor21.EF.Extensions.StoredProcedures.Contracts;

/// <summary>SQL Server type specification (name + optional length/precision/scale).</summary>
public readonly record struct SqlTypeSpec(
    string SqlTypeName,
    int? MaxLength = null,
    byte? Precision = null,
    byte? Scale = null);
