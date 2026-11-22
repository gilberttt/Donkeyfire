using System.Security.Claims;
using DonkeyFire.Web.Data;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.EntityFrameworkCore;

namespace DonkeyFire.Web.Services;

public static class AppClaims
{
    public const string UserId = "df_uid";
}

/// <summary>Resolves the signed-in user for the current circuit or request.</summary>
public class CurrentUser(AuthenticationStateProvider auth)
{
    public async Task<int?> GetIdAsync()
    {
        var state = await auth.GetAuthenticationStateAsync();
        var raw = state.User.FindFirstValue(AppClaims.UserId);
        return int.TryParse(raw, out var id) ? id : null;
    }

    public async Task<int> RequireIdAsync() =>
        await GetIdAsync() ?? throw new UnauthorizedAccessException("Not signed in.");

    public async Task<ClaimsPrincipal> GetPrincipalAsync() => (await auth.GetAuthenticationStateAsync()).User;
}

/// <summary>Per-business role checks. Every business page goes through this before loading data.</summary>
public class BusinessAccess(IDbContextFactory<AppDbContext> dbf, CurrentUser user)
{
    public async Task<BusinessRole?> GetRoleAsync(int businessId)
    {
        var uid = await user.GetIdAsync();
        if (uid is null) return null;
        await using var db = await dbf.CreateDbContextAsync();
        var member = await db.BusinessMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.BusinessId == businessId && m.UserId == uid);
        return member?.Role;
    }

    public async Task<BusinessRole> RequireAsync(int businessId, BusinessRole minimum = BusinessRole.Viewer)
    {
        var role = await GetRoleAsync(businessId);
        if (role is null || role < minimum)
            throw new UnauthorizedAccessException("You don't have access to this business.");
        return role.Value;
    }

    public static async Task<BusinessRole?> GetRoleAsync(AppDbContext db, int businessId, int userId) =>
        (await db.BusinessMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.BusinessId == businessId && m.UserId == userId))?.Role;
}

/// <summary>Creates or updates the local user record after Google sign-in and accepts pending invites.</summary>
public class UserProvisioner(IDbContextFactory<AppDbContext> dbf)
{
    public async Task<AppUser> UpsertAsync(string externalId, string email, string name, string? picture)
    {
        await using var db = await dbf.CreateDbContextAsync();
        var u = await db.Users.FirstOrDefaultAsync(x => x.ExternalId == externalId);
        if (u is null)
        {
            u = new AppUser { ExternalId = externalId };
            db.Users.Add(u);
        }
        u.Email = email;
        u.DisplayName = string.IsNullOrWhiteSpace(name) ? email : name;
        u.PictureUrl = picture;
        u.LastLoginAt = DateTime.UtcNow;
        await db.SaveChangesAsync();

        var normalized = email.Trim().ToLowerInvariant();
        var invites = await db.BusinessMembers.Where(m => m.UserId == null && m.Email == normalized).ToListAsync();
        foreach (var inv in invites)
        {
            inv.UserId = u.Id;
            inv.AcceptedAt = DateTime.UtcNow;
        }
        if (invites.Count > 0) await db.SaveChangesAsync();
        return u;
    }
}
