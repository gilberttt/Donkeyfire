using System.Globalization;
using DonkeyFire.Web.Data;

namespace DonkeyFire.Web.Services;

public record Toast(Guid Id, string Message, string Kind);

/// <summary>Small per-circuit notification queue shown by the layout.</summary>
public class ToastService
{
    public event Action? Changed;
    public List<Toast> Items { get; } = new();

    public void Show(string message, string kind = "success")
    {
        var toast = new Toast(Guid.NewGuid(), message, kind);
        Items.Add(toast);
        Changed?.Invoke();
        _ = Task.Delay(4000).ContinueWith(_ => Dismiss(toast.Id));
    }

    public void Error(string message) => Show(message, "danger");

    public void Dismiss(Guid id)
    {
        Items.RemoveAll(t => t.Id == id);
        Changed?.Invoke();
    }
}

public static class Fmt
{
    private static readonly CultureInfo Us = CultureInfo.GetCultureInfo("en-US");

    public static string Money(decimal v) => v.ToString("C0", Us);
    public static string Money(decimal? v) => v is null ? "—" : Money(v.Value);
    public static string Money2(decimal v) => v.ToString("C2", Us);
    public static string Signed(decimal v) => (v > 0 ? "+" : v < 0 ? "−" : "") + Math.Abs(v).ToString("C0", Us);
    public static string Pct(double v) => $"{Math.Round(v):0}%";
    public static string Date(DateOnly? d) => d?.ToString("MMM d, yyyy", Us) ?? "—";
    public static string Short(DateOnly? d) => d?.ToString("MMM d", Us) ?? "—";
    public static string Range(DateOnly? a, DateOnly? b) => (a, b) switch
    {
        (null, null) => "No dates",
        (null, _) => $"Ends {Date(b)}",
        (_, null) => $"Starts {Date(a)}",
        _ => $"{Short(a)} – {Date(b)}",
    };
    /// <summary>One date for a single-day event, a range for a multi-day one.</summary>
    public static string Days(DateOnly start, DateOnly? end) => end is { } e && e > start ? Range(start, e) : Date(start);
    public static string Plural(int n, string one, string? many = null) => $"{n} {(n == 1 ? one : many ?? one + "s")}";
    public static string Size(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.0} MB",
    };

    /// <summary>Green when money is left over, red when it is short.</summary>
    public static string ProfitClass(decimal v) => v > 0 ? "num-under" : v < 0 ? "num-over" : "num-ok";
    public static string Qty(decimal v) => v == Math.Round(v) ? v.ToString("#,0", Us) : v.ToString("#,0.##", Us);
    public static string Hours(decimal v) => v.ToString("0.##", Us) + " h";

    public static string Label(Enum e) => e switch
    {
        ExpenseCategory.FoodAndDrink => "Food & drink",
        ExpenseCategory.PaperAndSupplies => "Paper & supplies",
        ExpenseCategory.PropaneAndFuel => "Propane & fuel",
        ExpenseCategory.EventFees => "Event & booth fees",
        ExpenseCategory.PermitsAndLicenses => "Permits & licenses",
        ExpenseCategory.PaymentFees => "Card & payment fees",
        ExpenseCategory.RevenueSharePayment => "Revenue share payment",
        ExpenseCategory.SalesTaxPayment => "Sales tax payment",
        CogsBasis.Purchases => "Food, drink & paper purchases that month",
        CogsBasis.RecipeCost => "Recipe cost of items sold",
        InventoryCategory.PaperAndPackaging => "Paper & packaging",
        _ => System.Text.RegularExpressions.Regex.Replace(e.ToString(), "(?<=[a-z])([A-Z])", " $1"),
    };
}

public static class Csv
{
    /// <summary>Builds CSV text with a header row; values containing commas, quotes or line breaks are quoted.</summary>
    public static string Build(IEnumerable<string> header, IEnumerable<IEnumerable<object?>> rows)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine(string.Join(",", header.Select(Cell)));
        foreach (var r in rows) sb.AppendLine(string.Join(",", r.Select(Cell)));
        return sb.ToString();
    }

    private static string Cell(object? v)
    {
        var s = v switch
        {
            null => "",
            decimal d => d.ToString(CultureInfo.InvariantCulture),
            DateOnly d => d.ToString("yyyy-MM-dd"),
            Enum e => Fmt.Label(e),
            _ => v.ToString() ?? "",
        };
        return s.IndexOfAny([',', '"', '\n', '\r']) >= 0 ? $"\"{s.Replace("\"", "\"\"")}\"" : s;
    }
}
