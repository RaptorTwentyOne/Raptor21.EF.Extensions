namespace Raptor21.EF.Extensions.Migrations;

/// <summary>Which side of a boundary value the boundary itself belongs to — <c>RANGE LEFT</c> or <c>RANGE RIGHT</c>.</summary>
public enum PartitionRange
{
    /// <summary><c>RANGE LEFT</c>: each boundary value is the last value of the partition to its left.</summary>
    Left,

    /// <summary>
    /// <c>RANGE RIGHT</c>: each boundary value is the first value of the partition to its right. The natural
    /// choice for date boundaries, where <c>'2026-09-01'</c> should open September rather than close August.
    /// </summary>
    Right,
}
