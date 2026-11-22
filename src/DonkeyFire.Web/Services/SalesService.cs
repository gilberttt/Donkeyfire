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
            foreach (var (sale, m) in sales) UseIngredients(db, sale, m);
            await db.SaveChangesAsync();
        }
        return sales.Select(s => s.Sale).ToList();
    }

    /// <summary>
    /// Takes a saved sale's recipe out of inventory, writing Sale adjustments linked to it. The menu item must be tracked by
    /// <paramref name="db"/> with its recipe and inventory items loaded. The caller saves.
    /// </summary>
    internal static void UseIngredients(AppDbContext db, Sale sale, MenuItem m)
    {
        foreach (var r in m.Recipe.Where(r => r.InventoryItem is not null))
        {
            var used = sale.Quantity * r.Quantity;
            r.InventoryItem!.OnHand -= used;
            db.InventoryAdjustments.Add(new InventoryAdjustment
            {
                BusinessId = sale.BusinessId, InventoryItemId = r.InventoryItemId, Date = sale.Date, Reason = AdjustmentReason.Sale,
                Change = -used, SaleId = sale.Id, Note = $"{Fmt.Qty(sale.Quantity)} × {m.Name}",
            });
        }
    }

    /// <summary>
    /// Links imported sales that came in without a menu item (a name the menu didn't know yet) to menu items whose name or
    /// Square names now match, and takes their ingredients out of inventory. Returns how many sales were linked.
    /// </summary>
    public async Task<int> RematchAsync(int businessId)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var orphans = await db.Sales.Where(s => s.BusinessId == businessId && s.Source == SaleSource.Square && s.MenuItemId == null && s.ExternalId != null).ToListAsync();
        if (orphans.Count == 0) return 0;
        var matcher = new MenuMatcher(await db.MenuItems.Where(m => m.BusinessId == businessId)
            .Include(m => m.Recipe).ThenInclude(r => r.InventoryItem).ToListAsync());
        var linked = 0;
        foreach (var sale in orphans)
        {
            if (matcher.Find(sale.Description) is not { } m) continue;
            sale.MenuItemId = m.Id;
            sale.UnitCost = Math.Round(BusinessMetrics.RecipeCost(m), 4);
            UseIngredients(db, sale, m);
            linked++;
        }
        await db.SaveChangesAsync();
        return linked;
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

/// <summary>Finds the menu item for a name as it appears on Square: the item's own name or any of its Square names, ignoring case.</summary>
public class MenuMatcher
{
    private readonly Dictionary<string, MenuItem> byName = new(StringComparer.OrdinalIgnoreCase);

    public MenuMatcher(IEnumerable<MenuItem> items)
    {
        // Active items win when two items share a name.
        foreach (var m in items.OrderBy(m => m.IsActive))
        {
            foreach (var n in NamesOf(m)) byName[Normalize(n)] = m;
        }
    }

    public static IEnumerable<string> NamesOf(MenuItem m) =>
        new[] { m.Name }.Concat((m.SquareNames ?? "").Split(['\n', '\r', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    public MenuItem? Find(string? name) => string.IsNullOrWhiteSpace(name) ? null : byName.GetValueOrDefault(Normalize(name));

    private static string Normalize(string s) => string.Join(' ', s.Split(' ', StringSplitOptions.RemoveEmptyEntries));
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
