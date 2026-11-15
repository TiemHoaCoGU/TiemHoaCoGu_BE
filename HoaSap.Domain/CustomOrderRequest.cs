namespace HoaSap.Domain.Entities;

/// <summary>Yêu cầu sản phẩm đặt trước/tùy biến; không giữ tồn kho có sẵn.</summary>
public class CustomOrderRequest
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CustomerId { get; set; }
    public Guid ProductId { get; set; }
    public int Quantity { get; set; } = 1;
    public string Description { get; set; } = "";
    public string ReferenceImageUrl { get; set; } = "";
    /// <summary>Khu vực giao hoa khách chọn lúc gửi yêu cầu (danh mục GHN 2 cấp); dùng để báo phí và điền sẵn lúc checkout.</summary>
    public int? DeliveryProvinceId { get; set; }
    public string? DeliveryProvinceName { get; set; }
    public int? DeliveryWardId { get; set; }
    public string? DeliveryWardName { get; set; }
    public DateTime? RequestedCompletionDate { get; set; }
    public DateTime? ProductionStartedAt { get; set; }
    public DateTime? CompletedAt { get; set; }
    public string Status { get; set; } = "Requested";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
    public Guid ConcurrencyToken { get; set; } = Guid.NewGuid();
}
