namespace Raptor21.EF.Extensions.Sample.Data;

/// <summary>The one table in this sample. EF owns its shape; the procedures below read and write it.</summary>
public sealed class Product
{
    public int Id { get; set; }
    public string Sku { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public decimal Price { get; set; }
    public DateTime UpdatedUtc { get; set; }
}
