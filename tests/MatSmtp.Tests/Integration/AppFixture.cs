using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using MatSmtp.Data;
using MatSmtp.Data.Entities;
using MatSmtp.Services.Auth;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace MatSmtp.Tests.Integration;

/// <summary>
/// The whole application in memory, with its own data folder (SQLite file, spool, keys, certificates) and its SMTP receiver on free ports of 127.0.0.1. Needs
/// nothing outside: no database server, no Docker.
/// </summary>
public sealed class AppFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    public string DataDir { get; } = Path.Combine(Path.GetTempPath(), "ms-it-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>The plain SMTP port of the receiver (STARTTLS offered).</summary>
    public int SmtpPort { get; } = FreePort();

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
        Environment.SetEnvironmentVariable("MATSMTP__Smtp__SubmissionPort", "0");
        Environment.SetEnvironmentVariable("MATSMTP__Smtp__TlsPort", TlsPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        return base.CreateHost(builder);
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
