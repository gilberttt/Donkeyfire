using DonkeyFire.Web.Data;
using Microsoft.EntityFrameworkCore;

namespace DonkeyFire.Web.Services;

/// <summary>
/// Decides who may sign in with Google. A Google account is allowed when its email is verified and either appears in
/// <c>Authentication:AllowedEmails</c> (comma or semicolon separated, or a JSON array) or has been invited to a business
/// from Business Settings. Without this, anyone with a Google account could create an account on a public server.
/// </summary>
public class SignInPolicy(IDbContextFactory<AppDbContext> dbf, IConfiguration config)
{
    public static IReadOnlyList<string> ParseAllowedEmails(IConfiguration config)
    {
        var section = config.GetSection("Authentication:AllowedEmails");
        var raw = new List<string>();
        if (!string.IsNullOrWhiteSpace(section.Value)) raw.Add(section.Value);
        raw.AddRange(section.GetChildren().Select(c => c.Value ?? ""));
        return raw
            .SelectMany(v => v.Split([',', ';', ' ', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(e => e.ToLowerInvariant())
            .Distinct()
            .ToList();
    }

    /// <summary>True when sign-in must be limited to the allowlist and invited members.</summary>
    public static bool IsRestricted(IConfiguration config, IHostEnvironment env) =>
        !config.GetValue("Authentication:AllowAnyGoogleAccount", false)
        && (ParseAllowedEmails(config).Count > 0 || !env.IsDevelopment());

    public async Task<bool> IsAllowedAsync(string? email, bool emailVerified)
    {
        if (string.IsNullOrWhiteSpace(email) || !emailVerified) return false;
        var normalized = email.Trim().ToLowerInvariant();
        if (ParseAllowedEmails(config).Contains(normalized)) return true;
        await using var db = await dbf.CreateDbContextAsync();
        return await db.BusinessMembers.AsNoTracking().AnyAsync(m => m.Email == normalized);
    }
}
