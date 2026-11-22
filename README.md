# Donkey Fire Concessions

A web app for running a concession business: sales by event, expenses with receipt photos, inventory tied to recipes, menu costing, employees and shifts, and profit for every day the window is open. Built with Blazor (.NET 10, interactive server rendering), EF Core and SQLite, on the same base as BuildTrack.

## What's in it

| Area | What it does |
| --- | --- |
| **Dashboard** | Net sales, net profit, expenses, labor, food cost %, sales per labor hour, low stock and the next event for any period (today, this week, this month, last month, year to date, last 12 months, all time). Profit & loss statement, sales by day, top sellers and results by event. Print or export CSV. |
| **Sales** | Phone-friendly ring-up: tap + / − on menu items, pick cash, card or other, and record. Each sale saves the item's recipe cost and uses up its ingredients from inventory; deleting a sale puts them back. Lump sums cover days you only have a card-reader or cash-box total. Defaults to the event running today. |
| **Expenses** | Date, category, vendor, event, payment method, notes and a receipt photo (opens the phone camera). Filter by period, category, event or text; CSV export. |
| **Events** | Festivals, games, fairs, markets and catering jobs with dates, location, contact, booth fee, organizer's cut of sales and a sales goal. Each event page shows its own P&L, what sold, the crew that worked it, and a one-tap "record booth fee as expense". |
| **Menu & recipes** | Price, category, and a recipe built from inventory items, so each item shows its cost, food cost % and margin against your target. It suggests the price that hits the target. Items without a recipe can take a manual cost. Hide items to keep them off the ring-up screen. |
| **Inventory** | Items with unit, cost per unit, on hand, reorder level, supplier and storage location. Add stock (updates the unit cost), use up or waste, count to true up, and full history per item. Low-stock filter, a copyable shopping list grouped by supplier, CSV export. |
| **Employees & shifts** | Crew with hourly rates. Log one shift for several people at once, with hours, flat pay, tips paid out and an optional event. Shifts keep the rate they were logged at. A "To pay" tab totals what each person is owed and marks them paid. |
| **Settings** | Sales tax rate (and whether prices include it), card fee %, payroll burden %, food cost target. Members and roles (Viewer, Editor, Owner) by Google email invite. Delete the business. |

### How the money is counted
- **Net sales** = gross sales less sales tax when menu prices include tax. When tax is added on top, it's shown as owed but doesn't reduce net sales.
- **Net profit** = net sales − estimated card fees − event commissions − labor (wages + payroll burden) − expenses. This is cash-basis: food is counted when you log the purchase as an expense.
- **Food cost** = recipe cost of what was sold. It's a ratio to watch, not subtracted again.
- **Tips** come from the tip jar, so they're tracked per shift but aren't a business cost.
- Card fees are estimated from card sales. Set the rate to 0 in Settings if you log processor fees as expenses instead.

## Run it locally

Requirements: the [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
cd src/DonkeyFire.Web
dotnet run
```

Then open http://localhost:5188. The database (`App_Data/donkeyfire.db`) and uploads (`App_Data/uploads`) are created on first run, and migrations apply automatically. In Development with no Google credentials, the login page shows a **Developer sign-in** button. When you create the business, tick "Start with a sample concession menu" to get a menu and inventory to edit.

Tests: `dotnet test tests/DonkeyFire.Tests`

## Google sign-in, configuration and deployment

These work exactly like BuildTrack:

- **Google sign-in:** create an OAuth client ID (Web application) with redirect URIs `http://localhost:5188/signin-google` and `https://<your-domain>/signin-google`. Set `Authentication:Google:ClientId` / `ClientSecret` with user-secrets locally or `Authentication__Google__ClientId` / `Authentication__Google__ClientSecret` when hosted.
- **Allowlist:** `Authentication__AllowedEmails` (comma-separated) is required in Production when Google is configured; invited members can also sign in.
- **Settings:** `ConnectionStrings:Default`, `Storage:Root`, `Hosting:BehindProxy`, `Backup:Enabled` / `IntervalHours` / `Keep` / `Directory` (daily snapshots plus one before any pending migration).

### Deploy to Fly.io
The repo includes a `Dockerfile` and `fly.toml` (app `donkeyfire`, volume `donkeyfire_data` mounted at `/data`). Run exactly one machine.

1. `fly apps create donkeyfire` (change `app` in `fly.toml` if the name is taken)
2. `fly volumes create donkeyfire_data --size 1 --region iad --app donkeyfire`
3. Add `https://donkeyfire.fly.dev/signin-google` (and your custom domain) to the Google OAuth client's redirect URIs.
4. `fly secrets set Authentication__Google__ClientId=... Authentication__Google__ClientSecret=... Authentication__AllowedEmails=you@example.com,other@example.com --app donkeyfire`
5. `fly deploy --ha=false`
6. Optional custom domain: `fly certs add <host> --app donkeyfire`, then a CNAME at your DNS provider pointing the host to `donkeyfire.fly.dev`.
