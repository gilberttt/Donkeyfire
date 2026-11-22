using DonkeyFire.Web.Data;
using DonkeyFire.Web.Services;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace DonkeyFire.Tests;

public class SignInPolicyTests
{
    private static IConfiguration Config(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>(v.Key, v.Value))).Build();

    private static IHostEnvironment Env(string name) => new FakeEnv(name);

    private sealed class FakeEnv(string name) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = name;
        public string ApplicationName { get; set; } = "Test";
        public string ContentRootPath { get; set; } = "";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = null!;
    }

    [Fact]
    public void Allowed_emails_accept_commas_semicolons_spaces_and_ignore_case()
    {
        var list = SignInPolicy.ParseAllowedEmails(Config(("Authentication:AllowedEmails", "Me@Example.com; trista@example.com, me@example.com")));
        Assert.Equal(["me@example.com", "trista@example.com"], list);
    }

    [Fact]
    public void Allowed_emails_accept_a_json_style_array()
    {
        var list = SignInPolicy.ParseAllowedEmails(Config(("Authentication:AllowedEmails:0", "a@x.com"), ("Authentication:AllowedEmails:1", "b@x.com")));
        Assert.Equal(["a@x.com", "b@x.com"], list);
    }

    [Fact]
    public void Production_is_restricted_by_default_but_development_without_a_list_is_not()
    {
        Assert.True(SignInPolicy.IsRestricted(Config(), Env("Production")));
        Assert.False(SignInPolicy.IsRestricted(Config(), Env("Development")));
        Assert.True(SignInPolicy.IsRestricted(Config(("Authentication:AllowedEmails", "a@x.com")), Env("Development")));
    }

    [Fact]
    public void Allow_any_google_account_switch_turns_the_restriction_off()
    {
        Assert.False(SignInPolicy.IsRestricted(Config(("Authentication:AllowAnyGoogleAccount", "true")), Env("Production")));
    }

    [Fact]
    public async Task Allowlisted_and_invited_emails_pass_but_strangers_and_unverified_emails_do_not()
    {
        using var factory = new TestDb();
        using (var db = factory.CreateDbContext())
        {
            var business = new Business { Name = "B" };
            db.Businesses.Add(business);
            db.SaveChanges();
            db.BusinessMembers.Add(new BusinessMember { BusinessId = business.Id, Email = "invited@example.com", Role = BusinessRole.Editor });
            db.SaveChanges();
        }
        var policy = new SignInPolicy(factory, Config(("Authentication:AllowedEmails", "owner@example.com")));

        Assert.True(await policy.IsAllowedAsync("Owner@Example.com", true));
        Assert.True(await policy.IsAllowedAsync("invited@example.com", true));
        Assert.False(await policy.IsAllowedAsync("stranger@example.com", true));
        Assert.False(await policy.IsAllowedAsync("owner@example.com", false));
        Assert.False(await policy.IsAllowedAsync("", true));
        Assert.False(await policy.IsAllowedAsync(null, true));
    }
}

public class DatabaseBackupTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "df-backup-" + Guid.NewGuid().ToString("N"));

    public DatabaseBackupTests() => Directory.CreateDirectory(dir);

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(dir)) Directory.Delete(dir, true);
    }

    [Fact]
    public async Task Snapshot_is_a_complete_readable_copy_of_the_live_database()
    {
        var live = $"Data Source={Path.Combine(dir, "live.db")}";
        await using (var conn = new SqliteConnection(live))
        {
            await conn.OpenAsync();
            await using var cmd = conn.CreateCommand();
            cmd.CommandText = "PRAGMA journal_mode=WAL; CREATE TABLE T(Id INTEGER, Name TEXT); INSERT INTO T VALUES (1,'kept');";
            await cmd.ExecuteNonQueryAsync();
        }

        var path = await DatabaseBackup.SnapshotAsync(live, Path.Combine(dir, "backups"), "daily");

        await using var copy = new SqliteConnection($"Data Source={path};Mode=ReadOnly");
        await copy.OpenAsync();
        await using var read = copy.CreateCommand();
        read.CommandText = "SELECT Name FROM T WHERE Id = 1";
        Assert.Equal("kept", await read.ExecuteScalarAsync());
    }

    [Fact]
    public void Prune_keeps_only_the_newest_snapshots_for_a_label()
    {
        foreach (var stamp in new[] { "20260101-000000", "20260102-000000", "20260103-000000", "20260104-000000" })
            File.WriteAllText(Path.Combine(dir, $"daily-{stamp}.db"), "x");
        File.WriteAllText(Path.Combine(dir, "pre-migrate-20260101-000000.db"), "x");

        DatabaseBackup.Prune(dir, "daily", keep: 2);

        var left = Directory.GetFiles(dir).Select(p => Path.GetFileName(p)!).OrderBy(n => n).ToArray();
        Assert.Equal(["daily-20260103-000000.db", "daily-20260104-000000.db", "pre-migrate-20260101-000000.db"], left);
    }
}
