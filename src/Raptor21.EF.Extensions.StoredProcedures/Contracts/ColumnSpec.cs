namespace Raptor21.EF.Extensions.StoredProcedures.Contracts;

/// <summary>Result set column specification; DotNetType is used for validation only (no runtime reflection).</summary>
public readonly record struct ColumnSpec(string Name, Type DotNetType);
