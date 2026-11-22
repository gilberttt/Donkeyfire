using System.Net;
using DonkeyFire.Web.Data;
using DonkeyFire.Web.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace DonkeyFire.Tests;

public class SquareTests
{
    // A card order (2 hot dogs + 1 item not on the menu, $1.00 tip, $0.36 fee) and a cash order with a returned hot dog.
    private const string OrdersJson = """
    {
      "orders": [
        {
          "id": "ORD1", "state": "COMPLETED", "location_id": "LOC", "closed_at": "2026-11-21T19:30:00Z",
          "line_items": [
            { "uid": "a", "name": "Hot Dog", "quantity": "2", "variation_name": "Regular", "total_money": { "amount": 900, "currency": "USD" } },
            { "uid": "b", "name": "Lemonade", "quantity": "1", "total_money": { "amount": 300, "currency": "USD" } }
          ],
          "tenders": [ { "type": "CARD", "amount_money": { "amount": 1300, "currency": "USD" }, "tip_money": { "amount": 100, "currency": "USD" },
                         "processing_fee_money": { "amount": 36, "currency": "USD" } } ],
          "total_tip_money": { "amount": 100, "currency": "USD" }
        },
        {
          "id": "ORD2", "state": "COMPLETED", "location_id": "LOC", "closed_at": "2026-11-22T03:10:00Z",
          "returns": [ { "uid": "r", "return_line_items": [ { "uid": "c", "name": "Hot Dog", "quantity": "1", "total_money": { "amount": 450, "currency": "USD" } } ] } ],
          "tenders": [ { "type": "CASH", "amount_money": { "amount": 0, "currency": "USD" } } ]
        },
        { "id": "ORD3", "state": "OPEN", "line_items": [ { "uid": "x", "name": "Hot Dog", "quantity": "5", "total_money": { "amount": 2250 } } ] }
      ]
    }
    """;

    private sealed class FakeSquare(string body) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }

    private static async Task<(TestDb Db, int BusinessId, int BunId)> Setup()
    {
        var f = new TestDb();
        await using var db = f.CreateDbContext();
        var b = new Business { Name = "Donkey Fire", SquareLocationId = "LOC", SquareTimeZone = "America/Chicago", SquareSyncFrom = new DateOnly(2026, 11, 1) };
        db.Businesses.Add(b);
        await db.SaveChangesAsync();
        var bun = new InventoryItem { BusinessId = b.Id, Name = "Bun", UnitCost = 0.25m, OnHand = 10 };
        db.MenuItems.Add(new MenuItem { BusinessId = b.Id, Name = "Hot dog", Price = 4.5m, Recipe = [new RecipeLine { InventoryItem = bun, Quantity = 1 }] });
        db.MenuItems.Add(new MenuItem { BusinessId = b.Id, Name = "Lemon shake-up", Price = 3m });
        db.Events.Add(new Event { BusinessId = b.Id, Name = "Fair", StartDate = new DateOnly(2026, 11, 21) });
        await db.SaveChangesAsync();
        return (f, b.Id, bun.Id);
    }

    private static SquareSync NewSync(TestDb f, FakeSquare handler)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection([new("Square:AccessToken", "test-token")]).Build();
        return new SquareSync(new SquareClient(new HttpClient(handler), config), f, new SalesService(f), NullLogger<SquareSync>.Instance);
    }

    [Fact]
    public async Task Sync_imports_lines_with_menu_match_tips_fees_returns_and_local_dates()
    {
        var (f, bid, bunId) = await Setup();
        using var _ = f;
        var handler = new FakeSquare(OrdersJson);

        var result = await NewSync(f, handler).SyncAsync(bid);

        Assert.Equal(3, result.LinesAdded);
        Assert.Equal(["Lemonade"], result.Unmatched);
        var req = Assert.Single(handler.Requests);
        Assert.Equal("Bearer test-token", req.Headers.Authorization!.ToString());
        Assert.EndsWith("/v2/orders/search", req.RequestUri!.ToString());

        await using var db = f.CreateDbContext();
        var sales = await db.Sales.AsNoTracking().OrderBy(s => s.Id).ToListAsync();
        var dogs = sales[0];
        Assert.Equal(("Hot dog", 2m, 4.5m, PaymentMethod.Card, SaleSource.Square), (dogs.Description, dogs.Quantity, dogs.UnitPrice, dogs.Payment, dogs.Source));
        Assert.NotNull(dogs.MenuItemId);
        Assert.Equal(0.25m, dogs.UnitCost);
        Assert.Equal(0.27m, dogs.Fee);              // 9/12 of the $0.36 fee
        Assert.Equal(0.75m, dogs.Tip);
        Assert.Equal(new DateOnly(2026, 11, 21), dogs.Date);
        Assert.NotNull(dogs.EventId);               // the fair was running that day

        Assert.Null(sales[1].MenuItemId);           // Lemonade isn't on the menu yet
        var ret = sales[2];
        Assert.Equal((-1m, PaymentMethod.Cash), (ret.Quantity, ret.Payment));
        Assert.Equal(new DateOnly(2026, 11, 21), ret.Date);   // 03:10 UTC is still the 21st in Alabama

        Assert.Equal(10 - 2 + 1, (await db.InventoryItems.FindAsync(bunId))!.OnHand);
        Assert.NotNull((await db.Businesses.FindAsync(bid))!.SquareLastSyncAt);
    }

    [Fact]
    public async Task Syncing_again_adds_nothing_and_fills_in_fees_that_arrived_late()
    {
        var (f, bid, bunId) = await Setup();
        using var _ = f;
        var noFee = System.Text.RegularExpressions.Regex.Replace(OrdersJson, @",\s*""processing_fee_money"": \{[^}]*\}", "");
        Assert.DoesNotContain("processing_fee", noFee);
        await NewSync(f, new FakeSquare(noFee)).SyncAsync(bid);
        await using (var db = f.CreateDbContext())
            Assert.All(await db.Sales.Where(s => s.Payment == PaymentMethod.Card).ToListAsync(), s => Assert.Null(s.Fee));

        var again = await NewSync(f, new FakeSquare(OrdersJson)).SyncAsync(bid);

        Assert.Equal(0, again.LinesAdded);
        Assert.Equal(2, again.FeesUpdated);
        await using var read = f.CreateDbContext();
        Assert.Equal(3, await read.Sales.CountAsync());
        Assert.Equal(9, (await read.InventoryItems.FindAsync(bunId))!.OnHand);
    }

    [Fact]
    public async Task Adding_a_square_name_to_a_menu_item_links_past_sales()
    {
        var (f, bid, _) = await Setup();
        using var _ = f;
        await NewSync(f, new FakeSquare(OrdersJson)).SyncAsync(bid);
        await using (var db = f.CreateDbContext())
        {
            var shake = await db.MenuItems.FirstAsync(m => m.Name == "Lemon shake-up");
            shake.SquareNames = "Lemonade";
            await db.SaveChangesAsync();
        }

        var linked = await new SalesService(f).RematchAsync(bid);

        Assert.Equal(1, linked);
        await using var read = f.CreateDbContext();
        Assert.Equal(0, await read.Sales.CountAsync(s => s.MenuItemId == null));
    }

    [Fact]
    public void Square_fees_replace_the_estimate_only_for_the_sales_that_have_them()
    {
        var b = new Business { CardFeePercent = 3 };
        var p = BusinessMetrics.Compute(b,
            [new Sale { Quantity = 1, UnitPrice = 100, Payment = PaymentMethod.Card, Fee = 2.60m, Tip = 5 },
             new Sale { Quantity = 1, UnitPrice = 100, Payment = PaymentMethod.Card }],
            [], [], new Dictionary<int, Event>());
        Assert.Equal(5.60m, p.CardFees);
        Assert.Equal(5, p.TipsCollected);
        Assert.Equal(200, p.NetSales);
    }

    [Fact]
    public void Menu_matcher_ignores_case_extra_spaces_and_uses_square_names()
    {
        var m = new MenuMatcher([new MenuItem { Id = 1, Name = "Hot dog", SquareNames = "Footlong Dog\nDog (Large)" }]);
        Assert.Equal(1, m.Find("HOT  DOG")?.Id);
        Assert.Equal(1, m.Find("dog (large)")?.Id);
        Assert.Null(m.Find("Nachos"));
    }
}
