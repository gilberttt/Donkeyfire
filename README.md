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
| **Taxes & EPR** | Live for the current month and any past one. **Sales tax** is split into Alabama state (4%), Barbour County (1.5%) and City of Eufaula (4%), with the return due date (the 20th of the next month). Events outside the city limits skip city tax. **Eufaula Parks and Recreation** gets 25% of park-venue sales minus sales tax, card fees, cost of goods and employee costs, worked out month by month and never below zero. Record payments for either and see what's still owed for each of the last 12 months. A warning shows sales that aren't tied to any event, so nothing at a park slips through. |
| **Square** | Pulls completed Square orders into Sales: each item (matched to the menu by name, so it uses up inventory), cash or card, tips, and Square's actual processing fee. Refunds come in as negative sales and put ingredients back. Items Square calls something else show up in a "not on your menu" panel with a one-click link. Sync from the Sales page, or on a timer. |
| **Settings** | Sales tax rate (and whether prices include it), card fee %, payroll burden %, food cost target. Members and roles (Viewer, Editor, Owner) by Google email invite. Delete the business. |

### How the money is counted
- **Net sales** = gross sales less sales tax when menu prices include tax. When tax is added on top, it's shown as owed but doesn't reduce net sales.
- **Net profit** = net sales − estimated card fees − event commissions − labor (wages + payroll burden) − expenses. This is cash-basis: food is counted when you log the purchase as an expense.
- **Food cost** = recipe cost of what was sold. It's a ratio to watch, not subtracted again.
- **Tips** come from the tip jar, so they're tracked per shift but aren't a business cost.
- Card fees: Square sales use Square's actual fee. Other card sales use the estimated rate from Settings (set it to 0 if you log processor fees as expenses).
- Tips customers add on Square are shown, but not counted as sales.
- **Revenue share (EPR)** = 25% × (gross sales at park venues − sales tax − card fees − cost of goods − employee costs) for each month, never below zero. Only events marked "At a Parks & Rec venue" count; make each season (baseball, softball, soccer, flag football, and pee wee football from fall 2027) one event spanning its dates. Costs tied to a park event count in full, costs tied to other events don't, and costs with no event (a Sam's Club run, a shift not tied to an event) are shared out by the parks' portion of that month's pre-tax sales. Cost of goods is Food & drink and Paper & supplies purchases (or recipe cost, if chosen in Settings). Employee costs are wages plus payroll burden; tips aren't included. The share is subtracted in profit.
- Saving an event attaches any sales in its dates that had no event (for example Square sales synced before the season was added).
- Sales tax and EPR payments are logged as expenses in their own categories, but they settle amounts already accounted for, so they're not counted as costs again.

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

### Connect Square
1. In the [Square Developer Dashboard](https://developer.squareup.com/apps), create an application (any name), open **Credentials**, and copy the **Production access token**. It needs permission to read orders and locations (personal access tokens have this).
2. Store it as a secret, never in the code: `fly secrets set Square__AccessToken=<token> --app donkeyfire` (locally: `dotnet user-secrets set "Square:AccessToken" "<token>"`). For a sandbox token also set `Square__Environment=sandbox`.
3. In the app, open **Settings → Square**, press **Load locations**, pick your location and the first day to import, and save.
4. Press **Sync Square** on the Sales page. To sync on a timer as well, set `Square__AutoSyncMinutes=15`.

Each order line is imported once (keyed by Square order and line), so syncing again never double-counts. Each sync starts a day before the last one so late-arriving fees get filled in.

### Deploy to Fly.io
The repo includes a `Dockerfile` and `fly.toml` (app `donkeyfire`, volume `donkeyfire_data` mounted at `/data`). Run exactly one machine.

1. `fly apps create donkeyfire` (change `app` in `fly.toml` if the name is taken)
2. `fly volumes create donkeyfire_data --size 1 --region iad --app donkeyfire`
3. Add `https://donkeyfire.fly.dev/signin-google` (and your custom domain) to the Google OAuth client's redirect URIs.
4. `fly secrets set Authentication__Google__ClientId=... Authentication__Google__ClientSecret=... Authentication__AllowedEmails=you@example.com,other@example.com --app donkeyfire`
5. `fly deploy --ha=false`
6. Optional custom domain: `fly certs add <host> --app donkeyfire`, then a CNAME at your DNS provider pointing the host to `donkeyfire.fly.dev`.
