using System.Security.Claims;
using DonkeyFire.Web.Components;
using DonkeyFire.Web.Data;
using DonkeyFire.Web.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(o => o.MaximumReceiveMessageSize = 1024 * 1024);

var connection = builder.Configuration.GetConnectionString("Default")
    ?? $"Data Source={Path.Combine(builder.Environment.ContentRootPath, "App_Data", "donkeyfire.db")}";
Directory.CreateDirectory(Path.Combine(builder.Environment.ContentRootPath, "App_Data"));
builder.Services.AddDbContextFactory<AppDbContext>(o => o.UseSqlite(connection));

// Keep the keys that encrypt sign-in cookies next to the database (the /data volume on Fly). Left in the container,
// they're lost on every deploy, which signs everyone out and breaks forms open at the time.
var keysDir = builder.Configuration["DataProtection:KeysDirectory"]
    ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(DatabaseBackup.DataSource(connection)))!, "keys");
Directory.CreateDirectory(keysDir);
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keysDir)).SetApplicationName("donkeyfire");

// Google is the only sign-in method for now. Client id/secret come from user-secrets or environment
// variables (Authentication__Google__ClientId / Authentication__Google__ClientSecret), never from source.
var googleId = builder.Configuration["Authentication:Google:ClientId"];
var googleSecret = builder.Configuration["Authentication:Google:ClientSecret"];
var googleConfigured = !string.IsNullOrWhiteSpace(googleId) && !string.IsNullOrWhiteSpace(googleSecret);
// On a public server, only allowlisted or invited accounts may sign in. Refuse to start rather than run wide open by accident.
var restrictSignIn = SignInPolicy.IsRestricted(builder.Configuration, builder.Environment);
if (googleConfigured && !builder.Environment.IsDevelopment() && restrictSignIn && SignInPolicy.ParseAllowedEmails(builder.Configuration).Count == 0)
    throw new InvalidOperationException(
        "Google sign-in is configured but nobody is allowed to sign in. Set Authentication__AllowedEmails to a comma-separated list of emails " +
        "(or Authentication__AllowAnyGoogleAccount=true to let any Google account in).");
// Behind a reverse proxy (Fly.io and similar) the app sees plain http. Trust the proxy's forwarded headers so sign-in redirects use https.
var behindProxy = builder.Configuration.GetValue("Hosting:BehindProxy", false);
if (behindProxy)
{
    builder.Services.Configure<ForwardedHeadersOptions>(o =>
    {
        o.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        o.KnownIPNetworks.Clear();
        o.KnownProxies.Clear();
    });
}
var devLoginEnabled = builder.Environment.IsDevelopment() && builder.Configuration.GetValue("Authentication:EnableDevLogin", !googleConfigured);

var auth = builder.Services.AddAuthentication(o =>
    {
        o.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
        o.DefaultChallengeScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    })
    .AddCookie(o =>
    {
        o.LoginPath = "/login";
        o.LogoutPath = "/auth/logout";
        o.ExpireTimeSpan = TimeSpan.FromDays(14);
        o.SlidingExpiration = true;
    });
if (googleConfigured)
{
    auth.AddGoogle(o =>
    {
        o.ClientId = googleId!;
        o.ClientSecret = googleSecret!;
        o.ClaimActions.MapJsonKey("picture", "picture");
        o.ClaimActions.MapJsonKey("email_verified", "email_verified");
        o.Events.OnCreatingTicket = async ctx =>
        {
            if (restrictSignIn)
            {
                var policy = ctx.HttpContext.RequestServices.GetRequiredService<SignInPolicy>();
                var verified = string.Equals(ctx.Principal!.FindFirstValue("email_verified"), "true", StringComparison.OrdinalIgnoreCase);
                if (!await policy.IsAllowedAsync(ctx.Principal!.FindFirstValue(ClaimTypes.Email), verified))
                {
                    ctx.HttpContext.Items["df:signin-denied"] = true;   // no user record is created for strangers
                    return;
                }
            }
            var provisioner = ctx.HttpContext.RequestServices.GetRequiredService<UserProvisioner>();
            var sub = ctx.Principal!.FindFirstValue(ClaimTypes.NameIdentifier)!;
            var email = ctx.Principal!.FindFirstValue(ClaimTypes.Email) ?? "";
            var user = await provisioner.UpsertAsync($"google:{sub}", email, ctx.Principal!.FindFirstValue(ClaimTypes.Name) ?? email,
                ctx.Principal!.FindFirstValue("picture"));
            ((ClaimsIdentity)ctx.Principal!.Identity!).AddClaim(new Claim(AppClaims.UserId, user.Id.ToString()));
        };
        o.Events.OnTicketReceived = ctx =>
        {
            if (ctx.HttpContext.Items.ContainsKey("df:signin-denied"))
            {
                ctx.HandleResponse();
                ctx.Response.Redirect("/login?error=not-allowed");
            }
            return Task.CompletedTask;
        };
    });
}
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();
builder.Services.AddHttpContextAccessor();

builder.Services.AddScoped<CurrentUser>();
builder.Services.AddScoped<BusinessAccess>();
builder.Services.AddScoped<UserProvisioner>();
builder.Services.AddScoped<SignInPolicy>();
builder.Services.AddHostedService<DatabaseBackupService>();
builder.Services.AddScoped<FileStorage>();
builder.Services.AddScoped<BusinessSeeder>();
builder.Services.AddScoped<BusinessMetrics>();
builder.Services.AddScoped<SalesService>();
builder.Services.AddScoped<InventoryService>();
builder.Services.AddScoped<ToastService>();
builder.Services.AddSingleton(new AuthOptionsInfo(googleConfigured, devLoginEnabled));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<AppDbContext>>().CreateDbContext();
    // Snapshot an existing database before applying new migrations, so a bad migration can be rolled back by restoring the file.
    if (builder.Configuration.GetValue("Backup:Enabled", true) && File.Exists(DatabaseBackup.DataSource(connection))
        && db.Database.GetAppliedMigrations().Any() && db.Database.GetPendingMigrations().Any())
    {
        var dir = DatabaseBackup.DefaultDirectory(builder.Configuration, connection);
        var path = await DatabaseBackup.SnapshotAsync(connection, dir, "pre-migrate");
        DatabaseBackup.Prune(dir, "pre-migrate", 10);
        app.Logger.LogInformation("Pre-migration snapshot written to {Path}", path);
    }
    db.Database.Migrate();
}

if (behindProxy) app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
if (!behindProxy) app.UseHttpsRedirection();   // behind a proxy, the proxy redirects to https

app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

app.MapStaticAssets();

app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous();

app.MapGet("/auth/login/google", (string? returnUrl) =>
    googleConfigured
        ? Results.Challenge(new AuthenticationProperties { RedirectUri = SafeReturn(returnUrl) }, [GoogleDefaults.AuthenticationScheme])
        : Results.Redirect("/login?error=google-not-configured"));

app.MapPost("/auth/logout", async (HttpContext ctx) =>
{
    await ctx.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect("/login");
}).DisableAntiforgery();

if (devLoginEnabled)
{
    // Local testing only: signs in a fixed developer account without Google.
    app.MapPost("/auth/dev-login", async (HttpContext ctx, UserProvisioner provisioner, string? returnUrl) =>
    {
        var user = await provisioner.UpsertAsync("dev:local", "dev@localhost", "Local Developer", null);
        var identity = new ClaimsIdentity([
            new Claim(ClaimTypes.NameIdentifier, user.ExternalId),
            new Claim(ClaimTypes.Name, user.DisplayName),
            new Claim(ClaimTypes.Email, user.Email),
            new Claim(AppClaims.UserId, user.Id.ToString()),
        ], CookieAuthenticationDefaults.AuthenticationScheme);
        await ctx.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(identity));
        return Results.Redirect(SafeReturn(returnUrl));
    }).DisableAntiforgery();
}

// Uploaded files (receipt photos) are only served to members of the file's business.
app.MapGet("/files/{id:int}", async (int id, bool? download, HttpContext ctx, IDbContextFactory<AppDbContext> dbf, FileStorage storage) =>
{
    if (!int.TryParse(ctx.User.FindFirstValue(AppClaims.UserId), out var uid)) return Results.Unauthorized();
    await using var db = await dbf.CreateDbContextAsync();
    var file = await db.Files.AsNoTracking().FirstOrDefaultAsync(f => f.Id == id);
    if (file is null) return Results.NotFound();
    if (await BusinessAccess.GetRoleAsync(db, file.BusinessId, uid) is null) return Results.Forbid();
    var path = storage.GetFullPath(file);
    if (!File.Exists(path)) return Results.NotFound();
    return Results.File(path, file.ContentType, download == true ? file.FileName : null, enableRangeProcessing: true);
}).RequireAuthorization();

app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

app.Run();

static string SafeReturn(string? returnUrl) =>
    !string.IsNullOrEmpty(returnUrl) && returnUrl.StartsWith('/') && !returnUrl.StartsWith("//") ? returnUrl : "/";

public record AuthOptionsInfo(bool GoogleConfigured, bool DevLoginEnabled);
