namespace DonkeyFire.Web.Data;

public enum BusinessRole { Viewer = 0, Editor = 1, Owner = 2 }
public enum EventKind { Festival = 0, Sports = 1, Fair = 2, Market = 3, Catering = 4, Storefront = 5, Other = 6 }
public enum EventStatus { Planned = 0, Done = 1, Cancelled = 2 }
public enum PaymentMethod { Cash = 0, Card = 1, Other = 2 }
public enum SaleSource { App = 0, Square = 1 }
public enum ExpenseCategory
{
    FoodAndDrink = 0, PaperAndSupplies = 1, PropaneAndFuel = 2, EventFees = 3, PermitsAndLicenses = 4, Equipment = 5,
    Repairs = 6, Insurance = 7, Marketing = 8, PaymentFees = 9, Vehicle = 10, Other = 11,
    /// <summary>Paying the revenue share owed to the venue partner. Settles a cost already counted, so it's not an expense.</summary>
    RevenueSharePayment = 12,
    /// <summary>Remitting sales tax. The tax was never income, so it's not an expense.</summary>
    SalesTaxPayment = 13,
}
public enum TaxLevel { State = 0, County = 1, City = 2, Other = 3 }
/// <summary>What counts as cost of goods in the revenue-share calculation.</summary>
public enum CogsBasis { Purchases = 0, RecipeCost = 1 }
public enum InventoryCategory { Food = 0, Drink = 1, PaperAndPackaging = 2, Supplies = 3, Other = 4 }
/// <summary>Why an inventory count changed. Sale rows are written automatically when sales are recorded from menu items.</summary>
public enum AdjustmentReason { Purchase = 0, Sale = 1, Used = 2, Waste = 3, Count = 4 }

public class AppUser
{
    public int Id { get; set; }
    public string ExternalId { get; set; } = "";
    public string Email { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public string? PictureUrl { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime LastLoginAt { get; set; } = DateTime.UtcNow;
}

public class Business
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string? City { get; set; }
    public string? State { get; set; }
    /// <summary>Sales tax rate in percent (e.g. 9 for 9%). Used to split tax out of gross sales.</summary>
    public decimal SalesTaxPercent { get; set; }
    /// <summary>True when menu prices already include sales tax (common at concession stands).</summary>
    public bool PricesIncludeTax { get; set; } = true;
    /// <summary>Card processor fee in percent of card sales (e.g. 2.6 for Square).</summary>
    public decimal CardFeePercent { get; set; } = 2.6m;
    /// <summary>Employer payroll taxes and other labor burden, in percent of wages.</summary>
    public decimal LaborBurdenPercent { get; set; }
    /// <summary>Who gets a share of the month's sales (e.g. Eufaula Parks and Recreation). Null when there's no agreement.</summary>
    public string? RevenueSharePartner { get; set; }
    /// <summary>Percent of (gross sales − sales tax − cost of goods − labor) owed each month.</summary>
    public decimal RevenueSharePercent { get; set; }
    public CogsBasis RevenueShareCogsBasis { get; set; }
    /// <summary>Food cost target in percent of sales. Menu items above it are flagged.</summary>
    public decimal TargetFoodCostPercent { get; set; } = 30;
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Square location whose orders are imported. Null when Square isn't connected.</summary>
    public string? SquareLocationId { get; set; }
    public string? SquareLocationName { get; set; }
    /// <summary>IANA time zone of the Square location, used to put each order on the right day.</summary>
    public string? SquareTimeZone { get; set; }
    /// <summary>First day to import on the first sync.</summary>
    public DateOnly? SquareSyncFrom { get; set; }
    /// <summary>When orders were last pulled. The next sync starts a day before this, so late fees and edits are picked up.</summary>
    public DateTime? SquareLastSyncAt { get; set; }

    public List<BusinessMember> Members { get; set; } = new();
}

public class BusinessMember
{
    public int Id { get; set; }
    public int BusinessId { get; set; }
    public Business? Business { get; set; }
    public int? UserId { get; set; }
    public AppUser? User { get; set; }
    /// <summary>Invite email. Matched to the Google account email on sign-in.</summary>
    public string Email { get; set; } = "";
    public BusinessRole Role { get; set; }
    public DateTime InvitedAt { get; set; } = DateTime.UtcNow;
    public DateTime? AcceptedAt { get; set; }
}

/// <summary>A day or run of days the stand is open somewhere: a festival, a ball game, a catering job.</summary>
public class Event
{
    public int Id { get; set; }
    public int BusinessId { get; set; }
    public string Name { get; set; } = "";
    public EventKind Kind { get; set; }
    public EventStatus Status { get; set; }
    public DateOnly StartDate { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public DateOnly? EndDate { get; set; }
    public string? Location { get; set; }
    public string? Contact { get; set; }
    /// <summary>Flat booth or space fee. Recorded as an expense when it is paid; this is the agreed amount.</summary>
    public decimal BoothFee { get; set; }
    /// <summary>Percent of sales the organizer takes, if any.</summary>
    public decimal CommissionPercent { get; set; }
    public decimal? SalesGoal { get; set; }
    /// <summary>Held outside the city limits, so city sales tax doesn't apply.</summary>
    public bool OutsideCityLimits { get; set; }
    /// <summary>Not covered by the revenue-share agreement (e.g. a private catering job elsewhere).</summary>
    public bool ExcludeFromRevenueShare { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public DateOnly LastDate => EndDate is { } e && e > StartDate ? e : StartDate;
}

public class MenuItem
{
    public int Id { get; set; }
    public int BusinessId { get; set; }
    public string Name { get; set; } = "";
    public string? Category { get; set; }
    public decimal Price { get; set; }
    /// <summary>Used when the item has no recipe (for example a canned drink bought by the case and costed by hand).</summary>
    public decimal? ManualCost { get; set; }
    public bool IsActive { get; set; } = true;
    public int SortOrder { get; set; }
    public string? Notes { get; set; }
    /// <summary>Other names this item has on Square, one per line (e.g. "Hot Dog (Regular)"). The item's own name always matches.</summary>
    public string? SquareNames { get; set; }

    public List<RecipeLine> Recipe { get; set; } = new();
}

/// <summary>How much of one inventory item goes into one serving of a menu item, in the inventory item's unit.</summary>
public class RecipeLine
{
    public int Id { get; set; }
    public int MenuItemId { get; set; }
    public MenuItem? MenuItem { get; set; }
    public int InventoryItemId { get; set; }
    public InventoryItem? InventoryItem { get; set; }
    public decimal Quantity { get; set; }
}

public class InventoryItem
{
    public int Id { get; set; }
    public int BusinessId { get; set; }
    public string Name { get; set; } = "";
    public InventoryCategory Category { get; set; }
    /// <summary>Counting unit, e.g. "each", "lb", "oz", "bun", "cup".</summary>
    public string Unit { get; set; } = "each";
    public decimal OnHand { get; set; }
    /// <summary>Cost of one unit, updated from the latest purchase.</summary>
    public decimal UnitCost { get; set; }
    public decimal? ReorderLevel { get; set; }
    public string? Supplier { get; set; }
    public string? Location { get; set; }
    public string? Notes { get; set; }

    public bool IsLow => ReorderLevel is { } r && OnHand <= r;
}

public class InventoryAdjustment
{
    public int Id { get; set; }
    public int BusinessId { get; set; }
    public int InventoryItemId { get; set; }
    public InventoryItem? InventoryItem { get; set; }
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public AdjustmentReason Reason { get; set; }
    /// <summary>Signed change in the item's unit. Positive adds stock.</summary>
    public decimal Change { get; set; }
    /// <summary>Unit cost for purchases; the cost used for the item afterwards.</summary>
    public decimal? UnitCost { get; set; }
    /// <summary>Set for automatic Sale rows so deleting the sale puts the stock back.</summary>
    public int? SaleId { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One line of sales: a menu item and quantity, or a lump sum (for example a day's POS total).</summary>
public class Sale
{
    public int Id { get; set; }
    public int BusinessId { get; set; }
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public int? EventId { get; set; }
    public Event? Event { get; set; }
    public int? MenuItemId { get; set; }
    public MenuItem? MenuItem { get; set; }
    /// <summary>Menu item name at the time of sale, or a description for lump sums.</summary>
    public string Description { get; set; } = "";
    public decimal Quantity { get; set; } = 1;
    public decimal UnitPrice { get; set; }
    /// <summary>Recipe cost of one unit at the time of sale. Zero for lump sums.</summary>
    public decimal UnitCost { get; set; }
    public PaymentMethod Payment { get; set; }
    public string? Note { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public SaleSource Source { get; set; }
    /// <summary>Square order and line ("orderId:lineUid"), so a re-sync never imports the same line twice.</summary>
    public string? ExternalId { get; set; }
    /// <summary>Actual card processing fee for this line (Square's fee spread across the order's lines). Null means estimate it.</summary>
    public decimal? Fee { get; set; }
    /// <summary>Tip collected with this line on Square. Passed to the crew, so not counted as sales.</summary>
    public decimal Tip { get; set; }

    public decimal Total => Quantity * UnitPrice;
}

public class Expense
{
    public int Id { get; set; }
    public int BusinessId { get; set; }
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public ExpenseCategory Category { get; set; }
    public string? Vendor { get; set; }
    public string Description { get; set; } = "";
    public decimal Amount { get; set; }
    public int? EventId { get; set; }
    public Event? Event { get; set; }
    public PaymentMethod Payment { get; set; } = PaymentMethod.Card;
    public int? ReceiptFileId { get; set; }
    /// <summary>For tax and revenue-share payments: the month (first day) the payment covers.</summary>
    public DateOnly? ForMonth { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}

/// <summary>One sales tax the business collects and files: Alabama state, Barbour County, City of Eufaula.</summary>
public class TaxRate
{
    public int Id { get; set; }
    public int BusinessId { get; set; }
    public string Name { get; set; } = "";
    public TaxLevel Level { get; set; }
    public decimal Percent { get; set; }
    /// <summary>Where the return is filed, as a reminder (e.g. "My Alabama Taxes").</summary>
    public string? FiledWith { get; set; }
    public int SortOrder { get; set; }
}

public class Employee
{
    public int Id { get; set; }
    public int BusinessId { get; set; }
    public string Name { get; set; } = "";
    public string? Position { get; set; }
    public string? Phone { get; set; }
    public string? Email { get; set; }
    public decimal HourlyRate { get; set; }
    public bool IsActive { get; set; } = true;
    public string? Notes { get; set; }
}

public class Shift
{
    public int Id { get; set; }
    public int BusinessId { get; set; }
    public int EmployeeId { get; set; }
    public Employee? Employee { get; set; }
    public DateOnly Date { get; set; } = DateOnly.FromDateTime(DateTime.Today);
    public int? EventId { get; set; }
    public Event? Event { get; set; }
    public decimal Hours { get; set; }
    /// <summary>Rate copied from the employee when the shift is logged, so later raises don't rewrite history.</summary>
    public decimal HourlyRate { get; set; }
    /// <summary>Flat pay on top of hours (a day rate, a bonus).</summary>
    public decimal FlatPay { get; set; }
    /// <summary>Tips paid out to the employee. Shown separately; not counted as a business cost.</summary>
    public decimal Tips { get; set; }
    public bool IsPaid { get; set; }
    public string? Notes { get; set; }

    public decimal Wages => Hours * HourlyRate + FlatPay;
}

public class StoredFile
{
    public int Id { get; set; }
    public int BusinessId { get; set; }
    public string FileName { get; set; } = "";
    public string ContentType { get; set; } = "application/octet-stream";
    public long Size { get; set; }
    public string StoragePath { get; set; } = "";
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
    public int? UploadedById { get; set; }
}
