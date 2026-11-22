using Microsoft.EntityFrameworkCore;

namespace DonkeyFire.Web.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<Business> Businesses => Set<Business>();
    public DbSet<BusinessMember> BusinessMembers => Set<BusinessMember>();
    public DbSet<Event> Events => Set<Event>();
    public DbSet<MenuItem> MenuItems => Set<MenuItem>();
    public DbSet<RecipeLine> RecipeLines => Set<RecipeLine>();
    public DbSet<InventoryItem> InventoryItems => Set<InventoryItem>();
    public DbSet<InventoryAdjustment> InventoryAdjustments => Set<InventoryAdjustment>();
    public DbSet<Sale> Sales => Set<Sale>();
    public DbSet<Expense> Expenses => Set<Expense>();
    public DbSet<Employee> Employees => Set<Employee>();
    public DbSet<Shift> Shifts => Set<Shift>();
    public DbSet<StoredFile> Files => Set<StoredFile>();

    protected override void ConfigureConventions(ModelConfigurationBuilder c)
    {
        // SQLite stores decimals as text ("5000.0"); normalizing on read drops the trailing zeros in forms.
        // Text also means money can't be summed in SQL, so totals are added up in memory (see BusinessMetrics).
        c.Properties<decimal>().HaveConversion<NormalizedDecimalConverter>();
    }

    private sealed class NormalizedDecimalConverter() : Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter<decimal, decimal>(
        v => v, v => v / 1.000000000000000000000000000000000m);

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>().HasIndex(u => u.ExternalId).IsUnique();
        b.Entity<AppUser>().HasIndex(u => u.Email);

        b.Entity<BusinessMember>().HasIndex(m => new { m.BusinessId, m.Email }).IsUnique();
        b.Entity<BusinessMember>().HasIndex(m => m.Email);
        b.Entity<BusinessMember>().HasOne(m => m.Business).WithMany(p => p.Members).OnDelete(DeleteBehavior.Cascade);
        b.Entity<BusinessMember>().HasOne(m => m.User).WithMany().OnDelete(DeleteBehavior.Cascade);

        b.Entity<RecipeLine>().HasOne(r => r.MenuItem).WithMany(m => m.Recipe).OnDelete(DeleteBehavior.Cascade);
        b.Entity<RecipeLine>().HasOne(r => r.InventoryItem).WithMany().OnDelete(DeleteBehavior.Cascade);
        b.Entity<InventoryAdjustment>().HasOne(a => a.InventoryItem).WithMany().OnDelete(DeleteBehavior.Cascade);
        b.Entity<InventoryAdjustment>().HasIndex(a => a.SaleId);
        b.Entity<Sale>().HasOne(s => s.Event).WithMany().OnDelete(DeleteBehavior.SetNull);
        b.Entity<Sale>().HasOne(s => s.MenuItem).WithMany().OnDelete(DeleteBehavior.SetNull);
        b.Entity<Sale>().HasIndex(s => new { s.BusinessId, s.Date });
        b.Entity<Expense>().HasOne(e => e.Event).WithMany().OnDelete(DeleteBehavior.SetNull);
        b.Entity<Expense>().HasIndex(e => new { e.BusinessId, e.Date });
        b.Entity<Shift>().HasOne(s => s.Employee).WithMany().OnDelete(DeleteBehavior.Cascade);
        b.Entity<Shift>().HasOne(s => s.Event).WithMany().OnDelete(DeleteBehavior.SetNull);
        b.Entity<Shift>().HasIndex(s => new { s.BusinessId, s.Date });

        // Every business-owned table cascades when the business is deleted.
        foreach (var type in new[] { typeof(Event), typeof(MenuItem), typeof(InventoryItem), typeof(InventoryAdjustment), typeof(Sale),
                     typeof(Expense), typeof(Employee), typeof(Shift), typeof(StoredFile) })
        {
            b.Entity(type).HasOne(typeof(Business)).WithMany().HasForeignKey("BusinessId").OnDelete(DeleteBehavior.Cascade);
        }
    }
}
