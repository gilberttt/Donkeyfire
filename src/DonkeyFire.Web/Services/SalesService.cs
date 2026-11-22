using DonkeyFire.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DonkeyFire.Web.Services;

public record SaleLine(int MenuItemId, decimal Quantity, decimal? UnitPrice = null);

/// <summary>Records sales and keeps inventory in step: a sale of a menu item uses up its recipe, and deleting it puts the stock back.</summary>
public class SalesService(IDbContextFactory<AppDbContext> dbf)
{
    public async Task<List<Sale>> RecordItemsAsync(int businessId, DateOnly date, int? eventId, PaymentMethod payment,
        IEnumerable<SaleLine> lines, bool deductInventory = true, string? note = null)
    {
        var wanted = lines.Where(l => l.Quantity != 0).ToList();
        if (wanted.Count == 0) return [];
        await using var db = await dbf.CreateDbContextAsync();
        var ids = wanted.Select(l => l.MenuItemId).Distinct().ToList();
        var menu = await db.MenuItems.Where(m => m.BusinessId == businessId && ids.Contains(m.Id))
            .Include(m => m.Recipe).ThenInclude(r => r.InventoryItem).ToDictionaryAsync(m => m.Id);

        var sales = new List<(Sale Sale, MenuItem Item)>();
        foreach (var l in wanted)
        {
            if (!menu.TryGetValue(l.MenuItemId, out var m)) continue;
            var sale = new Sale
            {
                BusinessId = businessId, Date = date, EventId = eventId, MenuItemId = m.Id, Description = m.Name,
                Quantity = l.Quantity, UnitPrice = l.UnitPrice ?? m.Price, UnitCost = Math.Round(BusinessMetrics.RecipeCost(m), 4),
                Payment = payment, Note = note,
            };
            db.Sales.Add(sale);
            sales.Add((sale, m));
        }
        await db.SaveChangesAsync();

        if (deductInventory)
        {
            foreach (var (sale, m) in sales)
            {
                foreach (var r in m.Recipe.Where(r => r.InventoryItem is not null))
                {
                    var used = sale.Quantity * r.Quantity;
                    r.InventoryItem!.OnHand -= used;
                    db.InventoryAdjustments.Add(new InventoryAdjustment
                    {
                        BusinessId = businessId, InventoryItemId = r.InventoryItemId, Date = date, Reason = AdjustmentReason.Sale,
                        Change = -used, SaleId = sale.Id, Note = $"{Fmt.Qty(sale.Quantity)} × {m.Name}",
                    });
                }
            }
            await db.SaveChangesAsync();
        }
        return sales.Select(s => s.Sale).ToList();
    }

    public async Task<Sale> RecordLumpSumAsync(int businessId, DateOnly date, int? eventId, PaymentMethod payment, string description, decimal amount, string? note = null)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var sale = new Sale
        {
            BusinessId = businessId, Date = date, EventId = eventId, Description = string.IsNullOrWhiteSpace(description) ? "Sales" : description.Trim(),
            Quantity = 1, UnitPrice = amount, Payment = payment, Note = note,
        };
        db.Sales.Add(sale);
        await db.SaveChangesAsync();
        return sale;
    }

    public async Task DeleteAsync(int businessId, int saleId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var sale = await db.Sales.FirstOrDefaultAsync(s => s.Id == saleId && s.BusinessId == businessId);
        if (sale is null) return;
        var adjustments = await db.InventoryAdjustments.Include(a => a.InventoryItem).Where(a => a.SaleId == saleId && a.BusinessId == businessId).ToListAsync();
        foreach (var a in adjustments)
        {
            if (a.InventoryItem is not null) a.InventoryItem.OnHand -= a.Change;
            db.InventoryAdjustments.Remove(a);
        }
        db.Sales.Remove(sale);
        await db.SaveChangesAsync();
    }
}

public class InventoryService(IDbContextFactory<AppDbContext> dbf)
{
    /// <summary>
    /// Applies a stock change. For <see cref="AdjustmentReason.Count"/>, <paramref name="amount"/> is the counted quantity on hand;
    /// for every other reason it is how much came in (purchase) or went out (used, waste, sale), always entered as a positive number.
    /// A purchase with a unit cost becomes the item's cost going forward.
    /// </summary>
    public async Task<InventoryAdjustment?> AdjustAsync(int businessId, int itemId, AdjustmentReason reason, decimal amount, DateOnly date,
        decimal? unitCost = null, string? note = null)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var item = await db.InventoryItems.FirstOrDefaultAsync(i => i.Id == itemId && i.BusinessId == businessId);
        if (item is null) return null;
        var change = reason switch
        {
            AdjustmentReason.Count => amount - item.OnHand,
            AdjustmentReason.Purchase => Math.Abs(amount),
            _ => -Math.Abs(amount),
        };
        item.OnHand += change;
        if (reason == AdjustmentReason.Purchase && unitCost is { } c && c >= 0) item.UnitCost = c;
        var adj = new InventoryAdjustment
        {
            BusinessId = businessId, InventoryItemId = itemId, Date = date, Reason = reason, Change = change,
            UnitCost = reason == AdjustmentReason.Purchase ? unitCost : null, Note = string.IsNullOrWhiteSpace(note) ? null : note.Trim(),
        };
        db.InventoryAdjustments.Add(adj);
        await db.SaveChangesAsync();
        return adj;
    }
}
