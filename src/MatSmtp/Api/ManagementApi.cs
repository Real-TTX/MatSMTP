using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MatSmtp.Configuration;
using MatSmtp.Data.Entities;
using MatSmtp.Services;
using MatSmtp.Services.Auth;
using MatSmtp.Services.Relay;
using MatSmtp.UiKit;

namespace MatSmtp.Api;

/// <summary>The key of the management API: <c>MATSMTP_API_KEY</c> from the environment; null = the API answers 503.</summary>
public sealed record ManagementApiOptions(string? Key)
{
    public const string KeyVariable = "MATSMTP_API_KEY";

    public static ManagementApiOptions From(IConfiguration configuration)
        => new(string.IsNullOrWhiteSpace(configuration[KeyVariable]) ? null : configuration[KeyVariable]!.Trim());
}

/// <summary>
/// The management API of the engine MatSMTP for Mattiverse (<c>/api/v1</c>, Mattiverse docs/ENGINES.md): the status with the port applications send to,
/// the relay users of the applications (their SMTP AUTH logins) and the two settings of a headless installation. The header <c>X-Api-Key</c> is compared in
/// constant time with <c>MATSMTP_API_KEY</c>; without a key every call answers 503, a wrong or missing key 401, always <c>{"error": "..."}</c>.
/// The cookie of a session counts for nothing here. This is the web layer: it translates HTTP to the services.
/// </summary>
public static class ManagementApi
{
    public const int ApiVersion = 1;

    public const string HeaderName = "X-Api-Key";

    /// <summary>camelCase; a null is written ("lastUsedUtc": null), the password of <see cref="RelayUserSaved"/> is left out instead.</summary>
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A relay user as the API lists it: never a secret.</summary>
    public sealed record RelayUserView(string Id, string Username, string? DisplayName, IReadOnlyList<string> AllowedSenders, string? ManagedBy, DateTimeOffset? LastUsedUtc);

    /// <summary>The answer of <c>PUT /relay-users/{id}</c>: the password only when it was made now.</summary>
    public sealed record RelayUserSaved(string Id, string Username, [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? Password, bool Created);

    public sealed record RelayUserBody(string? DisplayName, List<string>? AllowedSenders, string? ManagedBy, bool? RenewSecret);

    public sealed record SettingsBody(bool? SetupCompleted, string? DefaultSenderDomain);

    public sealed record SettingsView(bool SetupCompleted, string DefaultSenderDomain);

    public static RouteGroupBuilder MapManagementApi(this IEndpointRouteBuilder app)
    {
        var api = app.MapGroup("/api/v1").AllowAnonymous().AddEndpointFilter(RequireKeyAsync);

        api.MapGet("/status", (ConfigStore config) =>
        {
            var smtp = config.Current.Smtp;
            var (port, tls) = SendPort(smtp);
            return Results.Json(new
            {
                ok = true,
                app = "matsmtp",
                version = AppVersion.Current,
                apiVersion = ApiVersion,
                smtpPort = port,
                smtpTls = tls,
                smtpEnabled = smtp.Enabled,
            }, Json);
        });

        api.MapGet("/relay-users", async (RelayUserService relayUsers, CancellationToken ct)
            => Results.Json((await relayUsers.ListApiAsync(ct)).Select(View), Json));

        api.MapPut("/relay-users/{id}", async (string id, HttpContext http, RelayUserService relayUsers, ActivityLogger log, ICurrentUser current, CancellationToken ct) =>
        {
            if (await ReadAsync<RelayUserBody>(http, ct) is not { } body)
            {
                return Error(400, "The body must be JSON: {\"displayName\", \"allowedSenders\", \"managedBy\", \"renewSecret\"}.");
            }

            var result = await relayUsers.UpsertApiAsync(id, new RelayUserApiInput(body.DisplayName, body.AllowedSenders, body.ManagedBy, body.RenewSecret == true), ct);
            if (result.Error is not null)
            {
                return Error(result.Status, result.Error);
            }

            var user = result.User!;
            using (current.RunAs(null, Caller(user.ManagedBy)))
            {
                var what = result.Created ? "angelegt" : result.Password is null ? "geändert" : "geändert, neues Passwort";
                await log.LogAsync($"Zugang über die API {what}", nameof(RelayUser), user.Id, user.UserName);
            }

            return Results.Json(new RelayUserSaved(user.ApiId!, user.UserName, result.Password, result.Created), Json, statusCode: result.Status);
        });

        api.MapDelete("/relay-users/{id}", async (string id, string? managedBy, RelayUserService relayUsers, ActivityLogger log, ICurrentUser current, CancellationToken ct) =>
        {
            var result = await relayUsers.DeleteApiAsync(id, managedBy, ct);
            if (result.Error is not null)
            {
                return Error(result.Status, result.Error);
            }

            using (current.RunAs(null, Caller(managedBy)))
            {
                await log.LogAsync("Zugang über die API gelöscht", nameof(RelayUser), result.User!.Id, result.User.UserName);
            }

            return Results.NoContent();
        });

        api.MapGet("/settings", async (HttpContext http, SetupState setup, ConfigStore config, CancellationToken ct)
            => Results.Json(new SettingsView(await setup.IsDoneAsync(http.RequestServices, ct), config.Current.Smtp.DefaultSenderDomain), Json));

        api.MapPut("/settings", async (HttpContext http, SetupState setup, ConfigStore config, RelaySettingsService settings, ActivityLogger log, ICurrentUser current, CancellationToken ct) =>
        {
            if (await ReadAsync<SettingsBody>(http, ct) is not { } body)
            {
                return Error(400, "The body must be JSON: {\"setupCompleted\", \"defaultSenderDomain\"}.");
            }

            if (body.DefaultSenderDomain is not null && !RelaySettingsService.IsValidDomain(body.DefaultSenderDomain.Trim().TrimStart('@')))
            {
                return Error(400, "defaultSenderDomain must be a domain like example.com, or \"\".");
            }

            if (body.DefaultSenderDomain is not null)
            {
                settings.SetDefaultSenderDomain(body.DefaultSenderDomain);
            }

            if (body.SetupCompleted is { } completed)
            {
                setup.SetCompleted(completed);
            }

            using (current.RunAs(null, Caller(null)))
            {
                await log.LogAsync("Einstellungen über die API geändert", "Settings", null,
                    $"setupCompleted={body.SetupCompleted?.ToString() ?? "-"}, defaultSenderDomain={body.DefaultSenderDomain ?? "-"}");
            }

            return Results.Json(new SettingsView(await setup.IsDoneAsync(http.RequestServices, ct), config.Current.Smtp.DefaultSenderDomain), Json);
        });

        return api;
    }

    /// <summary>
    /// The port applications INSIDE the Docker network send to (the container port, not a published one) and how: the SMTP port (2025; else the
    /// submission port) without TLS when AUTH without TLS is allowed from the private networks (the default: the applications sit in the same Docker
    /// network and cannot check a self-signed certificate), else with STARTTLS; the TLS port only when nothing else listens.
    /// </summary>
    public static (int Port, string Tls) SendPort(SmtpConfig smtp)
    {
        var port = smtp.Port > 0 ? smtp.Port : smtp.SubmissionPort;
        if (port <= 0)
        {
            return (Math.Max(0, smtp.TlsPort), "tls");
        }

        return (port, smtp.AllowPlainAuth || !string.IsNullOrWhiteSpace(smtp.PlainAuthNetworks) ? "none" : "starttls");
    }

    private static RelayUserView View(RelayUser u) => new(
        u.ApiId!,
        u.UserName,
        u.Description,
        Core.Relay.AddressPattern.Split(u.AllowedSenders),
        u.ManagedBy,
        u.LastUsedDate is { } used ? new DateTimeOffset(DateTime.SpecifyKind(used, DateTimeKind.Utc)) : null);

    private static string Caller(string? managedBy) => "API" + (string.IsNullOrEmpty(managedBy) ? "" : $" ({managedBy})");

    private static IResult Error(int status, string message) => Results.Json(new { error = message }, Json, statusCode: status);

    private static async Task<T?> ReadAsync<T>(HttpContext http, CancellationToken ct)
        where T : class
    {
        try
        {
            return await JsonSerializer.DeserializeAsync<T>(http.Request.Body, Json, ct);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>503 without a key, 401 for a wrong or missing one; the key is compared in constant time.</summary>
    private static async ValueTask<object?> RequireKeyAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        var http = context.HttpContext;
        var key = http.RequestServices.GetRequiredService<ManagementApiOptions>().Key;
        if (key is null)
        {
            return Error(503, $"The management API is off: set {ManagementApiOptions.KeyVariable}.");
        }

        var given = http.Request.Headers[HeaderName].ToString();
        if (given.Length == 0 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(Encoding.UTF8.GetBytes(given)), SHA256.HashData(Encoding.UTF8.GetBytes(key))))
        {
            return Error(401, $"A valid {HeaderName} is required.");
        }

        return await next(context);
    }
}
