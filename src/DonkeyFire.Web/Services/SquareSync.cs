using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using DonkeyFire.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DonkeyFire.Web.Services;

// ---------- Square API shapes (only the fields the app uses). Money amounts are in cents. ----------

public record SquareMoney(long Amount, string? Currency)
{
    public decimal Dollars => Amount / 100m;
}

public record SquareLocation(string Id, string? Name, string? Timezone, string? Status);
public record SquareLineItem(string? Uid, string? Name, string? Quantity, string? CatalogObjectId, string? VariationName,
    SquareMoney? TotalMoney, SquareMoney? GrossSalesMoney);
public record SquareReturnLineItem(string? Uid, string? Name, string? Quantity, string? VariationName, SquareMoney? TotalMoney);
public record SquareReturn(string? Uid, string? SourceOrderId, List<SquareReturnLineItem>? ReturnLineItems);
public record SquareTender(string? Type, SquareMoney? AmountMoney, SquareMoney? TipMoney, SquareMoney? ProcessingFeeMoney);
public record SquareOrder(string Id, string? State, string? ClosedAt, string? LocationId, List<SquareLineItem>? LineItems,
    List<SquareTender>? Tenders, List<SquareReturn>? Returns, SquareMoney? TotalTipMoney, SquareMoney? TotalServiceChargeMoney);

file record LocationsResponse(List<SquareLocation>? Locations, List<SquareError>? Errors);
file record SearchOrdersResponse(List<SquareOrder>? Orders, string? Cursor, List<SquareError>? Errors);
file record SquareError(string? Category, string? Code, string? Detail);

public class SquareException(string message) : Exception(message);

/// <summary>
/// Thin Square REST client. Uses a personal access token from the Square Developer Dashboard
/// (Square:AccessToken, or Square__AccessToken when hosted); never stored in the database.
/// </summary>
public class SquareClient(HttpClient http, IConfiguration config)
{
    public const string ApiVersion = "2026-11-18";

    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public bool IsConfigured => !string.IsNullOrWhiteSpace(config["Square:AccessToken"]);
    public bool IsSandbox => string.Equals(config["Square:Environment"], "sandbox", StringComparison.OrdinalIgnoreCase);

    private HttpRequestMessage Request(HttpMethod method, string path, object? body = null)
    {
        if (!IsConfigured) throw new SquareException("Square isn't connected: the access token isn't set on the server.");
        var host = IsSandbox ? "https://connect.squareupsandbox.com" : "https://connect.squareup.com";
        var req = new HttpRequestMessage(method, host + path);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", config["Square:AccessToken"]);
        req.Headers.Add("Square-Version", ApiVersion);
        if (body is not null) req.Content = JsonContent.Create(body, options: Json);
        return req;
    }

    private async Task<T> SendAsync<T>(HttpRequestMessage req, CancellationToken ct)
    {
        using var res = await http.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            var detail = "";
            try { detail = JsonDocument.Parse(text).RootElement.GetProperty("errors")[0].GetProperty("detail").GetString() ?? ""; } catch (Exception) { }
            throw new SquareException(res.StatusCode switch
            {
                System.Net.HttpStatusCode.Unauthorized => "Square rejected the access token. Check Square__AccessToken (and Square__Environment for sandbox tokens).",
                System.Net.HttpStatusCode.Forbidden => "The Square token doesn't have permission to read orders and locations.",
                _ => $"Square returned {(int)res.StatusCode}. {detail}".Trim(),
            });
        }
        return JsonSerializer.Deserialize<T>(text, Json) ?? throw new SquareException("Square sent an empty response.");
    }

    public async Task<List<SquareLocation>> GetLocationsAsync(CancellationToken ct = default) =>
        (await SendAsync<LocationsResponse>(Request(HttpMethod.Get, "/v2/locations"), ct)).Locations ?? [];

    /// <summary>Completed orders closed in [from, to), oldest first, following every page.</summary>
    public async Task<List<SquareOrder>> SearchOrdersAsync(string locationId, DateTimeOffset from, DateTimeOffset to, CancellationToken ct = default)
    {
        var all = new List<SquareOrder>();
        string? cursor = null;
        do
        {
            var body = new
            {
                location_ids = new[] { locationId },
                cursor,
                limit = 500,
                query = new
                {
                    filter = new
                    {
                        state_filter = new { states = new[] { "COMPLETED" } },
                        date_time_filter = new { closed_at = new { start_at = from.ToString("o"), end_at = to.ToString("o") } },
                    },
                    sort = new { sort_field = "CLOSED_AT", sort_order = "ASC" },
                },
            };
            var page = await SendAsync<SearchOrdersResponse>(Request(HttpMethod.Post, "/v2/orders/search", body), ct);
            all.AddRange(page.Orders ?? []);
            cursor = string.IsNullOrEmpty(page.Cursor) ? null : page.Cursor;
        } while (cursor is not null);
        return all;
    }
}

public record SquareSyncResult(int Orders, int LinesAdded, int FeesUpdated, int Linked, IReadOnlyList<string> Unmatched)
{
    public string Summary => LinesAdded == 0 && FeesUpdated == 0 && Linked == 0
        ? $"Square is up to date ({Fmt.Plural(Orders, "order")} checked)."
        : $"Imported {Fmt.Plural(LinesAdded, "line")} from {Fmt.Plural(Orders, "order")}"
          + (FeesUpdated > 0 ? $", updated {Fmt.Plural(FeesUpdated, "fee")}" : "")
          + (Linked > 0 ? $", linked {Linked} to the menu" : "")
          + (Unmatched.Count > 0 ? $". Not on the menu: {string.Join(", ", Unmatched.Take(5))}{(Unmatched.Count > 5 ? "…" : "")}" : ".");
}

/// <summary>
/// Pulls completed Square orders into Sales. Each line becomes a sale (matched to a menu item by name, so it uses up
/// inventory); returns come in as negative quantities; the order's tips and processing fees are spread across its lines.
/// Safe to run any number of times: lines are keyed by order and line id.
/// </summary>
public class SquareSync(SquareClient square, IDbContextFactory<AppDbContext> dbf, SalesService sales, ILogger<SquareSync> log)
{
    private static readonly SemaphoreSlim Gate = new(1, 1);

    public bool IsConfigured => square.IsConfigured;

    public async Task<SquareSyncResult> SyncAsync(int businessId, CancellationToken ct = default)
    {
        await Gate.WaitAsync(ct);   // the button and the background timer never import at the same time
        try
        {
            await using var db = await dbf.CreateDbContextAsync(ct);
            var b = await db.Businesses.FirstAsync(x => x.Id == businessId, ct);
            if (string.IsNullOrEmpty(b.SquareLocationId)) throw new SquareException("Choose a Square location in Settings first.");
            var tz = TimeZoneOrDefault(b.SquareTimeZone);
            var now = DateTimeOffset.UtcNow;
            var from = b.SquareLastSyncAt is { } last
                ? new DateTimeOffset(DateTime.SpecifyKind(last, DateTimeKind.Utc)).AddDays(-1)
                : StartOfDay(b.SquareSyncFrom ?? DateOnly.FromDateTime(DateTime.Today.AddDays(-30)), tz);

            var orders = await square.SearchOrdersAsync(b.SquareLocationId, from, now, ct);
            var result = await ImportAsync(db, b, orders, tz, ct);
            b.SquareLastSyncAt = now.UtcDateTime;
            await db.SaveChangesAsync(ct);
            var linked = await sales.RematchAsync(businessId);
            log.LogInformation("Square sync for business {Id}: {Summary}", businessId, result.Summary);
            return result with { Linked = linked };
        }
        finally { Gate.Release(); }
    }

    /// <summary>Writes the orders into the database. Public for tests; the caller saves the business row.</summary>
    public static async Task<SquareSyncResult> ImportAsync(AppDbContext db, Business b, IReadOnlyList<SquareOrder> orders, TimeZoneInfo tz, CancellationToken ct = default)
    {
        var menu = new MenuMatcher(await db.MenuItems.Where(m => m.BusinessId == b.Id).Include(m => m.Recipe).ThenInclude(r => r.InventoryItem).ToListAsync(ct));
        var events = await db.Events.AsNoTracking().Where(e => e.BusinessId == b.Id && e.Status != EventStatus.Cancelled).ToListAsync(ct);
        var keys = orders.Select(o => o.Id + ":").ToList();
        var existing = (await db.Sales.Where(s => s.BusinessId == b.Id && s.Source == SaleSource.Square && s.ExternalId != null).ToListAsync(ct))
            .Where(s => keys.Any(k => s.ExternalId!.StartsWith(k, StringComparison.Ordinal))).ToDictionary(s => s.ExternalId!);

        int added = 0, feesUpdated = 0;
        var unmatched = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new List<(Sale Sale, MenuItem Item)>();

        foreach (var o in orders.Where(o => o.State == "COMPLETED"))
        {
            var date = LocalDate(o.ClosedAt, tz);
            // When events overlap (a festival weekend during baseball season), the one that started most recently is the more specific.
            var eventId = events.Where(e => e.StartDate <= date && e.LastDate >= date).OrderByDescending(e => e.StartDate).ThenBy(e => e.LastDate)
                .Select(e => (int?)e.Id).FirstOrDefault();
            var tenders = o.Tenders ?? [];
            var payment = PaymentOf(tenders);
            var anyFee = tenders.Any(t => t.ProcessingFeeMoney is not null);
            var fee = tenders.Sum(t => t.ProcessingFeeMoney?.Dollars ?? 0);
            var tip = o.TotalTipMoney?.Dollars ?? tenders.Sum(t => t.TipMoney?.Dollars ?? 0);

            // Lines: sales, then returns (negative), then any service charge as its own line.
            var lines = new List<(string Key, string Name, decimal Qty, decimal Total)>();
            foreach (var li in o.LineItems ?? [])
                lines.Add(($"{o.Id}:{li.Uid ?? li.Name}", LineName(li.Name, li.VariationName, menu), Qty(li.Quantity), li.TotalMoney?.Dollars ?? 0));
            foreach (var r in o.Returns ?? [])
                foreach (var rl in r.ReturnLineItems ?? [])
                    lines.Add(($"{o.Id}:ret:{rl.Uid ?? rl.Name}", LineName(rl.Name, rl.VariationName, menu), -Qty(rl.Quantity), -(rl.TotalMoney?.Dollars ?? 0)));
            if (o.TotalServiceChargeMoney is { Amount: > 0 } svc) lines.Add(($"{o.Id}:svc", "Service charge", 1, svc.Dollars));
            if (lines.Count == 0) continue;

            var orderTotal = lines.Sum(l => Math.Abs(l.Total));
            foreach (var l in lines)
            {
                var share = orderTotal == 0 ? 1m / lines.Count : Math.Abs(l.Total) / orderTotal;
                var lineFee = anyFee ? Math.Round(fee * share, 2) : (decimal?)null;
                if (existing.TryGetValue(l.Key, out var had))
                {
                    // Fees show up a little after the sale; fill them in on a later sync.
                    if (had.Fee is null && lineFee is not null) { had.Fee = lineFee; feesUpdated++; }
                    continue;
                }
                var m = menu.Find(l.Name);
                if (m is null && l.Name != "Service charge") unmatched.Add(l.Name);
                var sale = new Sale
                {
                    BusinessId = b.Id, Date = date, EventId = eventId, MenuItemId = m?.Id, Description = m?.Name ?? l.Name,
                    Quantity = l.Qty, UnitPrice = l.Qty == 0 ? 0 : Math.Round(l.Total / l.Qty, 4), UnitCost = m is null ? 0 : Math.Round(BusinessMetrics.RecipeCost(m), 4),
                    Payment = payment, Source = SaleSource.Square, ExternalId = l.Key, Fee = lineFee, Tip = Math.Round(tip * share, 2),
                    Note = m is null || string.Equals(m.Name, l.Name, StringComparison.OrdinalIgnoreCase) ? null : $"Square: {l.Name}",
                };
                db.Sales.Add(sale);
                existing[l.Key] = sale;
                if (m is not null) pending.Add((sale, m));
                added++;
            }
        }
        await db.SaveChangesAsync(ct);
        foreach (var (sale, m) in pending) SalesService.UseIngredients(db, sale, m);
        await db.SaveChangesAsync(ct);
        return new SquareSyncResult(orders.Count, added, feesUpdated, 0, unmatched.ToList());
    }

    /// <summary>"Hot Dog" with variation "Regular" matches a menu item named either "Hot Dog (Regular)", "Hot Dog Regular" or "Hot Dog".</summary>
    private static string LineName(string? name, string? variation, MenuMatcher menu)
    {
        var n = string.IsNullOrWhiteSpace(name) ? "Custom amount" : name.Trim();
        if (string.IsNullOrWhiteSpace(variation) || variation.Trim().Equals("Regular", StringComparison.OrdinalIgnoreCase) && menu.Find(n) is not null) return n;
        var v = variation.Trim();
        foreach (var candidate in new[] { $"{n} ({v})", $"{n} {v}" })
            if (menu.Find(candidate) is not null) return candidate;
        return menu.Find(n) is not null ? n : $"{n} ({v})";
    }

    private static PaymentMethod PaymentOf(List<SquareTender> tenders) =>
        tenders.OrderByDescending(t => t.AmountMoney?.Amount ?? 0).FirstOrDefault()?.Type switch
        {
            "CARD" => PaymentMethod.Card,
            "CASH" => PaymentMethod.Cash,
            null => PaymentMethod.Other,
            _ => PaymentMethod.Other,
        };

    private static decimal Qty(string? q) => decimal.TryParse(q, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : 1;

    public static TimeZoneInfo TimeZoneOrDefault(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id) && TimeZoneInfo.TryFindSystemTimeZoneById(id, out var tz)) return tz;
        return TimeZoneInfo.TryFindSystemTimeZoneById("America/Chicago", out var central) ? central : TimeZoneInfo.Utc;
    }

    private static DateOnly LocalDate(string? closedAt, TimeZoneInfo tz) =>
        DateTimeOffset.TryParse(closedAt, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var t)
            ? DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(t, tz).DateTime)
            : DateOnly.FromDateTime(DateTime.Today);

    private static DateTimeOffset StartOfDay(DateOnly d, TimeZoneInfo tz)
    {
        var local = d.ToDateTime(TimeOnly.MinValue);
        return new DateTimeOffset(local, tz.GetUtcOffset(local));
    }
}

/// <summary>Syncs every connected business on a timer when Square:AutoSyncMinutes is set (e.g. 15). Off by default.</summary>
public class SquareAutoSyncService(IServiceProvider services, IConfiguration config, ILogger<SquareAutoSyncService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var minutes = config.GetValue("Square:AutoSyncMinutes", 0);
        if (minutes <= 0 || string.IsNullOrWhiteSpace(config["Square:AccessToken"])) return;
        var interval = TimeSpan.FromMinutes(Math.Max(5, minutes));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(interval, stoppingToken);
                using var scope = services.CreateScope();
                var dbf = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>();
                var sync = scope.ServiceProvider.GetRequiredService<SquareSync>();
                await using var db = await dbf.CreateDbContextAsync(stoppingToken);
                foreach (var id in await db.Businesses.Where(b => b.SquareLocationId != null).Select(b => b.Id).ToListAsync(stoppingToken))
                {
                    try { await sync.SyncAsync(id, stoppingToken); }
                    catch (SquareException ex) { log.LogWarning("Square auto-sync for business {Id} skipped: {Message}", id, ex.Message); }
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { log.LogError(ex, "Square auto-sync failed; will retry"); }
        }
    }
}
