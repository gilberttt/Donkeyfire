using DonkeyFire.Web.Data;
using DonkeyFire.Web.Services;
using Microsoft.EntityFrameworkCore;

namespace DonkeyFire.Tests;

public class TaxAndShareTests
{
    private static Business Shop(CogsBasis basis = CogsBasis.Purchases) => new()
    {
        Id = 1, Name = "Donkey Fire", PricesIncludeTax = true, CardFeePercent = 0,
        RevenueSharePartner = "Eufaula Parks and Recreation", RevenueSharePercent = 25, RevenueShareCogsBasis = basis,
    };

    private static TaxPlan Eufaula(Business b) => new(b, BusinessSeeder.EufaulaRates(b.Id));

    private static Sale S(decimal total, int? ev = null, decimal unitCost = 0) =>
        new() { Quantity = 1, UnitPrice = total, UnitCost = unitCost, MenuItemId = 1, EventId = ev, Description = "Item" };

    [Fact]
    public void Tax_included_in_prices_is_split_into_state_county_and_city()
    {
        var b = Shop();
        var p = BusinessMetrics.Compute(b, [S(1095)], [], [], new Dictionary<int, Event>(), Eufaula(b));

        Assert.Equal(95, p.SalesTax);                       // 1095 = 1000 + 9.5%
        Assert.Equal(1000, p.NetSales);
        Assert.Equal([40m, 15m, 40m], p.Taxes.Select(t => t.Amount));
        Assert.Equal(["Alabama state", "Barbour County", "City of Eufaula"], p.Taxes.Select(t => t.Name));
    }

    [Fact]
    public void Sales_outside_city_limits_skip_city_tax()
    {
        var b = Shop();
        var lake = new Event { Id = 3, OutsideCityLimits = true };
        var p = BusinessMetrics.Compute(b, [S(1055, ev: 3)], [], [], new Dictionary<int, Event> { [3] = lake }, Eufaula(b));

        Assert.Equal(55, p.SalesTax);
        Assert.Equal(0, p.Taxes.Single(t => t.Level == TaxLevel.City).Amount);
    }

    [Fact]
    public void Share_is_25_percent_of_sales_after_tax_purchases_and_labor_and_reduces_profit()
    {
        var b = Shop();
        b.LaborBurdenPercent = 10;
        var p = BusinessMetrics.Compute(b, [S(1095)],
            [new Expense { Category = ExpenseCategory.FoodAndDrink, Amount = 200 }, new Expense { Category = ExpenseCategory.PaperAndSupplies, Amount = 50 },
             new Expense { Category = ExpenseCategory.PropaneAndFuel, Amount = 30 }],
            [new Shift { Hours = 10, HourlyRate = 20, Tips = 25 }], new Dictionary<int, Event>(), Eufaula(b));

        var share = p.Share!;
        Assert.Equal((1095m, 95m, 250m, 220m), (share.GrossSales, share.SalesTax, share.Cogs, share.Labor));
        Assert.Equal(530, share.Base);                      // 1095 − 95 − 250 − 220; propane isn't cost of goods
        Assert.Equal(132.50m, share.Owed);
        Assert.Equal(1000 - 220 - 280 - 132.50m, p.NetProfit);
    }

    [Fact]
    public void Recipe_cost_basis_uses_cost_of_items_sold_instead_of_purchases()
    {
        var b = Shop(CogsBasis.RecipeCost);
        var p = BusinessMetrics.Compute(b, [S(1095, unitCost: 300)], [new Expense { Category = ExpenseCategory.FoodAndDrink, Amount = 999 }], [],
            new Dictionary<int, Event>(), Eufaula(b));
        Assert.Equal(300, p.Share!.Cogs);
        Assert.Equal(175, p.Share.Owed);
    }

    [Fact]
    public void Nothing_is_owed_when_costs_exceed_sales_and_excluded_events_are_left_out()
    {
        var b = Shop();
        var catering = new Event { Id = 9, ExcludeFromRevenueShare = true };
        var p = BusinessMetrics.Compute(b, [S(109.5m), S(5000, ev: 9)],
            [new Expense { Category = ExpenseCategory.FoodAndDrink, Amount = 500 }, new Expense { Category = ExpenseCategory.FoodAndDrink, Amount = 900, EventId = 9 }],
            [], new Dictionary<int, Event> { [9] = catering }, Eufaula(b));

        Assert.Equal(109.5m, p.Share!.GrossSales);
        Assert.Equal(500, p.Share.Cogs);
        Assert.Equal(0, p.Share.Owed);
    }

    [Fact]
    public void Tax_and_share_payments_are_not_counted_as_expenses()
    {
        var b = Shop();
        var p = BusinessMetrics.Compute(b, [S(1095)],
            [new Expense { Category = ExpenseCategory.SalesTaxPayment, Amount = 95 }, new Expense { Category = ExpenseCategory.RevenueSharePayment, Amount = 250 }],
            [], new Dictionary<int, Event>(), Eufaula(b));
        Assert.Equal(0, p.Expenses);
        Assert.Equal((95m, 250m), (p.SalesTaxPaid, p.RevenueSharePaid));
        Assert.Equal(1000 - 250, p.NetProfit);              // share owed is 25% of 1000
    }

    [Fact]
    public async Task Months_match_payments_to_the_month_they_cover_and_add_up_shares_month_by_month()
    {
        using var f = new TestDb();
        await using var db = f.CreateDbContext();
        var b = Shop();
        b.Id = 0;
        db.Businesses.Add(b);
        await db.SaveChangesAsync();
        db.TaxRates.AddRange(BusinessSeeder.EufaulaRates(b.Id));
        db.Sales.Add(new Sale { BusinessId = b.Id, Date = new DateOnly(2026, 9, 12), Quantity = 1, UnitPrice = 1095, Description = "Sept" });
        db.Sales.Add(new Sale { BusinessId = b.Id, Date = new DateOnly(2026, 10, 3), Quantity = 1, UnitPrice = 109.5m, Description = "Oct" });
        db.Expenses.Add(new Expense { BusinessId = b.Id, Date = new DateOnly(2026, 10, 3), Category = ExpenseCategory.FoodAndDrink, Amount = 400, Description = "Food" });
        db.Expenses.Add(new Expense { BusinessId = b.Id, Date = new DateOnly(2026, 10, 18), ForMonth = new DateOnly(2026, 9, 1), Category = ExpenseCategory.SalesTaxPayment, Amount = 95, Description = "Tax" });
        await db.SaveChangesAsync();
        var metrics = new BusinessMetrics(f);

        var months = await metrics.MonthsAsync(b, new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 1));
        var sept = months.Single(m => m.Month.Month == 9);
        Assert.Equal((95m, 95m, 0m), (sept.PnL.SalesTax, sept.SalesTaxPaid, sept.SalesTaxDue));
        Assert.Equal(250, sept.PnL.ShareOwed);
        Assert.Equal(new DateOnly(2026, 10, 20), sept.TaxDueDate);
        Assert.Equal(0, months.Single(m => m.Month.Month == 10).PnL.ShareOwed);   // October's food costs exceed its sales

        // Over both months the share is 250 + 0: October's loss doesn't reduce September's share.
        var both = await metrics.ForPeriodAsync(b, new Period(new DateOnly(2026, 9, 1), new DateOnly(2026, 10, 31), "Two months"));
        Assert.Equal(250, both.PnL.ShareOwed);
    }
}
