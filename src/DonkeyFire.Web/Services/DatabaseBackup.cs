using Microsoft.Data.Sqlite;

namespace DonkeyFire.Web.Services;

/// <summary>
/// Consistent point-in-time copies of the SQLite database (VACUUM INTO), taken before migrations run and once a day.
/// This guards against bad migrations and mistakes. It does not replace volume snapshots or an off-server copy.
/// </summary>
public static class DatabaseBackup
{
    public static string DataSource(string connectionString) => new SqliteConnectionStringBuilder(connectionString).DataSource;

    public static string DefaultDirectory(IConfiguration config, string connectionString)
    {
        var configured = config["Backup:Directory"];
        if (!string.IsNullOrWhiteSpace(configured)) return configured;
        return Path.Combine(Path.GetDirectoryName(Path.GetFullPath(DataSource(connectionString)))!, "backups");
    }

    public static async Task<string> SnapshotAsync(string connectionString, string directory, string label, CancellationToken ct = default)
    {
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, $"{label}-{DateTime.UtcNow:yyyyMMdd-HHmmss}.db");
        await using var conn = new SqliteConnection(connectionString);
        await conn.OpenAsync(ct);
        await using var cmd = conn.CreateCommand();
        cmd.CommandText = "VACUUM INTO $path";
        cmd.Parameters.AddWithValue("$path", target);
        await cmd.ExecuteNonQueryAsync(ct);
        return target;
    }

    public static void Prune(string directory, string label, int keep)
    {
        if (!Directory.Exists(directory)) return;
        foreach (var old in new DirectoryInfo(directory).GetFiles($"{label}-*.db").OrderByDescending(f => f.Name).Skip(Math.Max(1, keep)))
            old.Delete();
    }

    public static DateTime? LatestUtc(string directory, string label) =>
        !Directory.Exists(directory) ? null
            : new DirectoryInfo(directory).GetFiles($"{label}-*.db").Select(f => (DateTime?)f.LastWriteTimeUtc).Max();
}

/// <summary>Takes a daily snapshot while the app is running. Turn off with Backup:Enabled=false.</summary>
public class DatabaseBackupService(IConfiguration config, ILogger<DatabaseBackupService> log) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!config.GetValue("Backup:Enabled", true)) return;
        var connection = config.GetConnectionString("Default");
        if (string.IsNullOrWhiteSpace(connection)) return;
        var dir = DatabaseBackup.DefaultDirectory(config, connection);
        var interval = TimeSpan.FromHours(Math.Max(1, config.GetValue("Backup:IntervalHours", 24)));
        var keep = config.GetValue("Backup:Keep", 14);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var latest = DatabaseBackup.LatestUtc(dir, "daily");
                if (latest is null || DateTime.UtcNow - latest >= interval)
                {
                    var path = await DatabaseBackup.SnapshotAsync(connection, dir, "daily", stoppingToken);
                    DatabaseBackup.Prune(dir, "daily", keep);
                    log.LogInformation("Database snapshot written to {Path}", path);
                }
                await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken);
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                log.LogError(ex, "Database snapshot failed; will retry");
                try { await Task.Delay(TimeSpan.FromMinutes(30), stoppingToken); } catch (OperationCanceledException) { }
            }
        }
    }
}
