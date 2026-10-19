using System.Globalization;
using MatPlatform;
using MatSmtp.Api;
using MatSmtp.Configuration;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Auth;
using MatSmtp.Services.Backup;
using MatSmtp.Platform;
using MatSmtp.Services.Relay;
using MatSmtp.UiKit;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Localization;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;

// The image checks itself (HEALTHCHECK in the Dockerfile): it needs no curl.
if (args.Contains("--healthcheck"))
{
    return await HealthCheck.RunAsync();
}

var dataDir = Environment.GetEnvironmentVariable("MATSMTP_DATA") is { Length: > 0 } fromEnv
    ? fromEnv
    : Path.Combine(Directory.GetCurrentDirectory(), "data");
Directory.CreateDirectory(dataDir);
var config = new ConfigStore(dataDir);
Fmt.Configure(config.Current.Server.TimeZone);

var builder = WebApplication.CreateBuilder(args);

// Port 2525; ASPNETCORE_URLS still wins (tests, a reverse proxy on the same machine).
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ASPNETCORE_URLS")))
{
    builder.WebHost.UseUrls($"http://+:{Environment.GetEnvironmentVariable("MATSMTP_WEB_PORT") ?? "2525"}");
}

builder.WebHost.ConfigureKestrel(o => o.Limits.MaxRequestBodySize = 100L * 1024 * 1024);

builder.Services.AddSingleton(config);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpContextAccessor();
builder.Services.AddMemoryCache();

// Sessions live in the database; the keys that protect the cookie (and secrets) live in the data volume, so a restart keeps everybody signed in.
builder.Services.AddDataProtection()
    .SetApplicationName("MatSMTP")
    .PersistKeysToFileSystem(new DirectoryInfo(Path.Combine(dataDir, "keys")));

builder.Services.AddDbContextFactory<MsDbContext>(options => options
    // No automatic retries: a save that fails is shown to the person, not repeated behind their back.
    .UseSqlite(config.ConnectionString())
    // Soft-deleted accounts, webhooks and relay users are refused by the pages before anything refers to them, so the warning about filtered required ends is noise.
    .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.CoreEventId.PossibleIncorrectRequiredNavigationWithQueryFilterInteractionWarning)));
builder.Services.AddScoped(sp => sp.GetRequiredService<IDbContextFactory<MsDbContext>>().CreateDbContext());

builder.Services.AddSingleton<ICurrentUser, CurrentUser>();
builder.Services.AddSingleton<IconSprite>();
builder.Services.AddSingleton<SecretProtector>();
builder.Services.AddSingleton<SessionCache>();
builder.Services.AddSingleton<TwoFactorTicket>();
builder.Services.AddSingleton<ActivityLogger>();
builder.Services.AddSingleton<SetupState>();
builder.Services.AddScoped<SessionCookieEvents>();
builder.Services.AddScoped<SignInService>();
builder.Services.AddScoped<TwoFactorService>();
builder.Services.AddScoped<UserService>();
builder.Services.AddScoped<PublicUrl>();
builder.Services.AddScoped<ShareService>();
builder.Services.AddSingleton<SpoolStore>();
builder.Services.AddSingleton<SpoolSignal>();
builder.Services.AddSingleton<CertificateService>();
builder.Services.AddScoped<RelayService>();
builder.Services.AddScoped<RelayUserService>();
builder.Services.AddScoped<RelayStatusService>();
builder.Services.AddScoped<RelaySettingsService>();
builder.Services.AddScoped<AccountService>();
builder.Services.AddScoped<MessageService>();
builder.Services.AddScoped<RuleService>();
builder.Services.AddScoped<WebhookAdminService>();
builder.Services.AddScoped<ActivityService>();
builder.Services.AddScoped<DeliveryService>();
builder.Services.AddScoped<WebhookService>();
builder.Services.AddHttpClient(WebhookService.HttpClientName);
builder.Services.AddSingleton<SmtpListener>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<SmtpListener>());
builder.Services.AddHostedService<DeliveryWorker>();
builder.Services.AddScoped<BackupStoreFactory>();
builder.Services.AddScoped<DatabaseArchive>();
builder.Services.AddScoped<BackupService>();
builder.Services.AddScoped<BackupAdminService>();
builder.Services.AddHostedService<BackupScheduler>();
builder.Services.AddHostedService<MaintenanceService>();
builder.Services.AddScoped<SystemService>();
builder.Services.AddScoped<NavCounts>();
builder.Services.AddScoped<ShellFactory>();

builder.Services.AddAntiforgery(o => o.HeaderName = "RequestVerificationToken");

// The management API of the engine (/api/v1, for Mattiverse): its own key MATSMTP_API_KEY.
var managementApi = ManagementApiOptions.From(builder.Configuration);
builder.Services.AddSingleton(managementApi);

// The Mat platform contract (MatDesign docs/PLATFORM.md): /api, /api/health, /api/state, /api/configure, /api/actions (the package MatPlatform, a copy in
// MatPlatform/; SmtpPlatform describes MatSMTP and supplies the values), and the optional sign-in through MatIdentity (MATIDENTITY_*). The key of the platform
// endpoints is MAT_API_KEY; without it the engine key MATSMTP_API_KEY serves there too, so a caller that only knows the engine key can read the tiles.
if (string.IsNullOrWhiteSpace(builder.Configuration[MatPlatformSettings.ApiKeyVariable]) && managementApi.Key is { } engineKey)
{
    builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> { [MatPlatformSettings.ApiKeyVariable] = engineKey });
}

var platform = builder.AddMatPlatform<SmtpPlatform>(SmtpPlatform.Describe);

builder.Services.AddAuthentication(SignInService.CookieScheme)
    .AddCookie(SignInService.CookieScheme, options =>
    {
        options.Cookie.Name = ".MatSMTP";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.Cookie.SecurePolicy = config.Current.Server.ForceSecureCookie ? CookieSecurePolicy.Always : CookieSecurePolicy.SameAsRequest;
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.SlidingExpiration = false;
        options.EventsType = typeof(SessionCookieEvents);
    })
    // Sign-in through MatIdentity, only when MATIDENTITY_AUTHORITY is set (nothing changes without it).
    .AddMatIdentitySignIn<SmtpIdentityUsers>(platform, options =>
    {
        options.CookieScheme = SignInService.CookieScheme;
        options.LoginPath = "/Account/Login";
        options.AccessDeniedPath = "/Account/AccessDenied";
    });

builder.Services.AddAuthorizationBuilder()
    // Everything needs a signed-in user (administrator or user) unless a page says otherwise: a new page is closed by default.
    .SetFallbackPolicy(new Microsoft.AspNetCore.Authorization.AuthorizationPolicyBuilder(SignInService.CookieScheme)
        .RequireAuthenticatedUser().RequireRole(nameof(UserRole.Admin), nameof(UserRole.User)).Build())
    .AddPolicy("Admin", p => p.AddAuthenticationSchemes(SignInService.CookieScheme).RequireRole(nameof(UserRole.Admin)))
    .AddPolicy("SignedIn", p => p.AddAuthenticationSchemes(SignInService.CookieScheme).RequireAuthenticatedUser());

builder.Services.AddRazorPages(options =>
{
    options.Conventions.AuthorizeFolder("/Admin", "Admin");
    options.Conventions.AuthorizeFolder("/Rules", "Admin");
    options.Conventions.AuthorizeFolder("/Accounts", "Admin");
    options.Conventions.AuthorizeFolder("/RelayUsers", "Admin");
    options.Conventions.AuthorizeFolder("/Webhooks", "Admin");
    options.Conventions.AuthorizeFolder("/Tools", "Admin");
    options.Conventions.AuthorizeFolder("/Account", "SignedIn");
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/TwoFactor");
    options.Conventions.AllowAnonymousToPage("/Account/Setup");
    options.Conventions.AllowAnonymousToPage("/Account/AccessDenied");
    options.Conventions.AllowAnonymousToPage("/Offline");
    options.Conventions.AllowAnonymousToFolder("/Share");
})
// A non-nullable string is not "required" by itself: the pages say what is required (the star, the message, the check).
.AddMvcOptions(options => options.SuppressImplicitRequiredAttributeForNonNullableReferenceTypes = true);

// Numbers in forms and in the query string are always written the invariant way ("12.5"); the pages show them German with Fmt.
builder.Services.Configure<RequestLocalizationOptions>(options =>
{
    options.DefaultRequestCulture = new RequestCulture(CultureInfo.InvariantCulture);
    options.SupportedCultures = [CultureInfo.InvariantCulture];
    options.SupportedUICultures = [CultureInfo.InvariantCulture];
});

builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto | ForwardedHeaders.XForwardedHost;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});

var app = builder.Build();
var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");

await DatabaseInitializer.InitializeAsync(app.Services, config, logger);

// A headless installation (Mattiverse): the administrator from the environment, the setup page is skipped.
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<UserService>().SeedAdminAsync(
        builder.Configuration["MATSMTP_ADMIN_PASSWORD"], builder.Configuration["MATSMTP_ADMIN_EMAIL"]);
}

app.UseForwardedHeaders();
app.UseRequestLocalization();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

// A page that is not there (or not for you) is an empty 404/403 for the pipeline; the Error page says it in words.
app.UseStatusCodePagesWithReExecute("/Error", "?code={0}");

app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"] = "no-referrer";
    headers["X-Frame-Options"] = "SAMEORIGIN";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    headers["Content-Security-Policy"] =
        "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' data: blob:; font-src 'self'; connect-src 'self'; manifest-src 'self'; worker-src 'self'; " +
        "frame-src 'self' blob:; object-src 'self' blob:; base-uri 'self'; form-action 'self'; frame-ancestors 'self'";
    await next();
});

// The web app manifest has its own type; the service worker must never be cached by the browser, or an update would wait for days.
var contentTypes = new FileExtensionContentTypeProvider();
contentTypes.Mappings[".webmanifest"] = "application/manifest+json";
app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = contentTypes,
    OnPrepareResponse = ctx =>
    {
        if (ctx.File.Name is "sw.js" or "manifest.webmanifest")
        {
            ctx.Context.Response.Headers.CacheControl = "no-cache";
        }
    },
});
// The rate limit of the anonymous platform endpoints /api and /api/health.
app.UseRateLimiter();
app.UseAuthentication();

// While the first start is not done the only page is the one that makes the first administrator; the APIs and the sign-in through MatIdentity answer anyway.
app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    if (!path.StartsWithSegments("/Account/Setup") && !path.StartsWithSegments("/health") && !path.StartsWithSegments("/api") && !path.StartsWithSegments("/css")
        && !path.StartsWithSegments("/js") && !path.StartsWithSegments("/ui") && !path.StartsWithSegments("/img") && !path.StartsWithSegments("/Offline")
        && !path.Value!.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) && !path.Value.EndsWith(".webmanifest", StringComparison.OrdinalIgnoreCase)
        && path.Value != "/sw.js"
        && !path.StartsWithSegments(MatIdentitySignIn.CallbackPath) && !path.StartsWithSegments(MatIdentitySignIn.SignedOutCallbackPath)
        && !await context.RequestServices.GetRequiredService<SetupState>().IsDoneAsync(context.RequestServices, context.RequestAborted))
    {
        context.Response.Redirect("/Account/Setup");
        return;
    }

    await next();
});

app.UseAuthorization();

// The plain check of older scripts; the platform's /api/health says more (and is what the image checks).
app.MapGet("/health", async (SystemService system) => await system.CanConnectAsync() ? Results.Text("ok") : Results.Text("down", statusCode: 503)).AllowAnonymous();

app.MapPost("/api/theme", async (HttpContext http, IAntiforgery antiforgery, UserService users, SignInService signIn, ICurrentUser user) =>
{
    await antiforgery.ValidateRequestAsync(http);
    var theme = http.Request.Form["theme"].ToString();
    if (theme is not ("system" or "light" or "dark") || user.UserId is not { } id)
    {
        return Results.BadRequest();
    }

    await signIn.RefreshAsync(http, await users.SetThemeAsync(id, theme));
    return Results.NoContent();
}).RequireAuthorization("SignedIn");

// The content of a message as it was received (and rewritten), for a mail program. Users and administrators only (the fallback policy).
app.MapGet("/messages/{id:long}/eml", async (long id, HttpContext http, MessageService messages, SpoolStore spool) =>
{
    var message = await messages.FindAsync(id, http.RequestAborted);
    if (message is null || !spool.Exists(message.StorageKey))
    {
        return Results.NotFound();
    }

    http.Response.Headers.CacheControl = "private, no-store";
    return Results.File(spool.OpenRead(message.StorageKey!), "message/rfc822", $"nachricht-{id}.eml");
});

// The management API of the engine (/api/v1, X-Api-Key = MATSMTP_API_KEY) and the platform contract next to it: /api and /api/health anonymous
// and rate limited, /api/state, /api/configure and /api/actions with X-Api-Key (MAT_API_KEY, else MATSMTP_API_KEY) or a token of MatIdentity.
app.MapManagementApi();
app.MapMatPlatform();

app.MapRazorPages();

await app.RunAsync();
return 0;

/// <summary>The command <c>dotnet MatSmtp.dll --healthcheck</c>: asks <c>/api/health</c> of the running site; exit code 0 means ok or degraded (200), 1 down (503) or no answer.</summary>
public static class HealthCheck
{
    public static async Task<int> RunAsync()
    {
        var port = Environment.GetEnvironmentVariable("MATSMTP_WEB_PORT") ?? "2525";
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
            using var response = await client.GetAsync($"http://localhost:{port}/api/health");
            return response.IsSuccessStatusCode ? 0 : 1;
        }
        catch (HttpRequestException)
        {
            return 1;
        }
        catch (TaskCanceledException)
        {
            return 1;
        }
    }
}

/// <summary>Makes the entry point visible to the tests (<c>WebApplicationFactory&lt;Program&gt;</c>).</summary>
public partial class Program;
