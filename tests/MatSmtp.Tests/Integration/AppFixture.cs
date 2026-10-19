using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using MatPlatform;
using MatSmtp.Api;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services.Auth;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.IdentityModel.Protocols;
using Microsoft.IdentityModel.Protocols.OpenIdConnect;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MatSmtp.Tests.Integration;

/// <summary>
/// The whole application in memory, with its own data folder (SQLite file, spool, keys, certificates) and its SMTP receiver on free ports of 127.0.0.1. Needs
/// nothing outside: no database server, no Docker.
/// </summary>
public class AppFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    /// <summary>The key of the management API (<c>MATSMTP_API_KEY</c>); without <c>MAT_API_KEY</c> it is the key of the platform endpoints too.</summary>
    public const string EngineKey = "engine-key-0123456789abcdef";

    /// <summary><c>MATSMTP_API_KEY</c> of this fixture; null: the management API is off.</summary>
    protected virtual string? ApiKey => EngineKey;

    /// <summary><c>MAT_API_KEY</c> of this fixture; null: the engine key serves the platform endpoints.</summary>
    protected virtual string? PlatformKey => null;

    /// <summary><c>MATSMTP_ADMIN_PASSWORD</c> of this fixture: a headless installation.</summary>
    protected virtual string? AdminPassword => null;

    /// <summary><c>MATIDENTITY_AUTHORITY</c> of this fixture: the sign-in through MatIdentity (one that does not need to run).</summary>
    protected virtual string? Authority => null;

    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "ms-it-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>The plain SMTP port of the receiver (STARTTLS offered).</summary>
    public int SmtpPort { get; } = FreePort();

    /// <summary>The submission port (STARTTLS offered; AUTH never without it).</summary>
    public int SubmissionPort { get; } = FreePort();

    /// <summary>The port with TLS from the first byte.</summary>
    public int TlsPort { get; } = FreePort();

    public static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    // The application reads where it lives from the environment while it starts. The test classes share one collection, so they start one after the other.
    protected override IHost CreateHost(IHostBuilder builder)
    {
        Directory.CreateDirectory(DataDir);
        Environment.SetEnvironmentVariable("MATSMTP_DATA", DataDir);
        Environment.SetEnvironmentVariable("MATSMTP__Smtp__Port", SmtpPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("MATSMTP__Smtp__SubmissionPort", SubmissionPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        Environment.SetEnvironmentVariable("MATSMTP__Smtp__TlsPort", TlsPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        // Every fixture sets every variable (also to nothing): the environment is shared by the fixtures that run one after the other.
        Environment.SetEnvironmentVariable(ManagementApiOptions.KeyVariable, ApiKey);
        Environment.SetEnvironmentVariable(MatPlatformSettings.ApiKeyVariable, PlatformKey);
        Environment.SetEnvironmentVariable("MATSMTP_ADMIN_PASSWORD", AdminPassword);
        Environment.SetEnvironmentVariable("MATSMTP_ADMIN_EMAIL", null);
        Environment.SetEnvironmentVariable(MatPlatformSettings.AuthorityVariable, Authority);
        Environment.SetEnvironmentVariable(MatPlatformSettings.ClientIdVariable, null);
        Environment.SetEnvironmentVariable(MatPlatformSettings.ClientSecretVariable, Authority is null ? null : "test-secret");
        Environment.SetEnvironmentVariable(MatPlatformSettings.InternalUrlVariable, null);
        return base.CreateHost(builder);
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        if (Authority is not { } authority)
        {
            return;
        }

        // A fixed OpenID configuration: no MatIdentity has to run, the tests look at the redirects.
        var configuration = new OpenIdConnectConfiguration
        {
            Issuer = authority,
            AuthorizationEndpoint = authority + "/connect/authorize",
            TokenEndpoint = authority + "/connect/token",
            EndSessionEndpoint = authority + "/connect/logout",
        };
        builder.ConfigureTestServices(services =>
        {
            services.PostConfigure<OpenIdConnectOptions>(MatIdentitySignIn.Scheme, options =>
            {
                options.Configuration = configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            });
            services.PostConfigure<JwtBearerOptions>(MatApiAccess.BearerScheme, options =>
            {
                options.Configuration = configuration;
                options.ConfigurationManager = new StaticConfigurationManager<OpenIdConnectConfiguration>(configuration);
            });
        });
    }

    /// <summary>A client that sends the engine key (and follows no redirect).</summary>
    public HttpClient ApiClient(string? key = EngineKey)
    {
        var client = CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        if (key is not null)
        {
            client.DefaultRequestHeaders.Add("X-Api-Key", key);
        }

        return client;
    }

    public Task InitializeAsync() => Task.CompletedTask;

    async Task IAsyncLifetime.DisposeAsync()
    {
        await base.DisposeAsync();
        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(DataDir, recursive: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // a temporary folder that stays behind is not worth failing a test run
        }
    }

    // ------------------------------------------------------------------------------------------------ what the tests start from

    /// <summary>Runs something with the services of a request scope (a context, the services of the application).</summary>
    public async Task<T> WithScopeAsync<T>(Func<IServiceProvider, Task<T>> action)
    {
        using var scope = Services.CreateScope();
        return await action(scope.ServiceProvider);
    }

    public Task WithScopeAsync(Func<IServiceProvider, Task> action) => WithScopeAsync<int>(async sp =>
    {
        await action(sp);
        return 0;
    });

    public Task<User> CreateUserAsync(string email, string password, UserRole role)
        => WithScopeAsync(sp => sp.GetRequiredService<UserService>().CreateAsync(email, "Test " + role, role, password));

    /// <summary>Until there is an administrator the only page is the setup; most tests start with one.</summary>
    public async Task EnsureAdminAsync()
    {
        // the first request starts the host (and with it the database)
        _ = Services;
        if (!await WithScopeAsync(sp => sp.GetRequiredService<MsDbContext>().User.AnyAsync(u => u.Role == UserRole.Admin)))
        {
            await CreateUserAsync("admin@example.test", "Admin-Passwort-2026", UserRole.Admin);
        }
    }

    /// <summary>Waits until <paramref name="condition"/> holds (the spooler works in the background), at most <paramref name="seconds"/>.</summary>
    public async Task<T> EventuallyAsync<T>(Func<IServiceProvider, Task<T>> read, Func<T, bool> condition, int seconds = 20)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        T value;
        do
        {
            value = await WithScopeAsync(read);
            if (condition(value))
            {
                return value;
            }

            await Task.Delay(100);
        }
        while (DateTime.UtcNow < deadline);

        return value;
    }

    // ------------------------------------------------------------------------------------------------ talking to the pages

    public HttpClient NewClient(bool followRedirects = false)
        => CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = followRedirects, HandleCookies = true });

    private static readonly Regex Token = new("name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"", RegexOptions.Compiled);

    /// <summary>The anti-forgery token of the form on a page (the cookie that belongs to it is kept by the client).</summary>
    public static async Task<string> TokenAsync(HttpClient client, string url)
    {
        var html = await client.GetStringAsync(url);
        return Token.Match(html) is { Success: true } m ? WebUtility.HtmlDecode(m.Groups[1].Value) : throw new InvalidOperationException("No form on " + url);
    }

    /// <summary>Posts a form the way a browser does: loads the page (for the token), then posts the fields.</summary>
    public static async Task<HttpResponseMessage> PostFormAsync(HttpClient client, string pageUrl, IDictionary<string, string> fields, string? postUrl = null)
    {
        var token = await TokenAsync(client, pageUrl);
        var body = new Dictionary<string, string>(fields) { ["__RequestVerificationToken"] = token };
        return await client.PostAsync(postUrl ?? pageUrl, new FormUrlEncodedContent(body));
    }

    /// <summary>The path a redirect points to (the address may be relative).</summary>
    public static string LocationPath(HttpResponseMessage response) => new Uri(new Uri("http://localhost"), response.Headers.Location!).AbsolutePath;

    /// <summary>The text of a page as a person reads it: Razor writes "ü" as an HTML entity.</summary>
    public static async Task<string> TextAsync(HttpResponseMessage response) => WebUtility.HtmlDecode(await response.Content.ReadAsStringAsync());

    public static async Task<string> TextAsync(HttpClient client, string url) => WebUtility.HtmlDecode(await client.GetStringAsync(url));

    public static async Task LoginAsync(HttpClient client, string email, string password)
    {
        var response = await PostFormAsync(client, "/Account/Login", new Dictionary<string, string> { ["Input.Email"] = email, ["Input.Password"] = password });
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }
}

[CollectionDefinition(Name)]
public sealed class IntegrationCollection
{
    public const string Name = "integration";
}

/// <summary>Without <c>MATSMTP_API_KEY</c> and <c>MAT_API_KEY</c>: the management API answers 503, the platform endpoints 401.</summary>
public sealed class NoKeyFixture : AppFixture
{
    protected override string? ApiKey => null;
}

/// <summary>A headless installation: <c>MATSMTP_ADMIN_PASSWORD</c> makes the administrator <c>admin</c>.</summary>
public sealed class SeededFixture : AppFixture
{
    public const string Password = "Seeded-Admin-2026";

    protected override string? AdminPassword => Password;
}

/// <summary>With <c>MATIDENTITY_AUTHORITY</c> (a MatIdentity that does not need to run) and its own <c>MAT_API_KEY</c>.</summary>
public sealed class IdentityFixture : AppFixture
{
    public const string IdentityAuthority = "http://identity.test";

    public const string PlatformApiKey = "platform-key-0123456789abcdef";

    protected override string? Authority => IdentityAuthority;

    protected override string? PlatformKey => PlatformApiKey;
}
