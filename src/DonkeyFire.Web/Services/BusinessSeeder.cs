using DonkeyFire.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DonkeyFire.Web.Services;

/// <summary>Creates a business with its first owner and, optionally, a starter concession menu to edit.</summary>
public class BusinessSeeder(IDbContextFactory<AppDbContext> dbf)
{
    /// <summary>
    /// Sales tax inside Eufaula city limits: 4% Alabama + 1.5% Barbour County + 4% City of Eufaula (9.5%), as of Nov 2026.
    /// All three are filed through My Alabama Taxes. Confirm with the Alabama Department of Revenue rate lookup if rates change.
    /// </summary>
    public static IEnumerable<TaxRate> EufaulaRates(int businessId) =>
    [
        new() { BusinessId = businessId, Name = "Alabama state", Level = TaxLevel.State, Percent = 4m, FiledWith = "My Alabama Taxes", SortOrder = 0 },
        new() { BusinessId = businessId, Name = "Barbour County", Level = TaxLevel.County, Percent = 1.5m, FiledWith = "My Alabama Taxes", SortOrder = 1 },
        new() { BusinessId = businessId, Name = "City of Eufaula", Level = TaxLevel.City, Percent = 4m, FiledWith = "My Alabama Taxes", SortOrder = 2 },
    ];

    public async Task<Business> CreateBusinessAsync(Business business, int ownerUserId, bool starterMenu, bool eufaulaTaxes = false)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var owner = await db.Users.AsNoTracking().FirstAsync(u => u.Id == ownerUserId);
        db.Businesses.Add(business);
        await db.SaveChangesAsync();
        db.BusinessMembers.Add(new BusinessMember
        {
            BusinessId = business.Id, UserId = owner.Id, Email = owner.Email.Trim().ToLowerInvariant(), Role = BusinessRole.Owner, AcceptedAt = DateTime.UtcNow,
        });
        if (starterMenu) AddStarterMenu(db, business.Id);
        if (eufaulaTaxes)
        {
            db.TaxRates.AddRange(EufaulaRates(business.Id));
            business.SalesTaxPercent = 9.5m;
        }
        await db.SaveChangesAsync();
        return business;
    }

    // Typical concession-stand items with ballpark wholesale costs. Prices and costs are placeholders to edit.
    private static readonly (string Name, InventoryCategory Cat, string Unit, decimal Cost, decimal OnHand, decimal Reorder)[] Stock =
    [
        ("Hot dog buns", InventoryCategory.Food, "each", 0.25m, 0, 48),
        ("All-beef franks", InventoryCategory.Food, "each", 0.55m, 0, 40),
        ("Hamburger buns", InventoryCategory.Food, "each", 0.35m, 0, 36),
        ("Burger patties (1/4 lb)", InventoryCategory.Food, "each", 1.10m, 0, 30),
        ("American cheese slices", InventoryCategory.Food, "each", 0.15m, 0, 40),
        ("Tortilla chips", InventoryCategory.Food, "oz", 0.12m, 0, 80),
        ("Nacho cheese sauce", InventoryCategory.Food, "oz", 0.10m, 0, 100),
        ("Funnel cake mix", InventoryCategory.Food, "lb", 1.20m, 0, 10),
        ("Powdered sugar", InventoryCategory.Food, "oz", 0.08m, 0, 32),
        ("Popcorn kernels", InventoryCategory.Food, "oz", 0.06m, 0, 64),
        ("Canned soda", InventoryCategory.Drink, "each", 0.45m, 0, 48),
        ("Bottled water", InventoryCategory.Drink, "each", 0.25m, 0, 48),
        ("Food trays", InventoryCategory.PaperAndPackaging, "each", 0.08m, 0, 100),
        ("Hot dog clamshells", InventoryCategory.PaperAndPackaging, "each", 0.12m, 0, 100),
        ("Popcorn bags", InventoryCategory.PaperAndPackaging, "each", 0.10m, 0, 100),
        ("Napkins", InventoryCategory.PaperAndPackaging, "each", 0.01m, 0, 500),
    ];

    private static readonly (string Name, string Category, decimal Price, (string Item, decimal Qty)[] Recipe)[] Menu =
    [
        ("Hot dog", "Food", 4m, [("Hot dog buns", 1), ("All-beef franks", 1), ("Hot dog clamshells", 1), ("Napkins", 2)]),
        ("Cheeseburger", "Food", 8m, [("Hamburger buns", 1), ("Burger patties (1/4 lb)", 1), ("American cheese slices", 1), ("Food trays", 1), ("Napkins", 2)]),
        ("Nachos", "Food", 6m, [("Tortilla chips", 3), ("Nacho cheese sauce", 3), ("Food trays", 1), ("Napkins", 2)]),
        ("Funnel cake", "Sweets", 8m, [("Funnel cake mix", 0.25m), ("Powdered sugar", 1), ("Food trays", 1), ("Napkins", 3)]),
        ("Popcorn", "Snacks", 4m, [("Popcorn kernels", 2), ("Popcorn bags", 1)]),
        ("Canned soda", "Drinks", 2m, [("Canned soda", 1)]),
        ("Bottled water", "Drinks", 2m, [("Bottled water", 1)]),
    ];

    private static void AddStarterMenu(AppDbContext db, int businessId)
    {
        var items = Stock.ToDictionary(s => s.Name, s => new InventoryItem
        {
            BusinessId = businessId, Name = s.Name, Category = s.Cat, Unit = s.Unit, UnitCost = s.Cost, OnHand = s.OnHand, ReorderLevel = s.Reorder,
        });
        db.InventoryItems.AddRange(items.Values);
        var order = 0;
        foreach (var m in Menu)
        {
            db.MenuItems.Add(new MenuItem
            {
                BusinessId = businessId, Name = m.Name, Category = m.Category, Price = m.Price, SortOrder = order++,
                Recipe = m.Recipe.Select(r => new RecipeLine { InventoryItem = items[r.Item], Quantity = r.Qty }).ToList(),
            });
        }
    }
}
