using DonkeyFire.Web.Data;
using DonkeyFire.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace DonkeyFire.Tests;

public sealed class TestDb : IDbContextFactory<AppDbContext>, IDisposable
{
    private readonly SqliteConnection connection = new("Data Source=:memory:");
    private readonly DbContextOptions<AppDbContext> options;

    public TestDb()
    {
        connection.Open();
        options = new DbContextOptionsBuilder<AppDbContext>().UseSqlite(connection).Options;
        using var db = CreateDbContext();
        db.Database.EnsureCreated();
    }

    public AppDbContext CreateDbContext() => new(options);
    public void Dispose() => connection.Dispose();
}

public class MetricsTests
{
    private static readonly Business Shop = new() { Id = 1, Name = "Donkey Fire", SalesTaxPercent = 0, CardFeePercent = 0, LaborBurdenPercent = 0 };

    private static Sale S(decimal qty, decimal price, decimal cost = 0, PaymentMethod pay = PaymentMethod.Cash, int? ev = null, int? item = 1) =>
        new() { BusinessId = 1, Quantity = qty, UnitPrice = price, UnitCost = cost, Payment = pay, EventId = ev, MenuItemId = item, Description = "Hot dog" };

    [Fact]
    public void Profit_is_sales_less_expenses_and_wages_and_tips_are_not_a_cost()
    {
        var p = BusinessMetrics.Compute(Shop,
            [S(100, 4, 1.25m)],
            [new Expense { Amount = 50, Category = ExpenseCategory.FoodAndDrink }, new Expense { Amount = 25, Category = ExpenseCategory.PropaneAndFuel }],
            [new Shift { Hours = 8, HourlyRate = 12, Tips = 40 }],
            new Dictionary<int, Event>());

        Assert.Equal(400, p.NetSales);
        Assert.Equal(96, p.Labor);
        Assert.Equal(75, p.Expenses);
        Assert.Equal(229, p.NetProfit);
        Assert.Equal(125, p.FoodCost);           // recipe cost is reported, not subtracted again
        Assert.Equal(31.25, p.FoodCostPct, 2);
        Assert.Equal(40, p.Tips);
        Assert.Equal(100, p.ItemsSold);
    }

    [Fact]
    public void Tax_included_in_prices_is_split_out_of_gross_sales()
    {
        var b = new Business { SalesTaxPercent = 10, PricesIncludeTax = true };
        var p = BusinessMetrics.Compute(b, [S(1, 110)], [], [], new Dictionary<int, Event>());
        Assert.Equal(10, p.SalesTax);
        Assert.Equal(100, p.NetSales);
    }

    [Fact]
    public void Tax_added_on_top_is_owed_but_does_not_reduce_net_sales()
    {
        var b = new Business { SalesTaxPercent = 10, PricesIncludeTax = false };
        var p = BusinessMetrics.Compute(b, [S(1, 100)], [], [], new Dictionary<int, Event>());
        Assert.Equal(10, p.SalesTax);
        Assert.Equal(100, p.NetSales);
    }

    [Fact]
    public void Card_fees_apply_to_card_sales_only_and_commissions_to_that_events_sales()
    {
        var b = new Business { CardFeePercent = 3, LaborBurdenPercent = 10 };
        var fair = new Event { Id = 7, CommissionPercent = 20 };
        var p = BusinessMetrics.Compute(b, [S(1, 100, pay: PaymentMethod.Card, ev: 7), S(1, 100, pay: PaymentMethod.Cash)], [],
            [new Shift { Hours = 10, HourlyRate = 10 }], new Dictionary<int, Event> { [7] = fair });

        Assert.Equal(3, p.CardFees);
        Assert.Equal(20, p.Commissions);
        Assert.Equal(110, p.Labor);
        Assert.Equal(200 - 3 - 20 - 110, p.NetProfit);
    }

    [Fact]
    public void Recipe_cost_uses_inventory_costs_and_falls_back_to_manual_cost()
    {
        var bun = new InventoryItem { UnitCost = 0.25m };
        var frank = new InventoryItem { UnitCost = 0.55m };
        var dog = new MenuItem { Price = 4, Recipe = [new RecipeLine { InventoryItem = bun, Quantity = 1 }, new RecipeLine { InventoryItem = frank, Quantity = 1 }] };
        Assert.Equal(0.80m, BusinessMetrics.RecipeCost(dog));
        Assert.Equal(20, BusinessMetrics.FoodCostPct(dog), 2);
        Assert.Equal(0.45m, BusinessMetrics.RecipeCost(new MenuItem { Price = 2, ManualCost = 0.45m }));
    }

    [Fact]
    public void Period_presets_cover_the_expected_days()
    {
        var sunday = new DateOnly(2026, 11, 22);
        Assert.Equal(new DateOnly(2026, 11, 16), Period.FromPreset("week", sunday).From);   // weeks start Monday
        var last = Period.FromPreset("last-month", sunday);
        Assert.Equal((new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31)), (last.From, last.To));
        Assert.Equal(new DateOnly(2026, 11, 1), Period.FromPreset("month", sunday).From);
    }
}

public class SalesAndInventoryTests
{
    private static async Task<(TestDb Db, int BusinessId, int DogId, int BunId)> Setup()
    {
        var f = new TestDb();
        await using var db = f.CreateDbContext();
        var b = new Business { Name = "Donkey Fire" };
        db.Businesses.Add(b);
        await db.SaveChangesAsync();
        var bun = new InventoryItem { BusinessId = b.Id, Name = "Bun", UnitCost = 0.25m, OnHand = 48 };
        var frank = new InventoryItem { BusinessId = b.Id, Name = "Frank", UnitCost = 0.55m, OnHand = 40 };
        var dog = new MenuItem { BusinessId = b.Id, Name = "Hot dog", Price = 4, Recipe = [new RecipeLine { InventoryItem = bun, Quantity = 1 }, new RecipeLine { InventoryItem = frank, Quantity = 1 }] };
        db.MenuItems.Add(dog);
        await db.SaveChangesAsync();
        return (f, b.Id, dog.Id, bun.Id);
    }

    [Fact]
    public async Task Recording_a_sale_uses_up_the_recipe_and_deleting_it_puts_stock_back()
    {
        var (f, bid, dogId, bunId) = await Setup();
        using var _ = f;
        var svc = new SalesService(f);

        var sales = await svc.RecordItemsAsync(bid, new DateOnly(2026, 11, 21), null, PaymentMethod.Card, [new SaleLine(dogId, 10)]);

        var sale = Assert.Single(sales);
        Assert.Equal(40, sale.Total);
        Assert.Equal(0.8m, sale.UnitCost);
        await using (var db = f.CreateDbContext())
        {
            Assert.Equal(38, (await db.InventoryItems.FindAsync(bunId))!.OnHand);
            Assert.Equal(2, await db.InventoryAdjustments.CountAsync(a => a.SaleId == sale.Id));
        }

        await svc.DeleteAsync(bid, sale.Id);

        await using (var db = f.CreateDbContext())
        {
            Assert.Equal(48, (await db.InventoryItems.FindAsync(bunId))!.OnHand);
            Assert.Equal(0, await db.InventoryAdjustments.CountAsync());
            Assert.Equal(0, await db.Sales.CountAsync());
        }
    }

    [Fact]
    public async Task Sales_can_skip_inventory_and_ignore_other_businesses_menu_items()
    {
        var (f, bid, dogId, bunId) = await Setup();
        using var _ = f;
        var svc = new SalesService(f);

        await svc.RecordItemsAsync(bid, new DateOnly(2026, 11, 21), null, PaymentMethod.Cash, [new SaleLine(dogId, 5)], deductInventory: false);
        var foreign = await svc.RecordItemsAsync(bid + 1, new DateOnly(2026, 11, 21), null, PaymentMethod.Cash, [new SaleLine(dogId, 5)]);

        Assert.Empty(foreign);
        await using var db = f.CreateDbContext();
        Assert.Equal(48, (await db.InventoryItems.FindAsync(bunId))!.OnHand);
    }

    [Fact]
    public async Task Counts_set_on_hand_purchases_add_and_update_cost_and_waste_subtracts()
    {
        var (f, bid, _, bunId) = await Setup();
        using var __ = f;
        var inv = new InventoryService(f);
        var day = new DateOnly(2026, 11, 21);

        var count = await inv.AdjustAsync(bid, bunId, AdjustmentReason.Count, 30, day);
        await inv.AdjustAsync(bid, bunId, AdjustmentReason.Purchase, 72, day, unitCost: 0.30m);
        await inv.AdjustAsync(bid, bunId, AdjustmentReason.Waste, 2, day);

        Assert.Equal(-18, count!.Change);
        await using var db = f.CreateDbContext();
        var bun = (await db.InventoryItems.FindAsync(bunId))!;
        Assert.Equal(100, bun.OnHand);
        Assert.Equal(0.30m, bun.UnitCost);
        Assert.Null(await inv.AdjustAsync(bid + 1, bunId, AdjustmentReason.Waste, 1, day));
    }

    [Fact]
    public async Task Event_results_include_only_that_events_rows()
    {
        var (f, bid, dogId, _) = await Setup();
        using var __ = f;
        int evId;
        await using (var db = f.CreateDbContext())
        {
            var ev = new Event { BusinessId = bid, Name = "Fair", StartDate = new DateOnly(2026, 11, 21) };
            db.Events.Add(ev);
            await db.SaveChangesAsync();
            evId = ev.Id;
            db.Expenses.Add(new Expense { BusinessId = bid, EventId = evId, Amount = 50, Description = "Booth", Category = ExpenseCategory.EventFees });
            db.Expenses.Add(new Expense { BusinessId = bid, Amount = 999, Description = "Insurance", Category = ExpenseCategory.Insurance });
            await db.SaveChangesAsync();
        }
        var svc = new SalesService(f);
        await svc.RecordItemsAsync(bid, new DateOnly(2026, 11, 21), evId, PaymentMethod.Cash, [new SaleLine(dogId, 25)]);
        await svc.RecordLumpSumAsync(bid, new DateOnly(2026, 11, 21), null, PaymentMethod.Card, "Other day", 300);

        await using var read = f.CreateDbContext();
        var business = await read.Businesses.FirstAsync(b => b.Id == bid);
        var ev2 = await read.Events.FirstAsync(e => e.Id == evId);
        var pnl = await new BusinessMetrics(f).ForEventAsync(business, ev2);

        Assert.Equal(100, pnl.NetSales);
        Assert.Equal(50, pnl.Expenses);
        Assert.Equal(50, pnl.NetProfit);
    }

    [Fact]
    public async Task Deleting_an_event_keeps_its_sales_but_unlinks_them()
    {
        var (f, bid, dogId, _) = await Setup();
        using var __ = f;
        await using var db = f.CreateDbContext();
        var ev = new Event { BusinessId = bid, Name = "Game" };
        db.Events.Add(ev);
        await db.SaveChangesAsync();
        await new SalesService(f).RecordItemsAsync(bid, ev.StartDate, ev.Id, PaymentMethod.Cash, [new SaleLine(dogId, 1)]);

        await db.Events.Where(e => e.Id == ev.Id).ExecuteDeleteAsync();

        var sale = await db.Sales.AsNoTracking().SingleAsync();
        Assert.Null(sale.EventId);
    }

    [Fact]
    public async Task Starter_menu_gives_every_item_a_recipe_with_a_cost()
    {
        using var f = new TestDb();
        await using (var db = f.CreateDbContext())
        {
            db.Users.Add(new AppUser { ExternalId = "dev:1", Email = "Owner@Example.com", DisplayName = "Owner" });
            await db.SaveChangesAsync();
        }
        var b = await new BusinessSeeder(f).CreateBusinessAsync(new Business { Name = "Donkey Fire" }, 1, starterMenu: true);

        await using var read = f.CreateDbContext();
        var member = await read.BusinessMembers.SingleAsync();
        Assert.Equal(("owner@example.com", BusinessRole.Owner), (member.Email, member.Role));
        var menu = await read.MenuItems.Where(m => m.BusinessId == b.Id).Include(m => m.Recipe).ThenInclude(r => r.InventoryItem).ToListAsync();
        Assert.NotEmpty(menu);
        Assert.All(menu, m => Assert.True(BusinessMetrics.RecipeCost(m) > 0, m.Name));
        Assert.All(menu, m => Assert.InRange(BusinessMetrics.FoodCostPct(m), 1, 40));
    }
}
