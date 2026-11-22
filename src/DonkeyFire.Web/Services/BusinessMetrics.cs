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

public record TaxLine(string Name, TaxLevel Level, decimal Percent, string? FiledWith, decimal Amount);

/// <summary>
/// The revenue share owed to a venue partner: a percent of (gross sales − sales tax − card fees − cost of goods − labor) for
/// sales at the partner's venues, never below zero. Worked out per calendar month; a period of several months adds up each
/// month's share. <see cref="SalesNotCounted"/> is sales elsewhere or not tied to any event.
/// </summary>
public record RevenueShare(string Partner, decimal Percent, CogsBasis CogsBasis, decimal GrossSales, decimal SalesTax, decimal Fees, decimal Cogs,
    decimal Labor, decimal Owed, decimal SalesNotCounted, decimal SalesWithoutEvent)
{
    public decimal Base => GrossSales - SalesTax - Fees - Cogs - Labor;
}

/// <summary>
/// Profit and loss for a period or an event. Profit is cash-basis: net sales less expenses, labor, card fees, event
/// commissions and the revenue share. Food cost is the recipe cost of what was sold; it is a ratio to watch, not subtracted
/// again (the ingredients were already counted when the purchase was logged as an expense). Tax and revenue-share
/// payments settle amounts already accounted for, so they are reported but not counted as expenses.
/// </summary>
public class PnL
{
    public decimal GrossSales { get; init; }
    public decimal CashSales { get; init; }
    public decimal CardSales { get; init; }
    public decimal SalesTax { get; init; }
    public IReadOnlyList<TaxLine> Taxes { get; init; } = [];
    public decimal NetSales { get; init; }
    public decimal CardFees { get; init; }
    /// <summary>True when at least some card fees are Square's actual fees rather than the estimate.</summary>
    public bool ActualFees { get; init; }
    /// <summary>Tips customers added on Square. They belong to the crew, so they're not sales.</summary>
    public decimal TipsCollected { get; init; }
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
    /// <summary>Null when the business has no revenue-share agreement.</summary>
    public RevenueShare? Share { get; set; }
    public decimal SalesTaxPaid { get; init; }
    public decimal RevenueSharePaid { get; init; }

    public decimal Labor => Wages + LaborBurden;
    public decimal ShareOwed => Share?.Owed ?? 0;
    public decimal NetProfit => NetSales - CardFees - Commissions - Expenses - Labor - ShareOwed;
    public double FoodCostPct => Ratio(FoodCost);
    public double LaborPct => Ratio(Labor);
    public double ProfitMarginPct => Ratio(NetProfit);
    public decimal SalesPerLaborHour => LaborHours == 0 ? 0 : NetSales / LaborHours;

    private double Ratio(decimal part) => NetSales == 0 ? 0 : (double)(part / NetSales) * 100;
}

/// <summary>
/// The sales taxes that apply to a sale. With no tax rates set up, the business's single rate is used. Sales at an event
/// outside the city limits skip city taxes.
/// </summary>
public class TaxPlan
{
    private readonly IReadOnlyList<TaxRate> rates;

    public TaxPlan(Business b, IEnumerable<TaxRate>? rates)
    {
        var list = rates?.Where(r => r.Percent > 0).OrderBy(r => r.SortOrder).ThenBy(r => r.Level).ToList() ?? [];
        this.rates = list.Count > 0 ? list
            : b.SalesTaxPercent > 0 ? [new TaxRate { Name = "Sales tax", Level = TaxLevel.Other, Percent = b.SalesTaxPercent }] : [];
    }

    public IReadOnlyList<TaxRate> Rates => rates;
    public decimal TotalPercent => rates.Sum(r => r.Percent);
    public IEnumerable<TaxRate> For(Event? e) => e?.OutsideCityLimits == true ? rates.Where(r => r.Level != TaxLevel.City) : rates;
}

public record EventResult(Event Event, PnL PnL);

public class BusinessMetrics(IDbContextFactory<AppDbContext> dbf)
{
    public static decimal RecipeCost(MenuItem m) =>
        m.Recipe.Count > 0 ? m.Recipe.Sum(r => r.Quantity * (r.InventoryItem?.UnitCost ?? 0)) : m.ManualCost ?? 0;

    public static double FoodCostPct(MenuItem m) => m.Price == 0 ? 0 : (double)(RecipeCost(m) / m.Price) * 100;

    private static readonly ExpenseCategory[] Settlements = [ExpenseCategory.RevenueSharePayment, ExpenseCategory.SalesTaxPayment];
    private static readonly ExpenseCategory[] GoodsCategories = [ExpenseCategory.FoodAndDrink, ExpenseCategory.PaperAndSupplies];

    /// <summary>Builds a P&amp;L from already-loaded rows. Everything else in this class funnels through here.</summary>
    public static PnL Compute(Business b, IReadOnlyCollection<Sale> sales, IReadOnlyCollection<Expense> expenses, IReadOnlyCollection<Shift> shifts,
        IReadOnlyDictionary<int, Event> events, TaxPlan? tax = null)
    {
        tax ??= new TaxPlan(b, null);
        Event? EventOf(int? id) => id is { } i && events.TryGetValue(i, out var e) ? e : null;

        // Tax per sale, so an event outside the city limits leaves out city tax; then split by jurisdiction.
        var byRate = new Dictionary<TaxRate, decimal>();
        decimal TaxOn(Sale s)
        {
            var applicable = tax.For(EventOf(s.EventId)).ToList();
            var pct = applicable.Sum(r => r.Percent);
            if (pct <= 0) return 0;
            var t = b.PricesIncludeTax ? s.Total * pct / (100 + pct) : s.Total * pct / 100;
            foreach (var r in applicable) byRate[r] = byRate.GetValueOrDefault(r) + t * r.Percent / pct;
            return t;
        }
        var taxBySale = sales.ToDictionary(s => s, TaxOn, (IEqualityComparer<Sale>)ReferenceEqualityComparer.Instance);

        var gross = sales.Sum(s => s.Total);
        var taxTotal = taxBySale.Values.Sum();
        var net = b.PricesIncludeTax ? gross - taxTotal : gross;
        var card = sales.Where(s => s.Payment == PaymentMethod.Card).Sum(s => s.Total);
        var commissions = sales.Where(s => EventOf(s.EventId)?.CommissionPercent > 0)
            .Sum(s => (b.PricesIncludeTax ? s.Total - taxBySale[s] : s.Total) * EventOf(s.EventId)!.CommissionPercent / 100);
        var wages = shifts.Sum(s => s.Wages);
        var spending = expenses.Where(e => !Settlements.Contains(e.Category)).ToList();

        return new PnL
        {
            GrossSales = gross,
            CardSales = card,
            CashSales = sales.Where(s => s.Payment == PaymentMethod.Cash).Sum(s => s.Total),
            SalesTax = Round(taxTotal),
            Taxes = tax.Rates.Select(r => new TaxLine(r.Name, r.Level, r.Percent, r.FiledWith, Round(byRate.GetValueOrDefault(r)))).ToList(),
            NetSales = Round(net),
            CardFees = Round(sales.Sum(s => FeeOf(b, s))),
            ActualFees = sales.Any(s => s.Fee is not null),
            TipsCollected = sales.Sum(s => s.Tip),
            Commissions = Round(commissions),
            FoodCost = Round(sales.Sum(s => s.Quantity * s.UnitCost)),
            Wages = wages,
            LaborBurden = Round(wages * b.LaborBurdenPercent / 100),
            Tips = shifts.Sum(s => s.Tips),
            LaborHours = shifts.Sum(s => s.Hours),
            Expenses = spending.Sum(e => e.Amount),
            ItemsSold = sales.Where(s => s.MenuItemId != null).Sum(s => s.Quantity),
            ExpensesByCategory = spending.GroupBy(e => e.Category).Select(g => (g.Key, g.Sum(e => e.Amount))).OrderByDescending(x => x.Item2).ToList(),
            Items = sales.Where(s => s.MenuItemId != null).GroupBy(s => s.MenuItemId)
                .Select(g => new ItemSales(g.OrderByDescending(s => s.Date).First().Description, g.Sum(s => s.Quantity), g.Sum(s => s.Total), g.Sum(s => s.Quantity * s.UnitCost)))
                .OrderByDescending(i => i.Revenue).ToList(),
            Share = ShareOf(b, sales, expenses, shifts, EventOf, taxBySale),
            SalesTaxPaid = expenses.Where(e => e.Category == ExpenseCategory.SalesTaxPayment).Sum(e => e.Amount),
            RevenueSharePaid = expenses.Where(e => e.Category == ExpenseCategory.RevenueSharePayment).Sum(e => e.Amount),
        };
    }

    /// <summary>Square sales carry their real fee; anything else paid by card gets the estimated rate.</summary>
    public static decimal FeeOf(Business b, Sale s) => s.Fee ?? (s.Payment == PaymentMethod.Card ? s.Total * b.CardFeePercent / 100 : 0);

    /// <summary>
    /// The partner's share of these rows. Only sales at events marked as the partner's venue count. Costs tied to those
    /// events count in full; costs not tied to any event (a Sam's Club run, a shift with no event) are shared out by the
    /// venue's portion of the month's pre-tax sales. Costs tied to other events don't count. Labor is wages plus payroll
    /// burden (tips aren't a business cost).
    /// </summary>
    private static RevenueShare? ShareOf(Business b, IReadOnlyCollection<Sale> sales, IReadOnlyCollection<Expense> expenses, IReadOnlyCollection<Shift> shifts,
        Func<int?, Event?> eventOf, IReadOnlyDictionary<Sale, decimal> taxBySale)
    {
        if (string.IsNullOrWhiteSpace(b.RevenueSharePartner) || b.RevenueSharePercent <= 0) return null;
        bool Covered(int? eventId) => eventOf(eventId)?.AtPartnerVenue == true;
        var atVenue = sales.Where(s => Covered(s.EventId)).ToList();
        var preTaxAll = sales.Sum(s => s.Total - taxBySale[s]);
        var portion = preTaxAll <= 0 ? 0 : Math.Clamp(atVenue.Sum(s => s.Total - taxBySale[s]) / preTaxAll, 0, 1);
        decimal Allocated<T>(IEnumerable<T> rows, Func<T, int?> eventId, Func<T, decimal> amount) =>
            rows.Where(r => Covered(eventId(r))).Sum(amount) + rows.Where(r => eventId(r) is null).Sum(amount) * portion;

        var cogs = b.RevenueShareCogsBasis == CogsBasis.RecipeCost
            ? atVenue.Sum(s => s.Quantity * s.UnitCost)
            : Allocated(expenses.Where(e => GoodsCategories.Contains(e.Category)), e => e.EventId, e => e.Amount);
        var wages = Allocated(shifts, s => s.EventId, s => s.Wages);
        var labor = wages + wages * b.LaborBurdenPercent / 100;
        var gross = atVenue.Sum(s => s.Total);
        var tax = atVenue.Sum(s => taxBySale[s]);
        var fees = atVenue.Sum(s => FeeOf(b, s));
        var baseAmount = gross - tax - fees - cogs - labor;
        return new RevenueShare(b.RevenueSharePartner.Trim(), b.RevenueSharePercent, b.RevenueShareCogsBasis,
            Round(gross), Round(tax), Round(fees), Round(cogs), Round(labor), Round(Math.Max(0, baseAmount) * b.RevenueSharePercent / 100),
            Round(sales.Sum(s => s.Total) - gross), Round(sales.Where(s => s.EventId is null).Sum(s => s.Total)));
    }

    private static decimal Round(decimal v) => Math.Round(v, 2, MidpointRounding.AwayFromZero);

    public static DateOnly MonthOf(DateOnly d) => new(d.Year, d.Month, 1);

    public class Snapshot
    {
        public required PnL PnL { get; init; }
        public required IReadOnlyList<EventResult> Events { get; init; }
        public required IReadOnlyList<DaySales> Days { get; init; }
    }

    private record Rows(List<Sale> Sales, List<Expense> Expenses, List<Shift> Shifts, Dictionary<int, Event> Events, TaxPlan Tax);

    private async Task<Rows> LoadAsync(Business b, DateOnly from, DateOnly to)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return new Rows(
            await db.Sales.AsNoTracking().Where(s => s.BusinessId == b.Id && s.Date >= from && s.Date <= to).ToListAsync(),
            await db.Expenses.AsNoTracking().Where(e => e.BusinessId == b.Id && e.Date >= from && e.Date <= to).ToListAsync(),
            await db.Shifts.AsNoTracking().Where(s => s.BusinessId == b.Id && s.Date >= from && s.Date <= to).ToListAsync(),
            await db.Events.AsNoTracking().Where(e => e.BusinessId == b.Id).ToDictionaryAsync(e => e.Id),
            new TaxPlan(b, await db.TaxRates.AsNoTracking().Where(r => r.BusinessId == b.Id).ToListAsync()));
    }

    public async Task<TaxPlan> TaxPlanAsync(Business b)
    {
        await using var db = await dbf.CreateDbContextAsync();
        return new TaxPlan(b, await db.TaxRates.AsNoTracking().Where(r => r.BusinessId == b.Id).ToListAsync());
    }

    /// <summary>The revenue share is settled monthly, so a longer period adds up each month's share (a bad month doesn't offset a good one).</summary>
    private static RevenueShare? MonthlyShare(Business b, Rows r)
    {
        var months = r.Sales.Select(s => MonthOf(s.Date)).Concat(r.Expenses.Select(e => MonthOf(e.Date))).Concat(r.Shifts.Select(s => MonthOf(s.Date))).Distinct().ToList();
        if (months.Count <= 1) return null;
        var parts = months.Select(m => Compute(b, r.Sales.Where(s => MonthOf(s.Date) == m).ToList(), r.Expenses.Where(e => MonthOf(e.Date) == m).ToList(),
            r.Shifts.Where(s => MonthOf(s.Date) == m).ToList(), r.Events, r.Tax).Share).Where(x => x is not null).Select(x => x!).ToList();
        if (parts.Count == 0) return null;
        return parts[0] with
        {
            GrossSales = parts.Sum(p => p.GrossSales), SalesTax = parts.Sum(p => p.SalesTax), Fees = parts.Sum(p => p.Fees), Cogs = parts.Sum(p => p.Cogs),
            Labor = parts.Sum(p => p.Labor), Owed = parts.Sum(p => p.Owed), SalesNotCounted = parts.Sum(p => p.SalesNotCounted),
            SalesWithoutEvent = parts.Sum(p => p.SalesWithoutEvent),
        };
    }

    public async Task<Snapshot> ForPeriodAsync(Business b, Period period)
    {
        var r = await LoadAsync(b, period.From, period.To);
        var pnl = Compute(b, r.Sales, r.Expenses, r.Shifts, r.Events, r.Tax);
        if (MonthlyShare(b, r) is { } monthly) pnl.Share = monthly;

        var eventIds = r.Sales.Select(s => s.EventId).Concat(r.Expenses.Select(e => e.EventId)).Concat(r.Shifts.Select(s => s.EventId))
            .Where(id => id is not null).Select(id => id!.Value).Distinct();
        var byEvent = eventIds.Where(r.Events.ContainsKey).Select(id => new EventResult(r.Events[id], Compute(b,
                r.Sales.Where(s => s.EventId == id).ToList(), r.Expenses.Where(e => e.EventId == id).ToList(), r.Shifts.Where(s => s.EventId == id).ToList(), r.Events, r.Tax)))
            .OrderByDescending(x => x.Event.StartDate).ToList();
        var days = r.Sales.Select(s => s.Date).Concat(r.Expenses.Select(e => e.Date)).Concat(r.Shifts.Select(s => s.Date)).Distinct().OrderBy(d => d)
            .Select(d =>
            {
                var p = Compute(b, r.Sales.Where(s => s.Date == d).ToList(), r.Expenses.Where(e => e.Date == d).ToList(), r.Shifts.Where(s => s.Date == d).ToList(), r.Events, r.Tax);
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
        var tax = new TaxPlan(b, await db.TaxRates.AsNoTracking().Where(r => r.BusinessId == b.Id).ToListAsync());
        return Compute(b, sales, expenses, shifts, new Dictionary<int, Event> { [ev.Id] = ev }, tax);
    }

    /// <summary>What's owed and paid for one month. Payments are matched by the month they cover, whenever they were made.</summary>
    public record MonthObligations(DateOnly Month, PnL PnL, decimal SalesTaxPaid, decimal SharePaid)
    {
        public decimal SalesTaxDue => PnL.SalesTax - SalesTaxPaid;
        public decimal ShareDue => PnL.ShareOwed - SharePaid;
        /// <summary>Alabama sales tax returns are due the 20th of the following month.</summary>
        public DateOnly TaxDueDate => Month.AddMonths(1).AddDays(19);
    }

    public async Task<List<MonthObligations>> MonthsAsync(Business b, DateOnly firstMonth, DateOnly lastMonth)
    {
        var r = await LoadAsync(b, firstMonth, lastMonth.AddMonths(1).AddDays(-1));
        await using var db = await dbf.CreateDbContextAsync();
        var payments = await db.Expenses.AsNoTracking()
            .Where(e => e.BusinessId == b.Id && (e.Category == ExpenseCategory.SalesTaxPayment || e.Category == ExpenseCategory.RevenueSharePayment)).ToListAsync();
        var list = new List<MonthObligations>();
        for (var m = lastMonth; m >= firstMonth; m = m.AddMonths(-1))
        {
            var month = m;
            var pnl = Compute(b, r.Sales.Where(s => MonthOf(s.Date) == month).ToList(), r.Expenses.Where(e => MonthOf(e.Date) == month).ToList(),
                r.Shifts.Where(s => MonthOf(s.Date) == month).ToList(), r.Events, r.Tax);
            decimal Paid(ExpenseCategory c) => payments.Where(e => e.Category == c && (e.ForMonth ?? MonthOf(e.Date).AddMonths(-1)) == month).Sum(e => e.Amount);
            list.Add(new MonthObligations(month, pnl, Paid(ExpenseCategory.SalesTaxPayment), Paid(ExpenseCategory.RevenueSharePayment)));
        }
        return list;
    }
}
