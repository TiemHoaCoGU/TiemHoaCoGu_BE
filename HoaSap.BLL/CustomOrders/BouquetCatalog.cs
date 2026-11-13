using System.Text.Json;

namespace HoaSap.BLL.CustomOrders;

public sealed class BouquetItem
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string? Color { get; set; }
    public string Category { get; set; } = "";
    public string Art { get; set; } = "";
    public int Price { get; set; }
    public string? Swatch { get; set; }
}

public sealed class BouquetWrap
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public int Price { get; set; }
}
public sealed record BouquetLineInput(string Id, int Quantity);
public sealed record BouquetInput(string WrapId, IReadOnlyList<BouquetLineInput> Lines);
public sealed record BouquetLine(string Id, string Name, string? Color, int Quantity);
public sealed record BouquetSnapshot(string WrapId, string WrapName, IReadOnlyList<BouquetLine> Lines, long? EstimatedUnitPrice);

public sealed class BouquetCatalog
{
    public Guid ProductId { get; set; }
    public int MinFlowers { get; set; }
    public int MaxStems { get; set; }
    public int LaborPrice { get; set; }
    public List<BouquetItem> Items { get; set; } = [];
    public List<BouquetWrap> Wraps { get; set; } = [];

    internal bool IsConfigured => MinFlowers > 0 && MaxStems >= MinFlowers && MaxStems <= 101 && LaborPrice >= 0
        && Items.Any(item => item.Category == "flowers") && Wraps.Count > 0
        && Items.All(item => !string.IsNullOrWhiteSpace(item.Id) && !string.IsNullOrWhiteSpace(item.Name) && item.Price >= 0 && item.Category is "flowers" or "greenery" or "accents")
        && Wraps.All(wrap => !string.IsNullOrWhiteSpace(wrap.Id) && !string.IsNullOrWhiteSpace(wrap.Name) && wrap.Price >= 0)
        && Items.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count() == Items.Count
        && Wraps.Select(wrap => wrap.Id).Distinct(StringComparer.Ordinal).Count() == Wraps.Count;

    internal string Snapshot(BouquetInput input)
    {
        if (!IsConfigured) throw new BusinessException(503, "Tiệm chưa mở bộ phối hoa.");
        var wrap = Wraps.Find(option => option.Id == input.WrapId);
        if (wrap is null || input.Lines is null || input.Lines.Count == 0 || input.Lines.Count > MaxStems)
            throw new BusinessException(400, "Cấu hình bó hoa chưa hợp lệ.");
        var lines = new List<BouquetLine>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var total = 0;
        var flowers = 0;
        long estimate = (long)LaborPrice + wrap.Price;
        foreach (var line in input.Lines.OrderBy(line => line?.Id, StringComparer.Ordinal))
        {
            if (line is null || string.IsNullOrWhiteSpace(line.Id) || line.Quantity < 1 || line.Quantity > MaxStems - total || !ids.Add(line.Id))
                throw new BusinessException(400, "Số lượng hoa hoặc phụ kiện không hợp lệ.");
            var item = Items.Find(option => option.Id == line.Id) ?? throw new BusinessException(400, "Có lựa chọn hoa không còn khả dụng.");
            total += line.Quantity;
            if (item.Category == "flowers") flowers += line.Quantity;
            estimate += (long)item.Price * line.Quantity;
            lines.Add(new(item.Id, item.Name, item.Color, line.Quantity));
        }
        if (flowers < MinFlowers) throw new BusinessException(400, $"Cần ít nhất {MinFlowers} bông hoa chính.");
        return JsonSerializer.Serialize(new BouquetSnapshot(wrap.Id, wrap.Name, lines, estimate));
    }

    internal static BouquetSnapshot? Read(string? snapshot, bool hideMoney = false)
    {
        var bouquet = snapshot is null ? null : JsonSerializer.Deserialize<BouquetSnapshot>(snapshot);
        return hideMoney && bouquet is not null ? bouquet with { EstimatedUnitPrice = null } : bouquet;
    }
}
