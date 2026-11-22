# Donkey Fire Concessions: context for Claude

Personal web app for running Donkey Fire Concessions, a concession stand business: sales, expenses, events, menu and recipes, inventory, employees and shifts. Blazor Server on .NET 10, EF Core, SQLite. Built on the BuildTrack architecture (gilberttt/buildtrack). See README.md for features and settings.

## Run, test, migrate
- Run: `cd src/DonkeyFire.Web && dotnet run`, then open http://localhost:5188. In Development with no Google credentials, the login page shows a "Developer sign-in" button.
- Tests: `dotnet test tests/DonkeyFire.Tests`
- Migrations: `dotnet tool restore`, then `dotnet ef migrations add <Name> -o Data/Migrations` from `src/DonkeyFire.Web`. Migrations apply automatically at startup.
- **Back up `App_Data/donkeyfire.db` (plus `-wal`, `-shm`) before any migration work. Never run `dotnet ef migrations remove -f`**: it reverts the last applied migration in the live database and drops the tables (this wiped BuildTrack's database once). To undo an unapplied migration, delete its two files and rebuild.
- The app snapshots the database before pending migrations and daily (`Services/DatabaseBackup.cs`).

## Layout
- `Components/Pages/BusinessPages/`: one page per area (Dashboard, Sales, Expenses, Events, EventDetail, TaxesAndShare (`/b/{id}/taxes`), Menu, Inventory, Employees, Settings). `Components/Shared/`: Modal, ConfirmDialog, Kpi, BusinessShell, EventSelect, EventForm, PnLTable, FileUploadButton.
- `Data/Entities.cs` and `Data/AppDbContext.cs`: the model. Every business-owned table cascades on business delete and is filtered by `BusinessId` in queries.
- `Services/`: `BusinessMetrics` (all P&L math; `Compute` is pure and tested), `SalesService` (sales use up recipe ingredients and give them back on delete), `InventoryService` (purchase/use/waste/count adjustments), `SquareClient` + `SquareSync` (Square Orders API import; `ImportAsync` is tested with sample JSON) + `SquareAutoSyncService`, `MenuMatcher` (Square names → menu items), `BusinessSeeder` (new business + optional starter menu), `SignInPolicy`, `DatabaseBackup`, `FileStorage`.
- Pages follow one pattern: `BusinessShell` gives a `BusinessContext` (role, `CanEdit`); lists, modal forms, `ExecuteUpdateAsync` for quick toggles, `ConfirmDialog` before deletes.

## Money rules
- Money is `decimal`, dates are `DateOnly`. SQLite stores decimals as text, so sums and decimal comparisons happen in memory, not in SQL.
- Profit is cash-basis: net sales − card fees (Square's actual fee per sale, otherwise the estimated rate) − event commissions − labor (wages + burden) − expenses. Recipe food cost is a ratio, never subtracted (purchases are already expenses). Tips are not a business cost.
- Sales tax comes from `TaxRate` rows (Alabama 4% + Barbour County 1.5% + City of Eufaula 4%), computed per sale via `TaxPlan`; events with `OutsideCityLimits` skip City-level rates. `Business.SalesTaxPercent` is kept equal to the sum.
- Revenue share (Eufaula Parks and Recreation, 25%): `Share = pct × max(0, gross − sales tax − COGS − labor)` per calendar month (`BusinessMetrics.ShareOf`, `MonthlyShare`, `MonthsAsync`). COGS basis is a setting (purchases in Food & drink + Paper & supplies, or recipe cost). It is subtracted in net profit. `SalesTaxPayment` and `RevenueSharePayment` expenses settle these and are excluded from expenses; `Expense.ForMonth` says which month a payment covers.
- Inventory on-hand only changes through `InventoryAdjustment` rows (sales write `Sale` rows linked by `SaleId`), so history stays complete.

## Status and next steps
- Created 2026-11-22. Deploying to Fly.io at `https://donkeyfire.level42homebuyers.com` (Fly app `donkeyfire`, volume `donkeyfire_data`, fly.dev host `donkeyfire.fly.dev`), same approach as BuildTrack: personal Fly account, deploy from the personal computer, single machine, Google sign-in with an email allowlist, Squarespace CNAME for the subdomain.
- **Deployment progress (update as steps finish):**
  - [ ] `fly apps create donkeyfire`, then `fly volumes create donkeyfire_data --size 1 --region iad --app donkeyfire`
  - [ ] Google OAuth redirect URIs `https://donkeyfire.fly.dev/signin-google` and `https://donkeyfire.level42homebuyers.com/signin-google`
  - [ ] `fly secrets set` Google client id/secret, `Authentication__AllowedEmails`, optionally `Square__AccessToken` (never commit these or paste them in chat)
  - [ ] `fly deploy --ha=false`, sign in at donkeyfire.fly.dev
  - [ ] `fly certs add donkeyfire.level42homebuyers.com`, Squarespace CNAME `donkeyfire` → `donkeyfire.fly.dev`, certificate issued
  - [ ] Connect Square in Settings and run the first sync
- Square: card sales come from the Square Orders API (token in `Square__AccessToken`, never in the DB); cash is rung up in the app. Square sales have `Source = Square` and a unique `ExternalId`. Square's API is unreachable from Claude's cloud sandbox, so test against sample JSON there.
- Ideas, not started: reports by month for taxes (sales tax owed per period); par levels per event type; event checklists (load-out list); expense → inventory purchase in one step.

## Conventions
- Match the surrounding code's style, comment density and naming.
- Do not commit secrets. Google credentials and the allowlist come from user-secrets or environment variables.
- This is a personal project. Do not add employer-specific integrations.
