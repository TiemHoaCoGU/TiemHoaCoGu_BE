namespace HoaSap.Domain.Entities;

public class CustomQuotationRevision
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CustomQuotationId { get; set; }
    public int Version { get; set; }
    public decimal UnitPrice { get; set; }
    public int Quantity { get; set; }
    public decimal TotalAmount { get; set; }
    public decimal DepositAmount { get; set; }
    public DateTime CompletionDate { get; set; }
    public DateTime ExpiresAt { get; set; }
    public Guid CreatedBy { get; set; }
    public string AdminNote { get; set; } = "";
    public string? BouquetSnapshot { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public string Status { get; set; } = "Sent";
}
