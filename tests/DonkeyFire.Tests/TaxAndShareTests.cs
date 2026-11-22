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

    private static readonly Event Park = new() { Id = 1, Name = "Spring Baseball", AtPartnerVenue = true };
    private static Dictionary<int, Event> Events(params Event[] more) => new[] { Park }.Concat(more).ToDictionary(e => e.Id);

    private static Sale S(decimal total, int? ev = 1, decimal unitCost = 0, PaymentMethod pay = PaymentMethod.Cash, decimal? fee = null) =>
        new() { Quantity = 1, UnitPrice = total, UnitCost = unitCost, MenuItemId = 1, EventId = ev, Description = "Item", Payment = pay, Fee = fee };

    [Fact]
    public void Tax_included_in_prices_is_split_into_state_county_and_city()
    {
        var b = Shop();
        var p = BusinessMetrics.Compute(b, [S(1095)], [], [], Events(), Eufaula(b));

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
        var p = BusinessMetrics.Compute(b, [S(1055, ev: 3)], [], [], Events(lake), Eufaula(b));

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
            [new Shift { Hours = 10, HourlyRate = 20, Tips = 25 }], Events(), Eufaula(b));

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
            Events(), Eufaula(b));
        Assert.Equal(300, p.Share!.Cogs);
        Assert.Equal(175, p.Share.Owed);
    }

    [Fact]
    public void Nothing_is_owed_when_costs_exceed_sales()
    {
        var b = Shop();
        var p = BusinessMetrics.Compute(b, [S(109.5m)], [new Expense { Category = ExpenseCategory.FoodAndDrink, Amount = 500, EventId = 1 }], [], Events(), Eufaula(b));
        Assert.Equal(-400, p.Share!.Base);
        Assert.Equal(0, p.Share.Owed);
    }

    [Fact]
    public void Card_fees_come_off_before_the_share()
    {
        var b = Shop();
        b.CardFeePercent = 3;
        var p = BusinessMetrics.Compute(b, [S(1095, pay: PaymentMethod.Card, fee: 30m), S(109.5m, pay: PaymentMethod.Card)], [], [], Events(), Eufaula(b));
        Assert.Equal(33.29m, p.Share!.Fees);                // Square's actual fee, plus 3% estimated on the sale without one
        Assert.Equal(Math.Round((1100 - 33.29m) * 0.25m, 2), p.Share.Owed);
    }

    [Fact]
    public void Only_park_venue_sales_count_and_unassigned_costs_are_shared_out_by_sales()
    {
        var b = Shop();
        var festival = new Event { Id = 2, Name = "Indian Summer Festival" };
        var p = BusinessMetrics.Compute(b,
            [S(1095), S(1095, ev: 2), S(219, ev: null)],                           // park 1000, festival 1000, no event 200 (pre-tax)
            [new Expense { Category = ExpenseCategory.FoodAndDrink, Amount = 440 },             // Sam's run, no event: 1000/2200 of it
             new Expense { Category = ExpenseCategory.PaperAndSupplies, Amount = 50, EventId = 1 },
             new Expense { Category = ExpenseCategory.FoodAndDrink, Amount = 300, EventId = 2 }],
            [new Shift { Hours = 11, HourlyRate = 20 }, new Shift { Hours = 5, HourlyRate = 20, EventId = 1 }, new Shift { Hours = 8, HourlyRate = 20, EventId = 2 }],
            Events(festival), Eufaula(b));

        var share = p.Share!;
        Assert.Equal((1095m, 95m), (share.GrossSales, share.SalesTax));
        Assert.Equal(200 + 50, share.Cogs);                 // 440 × 1000/2200 + 50
        Assert.Equal(100 + 100, share.Labor);               // 220 × 1000/2200 + 100
        Assert.Equal(1314, share.SalesNotCounted);
        Assert.Equal(219, share.SalesWithoutEvent);
        Assert.Equal(137.50m, share.Owed);                  // 25% of (1000 − 250 − 200)
    }

    [Fact]
    public void Tax_and_share_payments_are_not_counted_as_expenses()
    {
        var b = Shop();
        var p = BusinessMetrics.Compute(b, [S(1095)],
            [new Expense { Category = ExpenseCategory.SalesTaxPayment, Amount = 95 }, new Expense { Category = ExpenseCategory.RevenueSharePayment, Amount = 250 }],
            [], Events(), Eufaula(b));
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
        var season = new Event { BusinessId = b.Id, Name = "Fall ball", StartDate = new DateOnly(2026, 9, 1), EndDate = new DateOnly(2026, 10, 31), AtPartnerVenue = true };
        db.Events.Add(season);
        await db.SaveChangesAsync();
        db.Sales.Add(new Sale { BusinessId = b.Id, EventId = season.Id, Date = new DateOnly(2026, 9, 12), Quantity = 1, UnitPrice = 1095, Description = "Sept" });
        db.Sales.Add(new Sale { BusinessId = b.Id, EventId = season.Id, Date = new DateOnly(2026, 10, 3), Quantity = 1, UnitPrice = 109.5m, Description = "Oct" });
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
