using DonkeyFire.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DonkeyFire.Web.Services;

public record Period(DateOnly From, DateOnly To, string Label)
{
    public bool Contains(DateOnly d) => d >= From && d <= To;

    public static IReadOnlyList<(string Key, string Label)> Presets { get; } =
    [
        ("today", "Today"), ("week", "This week"), ("month", "This month"), ("last-month", "Last month"),
        ("ytd", "Year to date"), ("12m", "Last 12 months"), ("all", "All time"),
    ];

    public static Period FromPreset(string? key, DateOnly today) => key switch
    {
        "today" => new(today, today, "Today"),
        "week" => new(today.AddDays(-(((int)today.DayOfWeek + 6) % 7)), today, "This week"),
        "last-month" => new(new DateOnly(today.Year, today.Month, 1).AddMonths(-1), new DateOnly(today.Year, today.Month, 1).AddDays(-1), "Last month"),
        "ytd" => new(new DateOnly(today.Year, 1, 1), today, "Year to date"),
        "12m" => new(today.AddMonths(-12).AddDays(1), today, "Last 12 months"),
        "all" => new(DateOnly.MinValue, DateOnly.MaxValue, "All time"),
        _ => new(new DateOnly(today.Year, today.Month, 1), today, "This month"),
    };
}

public record ItemSales(string Name, decimal Quantity, decimal Revenue, decimal Cost)
{
    public decimal Margin => Revenue - Cost;
}

public record DaySales(DateOnly Date, decimal Sales, decimal Profit);

/// <summary>
/// Profit and loss for a period or an event. Profit is cash-basis: net sales less expenses, labor, estimated card fees
/// and event commissions. Food cost is the recipe cost of what was sold; it is a ratio to watch, not subtracted again
/// (the ingredients were already counted when the purchase was logged as an expense).
/// </summary>
public class PnL
{
    public decimal GrossSales { get; init; }
    public decimal CashSales { get; init; }
    public decimal CardSales { get; init; }
    public decimal SalesTax { get; init; }
    public decimal NetSales { get; init; }
    public decimal CardFees { get; init; }
    public decimal Commissions { get; init; }
    public decimal FoodCost { get; init; }
    public decimal Wages { get; init; }
    public decimal LaborBurden { get; init; }
    public decimal Tips { get; init; }
    public decimal LaborHours { get; init; }
    public decimal Expenses { get; init; }
    public decimal ItemsSold { get; init; }
    public IReadOnlyList<(ExpenseCategory Category, decimal Amount)> ExpensesByCategory { get; init; } = [];
    public IReadOnlyList<ItemSales> Items { get; init; } = [];

    public decimal Labor => Wages + LaborBurden;
    public decimal NetProfit => NetSales - CardFees - Commissions - Expenses - Labor;
    public double FoodCostPct => Ratio(FoodCost);
    public double LaborPct => Ratio(Labor);
    public double ProfitMarginPct => Ratio(NetProfit);
    public decimal SalesPerLaborHour => LaborHours == 0 ? 0 : NetSales / LaborHours;

    private double Ratio(decimal part) => NetSales == 0 ? 0 : (double)(part / NetSales) * 100;
}

public record EventResult(Event Event, PnL PnL);

public class BusinessMetrics(IDbContextFactory<AppDbContext> dbf)
{
    public static decimal RecipeCost(MenuItem m) =>
        m.Recipe.Count > 0 ? m.Recipe.Sum(r => r.Quantity * (r.InventoryItem?.UnitCost ?? 0)) : m.ManualCost ?? 0;

    public static double FoodCostPct(MenuItem m) => m.Price == 0 ? 0 : (double)(RecipeCost(m) / m.Price) * 100;

    /// <summary>Builds a P&amp;L from already-loaded rows. Everything else in this class funnels through here.</summary>
    public static PnL Compute(Business b, IReadOnlyCollection<Sale> sales, IReadOnlyCollection<Expense> expenses, IReadOnlyCollection<Shift> shifts,
        IReadOnlyDictionary<int, Event> events)
    {
        var gross = sales.Sum(s => s.Total);
        var rate = b.SalesTaxPercent;
        var tax = rate <= 0 ? 0 : b.PricesIncludeTax ? gross * rate / (100 + rate) : gross * rate / 100;
        var net = b.PricesIncludeTax ? gross - tax : gross;
        var card = sales.Where(s => s.Payment == PaymentMethod.Card).Sum(s => s.Total);
        var commissions = sales.Where(s => s.EventId is { } id && events.TryGetValue(id, out var e) && e.CommissionPercent > 0)
            .Sum(s => NetOf(b, s.Total) * events[s.EventId!.Value].CommissionPercent / 100);
        var wages = shifts.Sum(s => s.Wages);

        return new PnL
        {
            GrossSales = gross,
            CardSales = card,
            CashSales = sales.Where(s => s.Payment == PaymentMethod.Cash).Sum(s => s.Total),
            SalesTax = Round(tax),
            NetSales = Round(net),
            CardFees = Round(card * b.CardFeePercent / 100),
            Commissions = Round(commissions),
            FoodCost = Round(sales.Sum(s => s.Quantity * s.UnitCost)),
            Wages = wages,
            LaborBurden = Round(wages * b.LaborBurdenPercent / 100),
            Tips = shifts.Sum(s => s.Tips),
            LaborHours = shifts.Sum(s => s.Hours),
            Expenses = expenses.Sum(e => e.Amount),
            ItemsSold = sales.Where(s => s.MenuItemId != null).Sum(s => s.Quantity),
            ExpensesByCategory = expenses.GroupBy(e => e.Category).Select(g => (g.Key, g.Sum(e => e.Amount))).OrderByDescending(x => x.Item2).ToList(),
            Items = sales.Where(s => s.MenuItemId != null).GroupBy(s => s.MenuItemId)
                .Select(g => new ItemSales(g.OrderByDescending(s => s.Date).First().Description, g.Sum(s => s.Quantity), g.Sum(s => s.Total), g.Sum(s => s.Quantity * s.UnitCost)))
                .OrderByDescending(i => i.Revenue).ToList(),
        };
    }

    private static decimal NetOf(Business b, decimal gross) =>
        b.PricesIncludeTax && b.SalesTaxPercent > 0 ? gross * 100 / (100 + b.SalesTaxPercent) : gross;

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    public class Snapshot
    {
        public required PnL PnL { get; init; }
        public required IReadOnlyList<EventResult> Events { get; init; }
        public required IReadOnlyList<DaySales> Days { get; init; }
    }

    public async Task<Snapshot> ForPeriodAsync(Business b, Period period)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var sales = await db.Sales.AsNoTracking().Where(s => s.BusinessId == b.Id && s.Date >= period.From && s.Date <= period.To).ToListAsync();
        var expenses = await db.Expenses.AsNoTracking().Where(e => e.BusinessId == b.Id && e.Date >= period.From && e.Date <= period.To).ToListAsync();
        var shifts = await db.Shifts.AsNoTracking().Where(s => s.BusinessId == b.Id && s.Date >= period.From && s.Date <= period.To).ToListAsync();
        var events = await db.Events.AsNoTracking().Where(e => e.BusinessId == b.Id).ToDictionaryAsync(e => e.Id);

        var pnl = Compute(b, sales, expenses, shifts, events);
        var eventIds = sales.Select(s => s.EventId).Concat(expenses.Select(e => e.EventId)).Concat(shifts.Select(s => s.EventId))
            .Where(id => id is not null).Select(id => id!.Value).Distinct();
        var byEvent = eventIds.Where(events.ContainsKey).Select(id => new EventResult(events[id], Compute(b,
                sales.Where(s => s.EventId == id).ToList(), expenses.Where(e => e.EventId == id).ToList(), shifts.Where(s => s.EventId == id).ToList(), events)))
            .OrderByDescending(r => r.Event.StartDate).ToList();
        var days = sales.Select(s => s.Date).Concat(expenses.Select(e => e.Date)).Concat(shifts.Select(s => s.Date)).Distinct().OrderBy(d => d)
            .Select(d =>
            {
                var p = Compute(b, sales.Where(s => s.Date == d).ToList(), expenses.Where(e => e.Date == d).ToList(), shifts.Where(s => s.Date == d).ToList(), events);
                return new DaySales(d, p.NetSales, p.NetProfit);
            }).ToList();
        return new Snapshot { PnL = pnl, Events = byEvent, Days = days };
    }

    /// <summary>Everything recorded against one event, whatever its date.</summary>
    public async Task<PnL> ForEventAsync(Business b, Event ev)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var sales = await db.Sales.AsNoTracking().Where(s => s.BusinessId == b.Id && s.EventId == ev.Id).ToListAsync();
        var expenses = await db.Expenses.AsNoTracking().Where(e => e.BusinessId == b.Id && e.EventId == ev.Id).ToListAsync();
        var shifts = await db.Shifts.AsNoTracking().Where(s => s.BusinessId == b.Id && s.EventId == ev.Id).ToListAsync();
        return Compute(b, sales, expenses, shifts, new Dictionary<int, Event> { [ev.Id] = ev });
    }
}
